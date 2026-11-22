using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace HUDMind.Windows;

/// <summary>
/// Lazy Windows 11 WinRT OCR (~35ms CPU, 0% GPU) + Read-Only Game Log/Local API sensor layer.
/// Invoked ONLY when routing to text-only ("blind") AI models — Vision models pay 0ms overhead.
/// </summary>
public sealed partial class TextOnlySensorService : ITextOnlySensorService
{
    private readonly OcrEngine? _ocr;
    private readonly object _stateLock = new();
    private Dictionary<string, string> _prevZones = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient LocalApiHttp = CreateLoopbackApiClient();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}\b", RegexOptions.Compiled)]
    private static partial Regex TimerRegex();

    [GeneratedRegex(@"^\s*[123]\s*$", RegexOptions.Compiled)]
    private static partial Regex CountdownRegex();

    private static readonly string[] MenuKeywords =
    [
        "PLAY", "JUGAR", "SETTINGS", "OPCIONES", "AJUSTES", "GARAGE", "GARAJE",
        "ITEM SHOP", "TIENDA", "MAIN MENU", "MENÚ PRINCIPAL", "MENU PRINCIPAL",
        "LOCKER", "SALIR", "QUIT GAME", "COMPETITIVE", "CASUAL", "LOBBY"
    ];

    private static readonly string[] ReplayKeywords =
    [
        "REPLAY", "REPETICIÓN", "REPETICION", "SCORED BY", "ANOTADO POR",
        "KILLCAM", "PLAY OF THE GAME", "JUGADA DESTACADA", "GOAL!", "¡GOL!"
    ];

    private static readonly string[] LoadingKeywords =
    [
        "LOADING", "CARGANDO", "SEARCHING", "BUSCANDO PARTIDA", "JOINING", "CONECTANDO", "MATCH FOUND"
    ];

    private static readonly string[] TrainingKeywords =
    [
        "FREE PLAY", "ENTRENAMIENTO", "PRACTICE", "THE RANGE", "WARMUP", "CALENTAMIENTO", "ART OF WAR", "TUTORIAL"
    ];

    private static readonly string[] KickoffKeywords =
    [
        "GO!", "¡YA!", "FREEZETIME", "BUY PHASE", "FASE DE COMPRA", "PREP PHASE", "KICKOFF"
    ];

    public TextOnlySensorService()
    {
        try
        {
            _ocr = OcrEngine.TryCreateFromUserProfileLanguages()
                   ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
        }
        catch
        {
            _ocr = null;
        }
    }

    public void Reset()
    {
        lock (_stateLock)
            _prevZones.Clear();
    }

    public async Task<HudSensorSnapshot> ExtractAsync(byte[] jpegBytes, GameProfile profile, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var zones = await RunWinRtOcrZonesAsync(jpegBytes, ct);
        string logSummary = await ReadGameLogOrLocalApiAsync(profile, ct);
        sw.Stop();

        string phase = ClassifyPhase(zones, logSummary);
        string delta = ComputeZoneDelta(zones);
        string zonesSummary = FormatZones(zones);
        bool hasUseful = zones.Count > 0 || !string.IsNullOrWhiteSpace(logSummary);

        return new HudSensorSnapshot(
            Phase: phase,
            OcrZonesSummary: zonesSummary,
            LogSummary: logSummary,
            DeltaNote: delta,
            OcrMs: sw.Elapsed.TotalMilliseconds,
            HasUsefulData: hasUseful
        );
    }

    private async Task<Dictionary<string, string>> RunWinRtOcrZonesAsync(byte[] jpegBytes, CancellationToken ct)
    {
        var zones = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (_ocr is null || jpegBytes.Length < 64)
            return new Dictionary<string, string>();

        try
        {
            using var ras = new InMemoryRandomAccessStream();
            await ras.WriteAsync(jpegBytes.AsBuffer()).AsTask(ct);
            ras.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct);
            double imgW = Math.Max(1.0, decoder.PixelWidth);
            double imgH = Math.Max(1.0, decoder.PixelHeight);

            using var softwareBmp = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct);

            var ocrRes = await _ocr.RecognizeAsync(softwareBmp).AsTask(ct);
            foreach (var line in ocrRes.Lines)
            {
                var text = line.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
                    continue;
                if (line.Words.Count == 0)
                    continue;

                var r = line.Words[0].BoundingRect;
                double cx = (r.X + r.Width * 0.5) / imgW;
                double cy = (r.Y + r.Height * 0.5) / imgH;
                string zone = MapZone(cx, cy);

                if (!zones.TryGetValue(zone, out var list))
                {
                    list = [];
                    zones[zone] = list;
                }
                if (list.Count < 4)
                    list.Add(text);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Keep sensor resilient on transient decode errors
        }

        return zones.ToDictionary(kv => kv.Key, kv => string.Join(" ", kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static string MapZone(double cx, double cy)
    {
        string row = cy < 0.25 ? "TOP" : (cy > 0.72 ? "BOT" : "MID");
        string col = cx < 0.30 ? "LEFT" : (cx > 0.70 ? "RIGHT" : "CENTER");
        return (row == "MID" && col == "CENTER") ? "CENTER" : $"{row}-{col}";
    }

    private static string ClassifyPhase(Dictionary<string, string> zones, string logSummary)
    {
        var allUpper = (string.Join(" ", zones.Values) + " " + logSummary).ToUpperInvariant();

        if (ReplayKeywords.Any(k => allUpper.Contains(k, StringComparison.Ordinal)))
            return "REPLAY";
        if (LoadingKeywords.Any(k => allUpper.Contains(k, StringComparison.Ordinal)))
            return "LOADING";
        if (TrainingKeywords.Any(k => allUpper.Contains(k, StringComparison.Ordinal)))
            return "TRAINING";

        int menuHits = MenuKeywords.Count(k => allUpper.Contains(k, StringComparison.Ordinal));
        bool hasTimer = TimerRegex().IsMatch(allUpper);
        if (menuHits >= 2 && !hasTimer)
            return "MENU";

        if (KickoffKeywords.Any(k => allUpper.Contains(k, StringComparison.Ordinal)) ||
            (zones.TryGetValue("CENTER", out var centerText) && CountdownRegex().IsMatch(centerText)))
            return "KICKOFF";

        if (hasTimer || zones.ContainsKey("BOT-RIGHT") || zones.ContainsKey("BOT-LEFT") || !string.IsNullOrWhiteSpace(logSummary))
            return "PLAY";

        return menuHits == 1 ? "MENU" : "UNKNOWN";
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
        if (zones.Count == 0) return string.Empty;
        return string.Join(" | ", zones.Take(6).Select(kv => $"[{kv.Key}] {kv.Value}"));
    }

    /// <summary>
    /// Optional read-only official game logs / localhost APIs (zero memory read, 100% Anti-Cheat safe).
    /// </summary>
    private static async Task<string> ReadGameLogOrLocalApiAsync(GameProfile profile, CancellationToken ct)
    {
        var gid = profile.Manifest.GameId.ToLowerInvariant();
        if (gid == "rocket_league" || profile.MatchesProcess("RocketLeague.exe"))
            return ReadRocketLeagueLogTail();

        if (gid.Contains("league") || profile.MatchesProcess("League of Legends.exe"))
            return await ReadLeagueLiveClientApiAsync(ct);

        return string.Empty;
    }

    private static string ReadRocketLeagueLogTail()
    {
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var logPath = Path.Combine(docs, "My Games", "Rocket League", "TAGame", "Logs", "Launch.log");
            if (!File.Exists(logPath))
                return string.Empty;

            var info = new FileInfo(logPath);
            // Ignore stale logs older than 15 minutes
            if ((DateTime.UtcNow - info.LastWriteTimeUtc).TotalMinutes > 15)
                return string.Empty;

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - 4096);
            fs.Seek(start, SeekOrigin.Begin);
            byte[] buf = new byte[fs.Length - start];
            int read = fs.Read(buf, 0, buf.Length);
            string tail = Encoding.UTF8.GetString(buf, 0, read);

            var lines = tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i];
                if (l.Contains("Freeplay", StringComparison.OrdinalIgnoreCase)) return "RL_LOG: Freeplay activo";
                if (l.Contains("GoalScored", StringComparison.OrdinalIgnoreCase)) return "RL_LOG: Gol anotado reciente";
                if (l.Contains("MatchEnded", StringComparison.OrdinalIgnoreCase)) return "RL_LOG: Partido finalizado";
                if (l.Contains("Menu_Main", StringComparison.OrdinalIgnoreCase)) return "RL_LOG: En menú principal";
                if (l.Contains("LoadMap:", StringComparison.OrdinalIgnoreCase)) return $"RL_LOG: {l.Trim()[^Math.Min(60, l.Trim().Length)..]}";
            }
        }
        catch
        {
            // Non-fatal read-only tail check
        }
        return string.Empty;
    }

    private static async Task<string> ReadLeagueLiveClientApiAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(250));
            using var resp = await LocalApiHttp.GetAsync("https://127.0.0.1:2999/liveclientdata/allgamedata", timeoutCts.Token);
            if (!resp.IsSuccessStatusCode) return string.Empty;
            var json = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            double gameTime = root.GetProperty("gameData").GetProperty("gameTime").GetDouble();
            var active = root.GetProperty("activePlayer");
            int lvl = active.GetProperty("level").GetInt32();
            double gold = active.GetProperty("currentGold").GetDouble();
            var stats = active.GetProperty("championStats");
            double hp = stats.GetProperty("currentHealth").GetDouble();
            double maxHp = Math.Max(1.0, stats.GetProperty("maxHealth").GetDouble());
            int mins = (int)(gameTime / 60);
            int secs = (int)(gameTime % 60);
            return $"LoL_API: Tiempo {mins:D2}:{secs:D2}, Nivel {lvl}, Oro {gold:F0}, Vida {(hp * 100.0 / maxHp):F0}%";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HttpClient CreateLoopbackApiClient()
    {
        // Riot's official localhost:2999 Live Client Data API uses Riot's self-signed local cert on 127.0.0.1 ONLY
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (req, _, _, _) =>
                req.RequestUri is { IsLoopback: true, Port: 2999 }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(350) };
    }
}
