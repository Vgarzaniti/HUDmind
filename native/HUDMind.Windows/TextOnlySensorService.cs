using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using HUDMind.Core.AI;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace HUDMind.Windows;

/// <summary>
/// Lazy Windows 11 WinRT OCR (~25-60ms CPU, 0% GPU) + pluggable <see cref="IGameTelemetrySensor"/> aggregator.
/// Invoked ONLY for text-only ("blind") AI models and optional proactive Live menu gating.
/// </summary>
public sealed class TextOnlySensorService : ITextOnlySensorService, IDisposable
{
    private readonly OcrEngine? _ocr;
    private readonly List<IGameTelemetrySensor> _sensors;
    private readonly SemaphoreSlim _ocrGate = new(1, 1);
    private readonly object _stateLock = new();
    private Dictionary<string, string> _prevZones = new(StringComparer.OrdinalIgnoreCase);
    private ulong _cachedFrameSig;
    private int _cachedFrameLen;
    private long _cachedOcrAtTick;
    private Dictionary<string, string>? _cachedOcrZones;

    public bool HasOcrEngine => _ocr is not null;
    public string OcrLanguageTag { get; }

    public TextOnlySensorService(
        LoopbackGsiService? gsiService = null,
        IEnumerable<IGameTelemetrySensor>? additionalSensors = null,
        int telemetryWsPort = HUDMind.Core.Overlay.InGameOverlayBridgeProtocol.DefaultTelemetryWsPort,
        bool telemetryWsEnabled = true)
    {
        _sensors =
        [
            new LoopbackWebSocketTelemetrySensor(telemetryWsPort, telemetryWsEnabled),
            new RocketLeagueLogSensor(),
            new ValorantLogSensor(),
            new LeagueLiveClientSensor()
        ];
        if (gsiService is not null)
            _sensors.Add(new ValveGsiTelemetrySensor(gsiService));
        if (additionalSensors is not null)
            _sensors.AddRange(additionalSensors);

        try
        {
            var available = OcrEngine.AvailableRecognizerLanguages;
            OcrEngine? picked = OcrEngine.TryCreateFromUserProfileLanguages();
            if (picked is null)
            {
                var esLang = available.FirstOrDefault(l => l.LanguageTag.StartsWith("es", StringComparison.OrdinalIgnoreCase));
                var enLang = available.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                var target = esLang ?? enLang ?? available.FirstOrDefault();
                if (target is not null)
                    picked = OcrEngine.TryCreateFromLanguage(target);
            }
            _ocr = picked;
            OcrLanguageTag = picked?.RecognizerLanguage.LanguageTag ?? "none";
        }
        catch
        {
            _ocr = null;
            OcrLanguageTag = "none";
        }
    }

    public void RegisterSensor(IGameTelemetrySensor sensor)
    {
        lock (_stateLock)
            _sensors.Add(sensor);
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            _prevZones.Clear();
            _cachedOcrZones = null;
            _cachedOcrAtTick = 0;
        }
    }

    public static string ClassifyPhase(IReadOnlyDictionary<string, string> zones, string logSummary) =>
        HudPhaseClassifier.ClassifyPhase(zones, logSummary);

    public Task<string> ReadTelemetryAsync(GameProfile profile, CancellationToken ct = default) =>
        ReadMatchingTelemetryAsync(profile, ct);

    public async Task<HudSensorSnapshot> ExtractAsync(byte[] jpegBytes, GameProfile profile, CancellationToken ct = default)
    {
        long t0 = Stopwatch.GetTimestamp();
        ulong sig = ComputeFastFrameSignature(jpegBytes);
        Dictionary<string, string>? cachedZones = null;
        lock (_stateLock)
        {
            if (_cachedOcrZones is not null &&
                _cachedFrameLen == jpegBytes.Length &&
                _cachedFrameSig == sig &&
                _cachedOcrAtTick > 0 &&
                Stopwatch.GetElapsedTime(_cachedOcrAtTick).TotalMilliseconds < 280.0)
            {
                cachedZones = _cachedOcrZones;
            }
        }

        Task<Dictionary<string, string>> ocrTask = cachedZones is not null
            ? Task.FromResult(cachedZones)
            : RunWinRtOcrZonesAsync(jpegBytes, ct);
        var logTask = ReadMatchingTelemetryAsync(profile, ct);
        await Task.WhenAll(ocrTask, logTask).ConfigureAwait(false);

        var zones = await ocrTask.ConfigureAwait(false);
        string logSummary = await logTask.ConfigureAwait(false);
        double elapsedMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        if (cachedZones is null)
        {
            lock (_stateLock)
            {
                _cachedFrameSig = sig;
                _cachedFrameLen = jpegBytes.Length;
                _cachedOcrAtTick = Stopwatch.GetTimestamp();
                _cachedOcrZones = zones;
            }
        }

        string phase = HudPhaseClassifier.ClassifyPhase(zones, logSummary);
        string delta = ComputeZoneDelta(zones);
        string zonesSummary = FormatZones(zones);
        bool hasUseful = zones.Count > 0 || !string.IsNullOrWhiteSpace(logSummary);

        return new HudSensorSnapshot(
            Phase: phase,
            OcrZonesSummary: zonesSummary,
            LogSummary: logSummary,
            DeltaNote: delta,
            OcrMs: elapsedMs,
            HasUsefulData: hasUseful
        );
    }

    private static ulong ComputeFastFrameSignature(byte[] bytes)
    {
        if (bytes.Length == 0)
            return 0;
        ulong hash = 14695981039346656037UL;
        int step = Math.Max(1, bytes.Length / 96);
        for (int i = 0; i < bytes.Length; i += step)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }
        hash ^= (ulong)bytes.Length;
        return hash;
    }

    private async Task<string> ReadMatchingTelemetryAsync(GameProfile profile, CancellationToken ct)
    {
        List<IGameTelemetrySensor> matching;
        lock (_stateLock)
            matching = _sensors.Where(s => s.Supports(profile)).ToList();

        if (matching.Count == 0)
            return string.Empty;

        var results = await Task.WhenAll(matching.Select(s => ReadSafeAsync(s, profile, ct))).ConfigureAwait(false);
        return string.Join(" ; ", results.Where(r => !string.IsNullOrWhiteSpace(r)));
    }

    private static async Task<string> ReadSafeAsync(IGameTelemetrySensor sensor, GameProfile profile, CancellationToken ct)
    {
        try { return await sensor.ReadAsync(profile, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return string.Empty; }
    }

    private async Task<Dictionary<string, string>> RunWinRtOcrZonesAsync(byte[] jpegBytes, CancellationToken ct)
    {
        var zones = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (_ocr is null || jpegBytes.Length < 64)
            return new Dictionary<string, string>();

        await _ocrGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var ras = new InMemoryRandomAccessStream();
            await ras.WriteAsync(jpegBytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
            ras.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);
            double imgW = Math.Max(1.0, decoder.PixelWidth);
            double imgH = Math.Max(1.0, decoder.PixelHeight);

            using var softwareBmp = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct).ConfigureAwait(false);

            var ocrRes = await _ocr.RecognizeAsync(softwareBmp).AsTask(ct).ConfigureAwait(false);
            foreach (var line in ocrRes.Lines)
            {
                var rawText = line.Text?.Trim();
                if (string.IsNullOrWhiteSpace(rawText) || rawText.Length > 64 || line.Words.Count == 0)
                    continue;

                var safeText = CoachingPersonaBuilder.SanitizeField(rawText, 64);
                if (string.IsNullOrWhiteSpace(safeText))
                    continue;

                var first = line.Words[0].BoundingRect;
                var last = line.Words[^1].BoundingRect;
                double cx = ((first.X + (last.X + last.Width)) * 0.5) / imgW;
                double cy = (first.Y + first.Height * 0.5) / imgH;
                string zone = MapZone(cx, cy);

                if (!zones.TryGetValue(zone, out var list))
                {
                    list = [];
                    zones[zone] = list;
                }
                if (list.Count < 4)
                    list.Add(safeText);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* Keep sensor resilient on transient decode errors */ }
        finally
        {
            _ocrGate.Release();
        }

        return zones.ToDictionary(kv => kv.Key, kv => string.Join(" ", kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static string MapZone(double cx, double cy)
    {
        string row = cy < 0.25 ? "TOP" : (cy > 0.72 ? "BOT" : "MID");
        string col = cx < 0.30 ? "LEFT" : (cx > 0.70 ? "RIGHT" : "CENTER");
        return (row == "MID" && col == "CENTER") ? "CENTER" : $"{row}-{col}";
    }

    private string ComputeZoneDelta(Dictionary<string, string> current)
    {
        var changes = new List<string>(3);
        lock (_stateLock)
        {
            foreach (var (zone, val) in current)
            {
                if (_prevZones.TryGetValue(zone, out var oldVal) &&
                    !string.Equals(oldVal, val, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add($"{zone}: '{oldVal}' -> '{val}'");
                    if (changes.Count >= 3) break;
                }
            }
            _prevZones = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
        }
        return string.Join(" ; ", changes);
    }

    private static string FormatZones(Dictionary<string, string> zones)
    {
        if (zones.Count == 0)
            return string.Empty;
        return string.Join(" ; ", zones.Take(6).Select(kv => $"[{kv.Key}] {kv.Value}"));
    }

    public void Dispose()
    {
        List<IGameTelemetrySensor> snapshot;
        lock (_stateLock)
            snapshot = [.. _sensors];
        foreach (var s in snapshot)
        {
            if (s is IDisposable disp)
                try { disp.Dispose(); } catch { }
        }
        try { _ocrGate.Dispose(); } catch { }
    }
}
