using System.Buffers;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HUDMind.Core.AI;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace HUDMind.Windows;

/// <summary>
/// Lazy Windows 11 WinRT OCR (~25-60ms CPU, 0% GPU) + Read-Only Game Log/Local API sensor layer.
/// Invoked ONLY for text-only ("blind") models and optional proactive Live menu gating.
/// </summary>
public sealed partial class TextOnlySensorService : ITextOnlySensorService
{
    private readonly OcrEngine? _ocr;
    private readonly LoopbackGsiService? _gsiService;
    private readonly SemaphoreSlim _ocrGate = new(1, 1);
    private readonly object _stateLock = new();
    private Dictionary<string, string> _prevZones = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient LocalApiHttp = CreateLoopbackApiClient();

    public bool HasOcrEngine => _ocr is not null;
    public string OcrLanguageTag { get; }

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
        "KILLCAM", "PLAY OF THE GAME", "JUGADA DESTACADA", "GOAL!", "GOL!"
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
        "GO!", "YA!", "FREEZETIME", "BUY PHASE", "FASE DE COMPRA", "PREP PHASE", "KICKOFF"
    ];

    public TextOnlySensorService(LoopbackGsiService? gsiService = null)
    {
        _gsiService = gsiService;
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

    public void Reset()
    {
        lock (_stateLock)
            _prevZones.Clear();
    }

    public async Task<HudSensorSnapshot> ExtractAsync(byte[] jpegBytes, GameProfile profile, CancellationToken ct = default)
    {
        long t0 = Stopwatch.GetTimestamp();

        // Run WinRT OCR and read-only Log/API checks concurrently!
        var ocrTask = RunWinRtOcrZonesAsync(jpegBytes, ct);
        var logTask = ReadGameLogOrLocalApiAsync(profile, ct);
        await Task.WhenAll(ocrTask, logTask).ConfigureAwait(false);

        var zones = await ocrTask.ConfigureAwait(false);
        string logSummary = await logTask.ConfigureAwait(false);
        double elapsedMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        string phase = ClassifyPhase(zones, logSummary);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Keep sensor resilient on transient decode errors
        }
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

    public static string ClassifyPhase(Dictionary<string, string> zones, string logSummary)
    {
        // Weight structural HUD bands more than MID-LEFT/BOT-LEFT (common in-game team chat corners!)
        var hudPriorityText = string.Join(" ", zones
            .Where(kv => kv.Key is not ("MID-LEFT" or "BOT-LEFT"))
            .Select(kv => kv.Value));
        var ocrHudUpper = NormalizePunct(hudPriorityText);
        var logUpper = NormalizePunct(logSummary);
        var hudUpper = string.Concat(ocrHudUpper, " ", logUpper);
        var allUpper = NormalizePunct(string.Join(" ", zones.Values) + " " + logSummary);

        if (logUpper.Contains("WAIT_FOR_PLAYERS") || logUpper.Contains("DISCONNECT"))
            return "LOADING";
        if (logUpper.Contains("PHASE=GAMEOVER") || logUpper.Contains("POST_GAME") ||
            logUpper.Contains("MENU_MAIN") || logUpper.Contains("HERO_SELECTION") ||
            logUpper.Contains("STRATEGY_TIME"))
            return "MENU";

        if (ReplayKeywords.Any(k => hudUpper.Contains(k, StringComparison.Ordinal)))
            return "REPLAY";
        if (LoadingKeywords.Any(k => hudUpper.Contains(k, StringComparison.Ordinal)))
            return "LOADING";
        if (TrainingKeywords.Any(k => hudUpper.Contains(k, StringComparison.Ordinal)))
            return "TRAINING";

        // Match clock timers on screen OCR only (never let DOTA2_GSI clock strings cancel OCR menu detection)
        bool hasTimer = TimerRegex().IsMatch(ocrHudUpper);
        int menuHits = MenuKeywords.Count(k => allUpper.Contains(k, StringComparison.Ordinal));
        if (menuHits >= 2 && !hasTimer)
            return "MENU";

        if (KickoffKeywords.Any(k => hudUpper.Contains(k, StringComparison.Ordinal)) ||
            (zones.TryGetValue("CENTER", out var centerText) && CountdownRegex().IsMatch(centerText)))
            return "KICKOFF";

        if (menuHits == 1 && !hasTimer && string.IsNullOrWhiteSpace(logSummary))
            return "MENU";

        if (hasTimer || zones.ContainsKey("BOT-RIGHT") || zones.ContainsKey("BOT-LEFT") || !string.IsNullOrWhiteSpace(logSummary))
            return "PLAY";

        return "UNKNOWN";
    }

    private static string NormalizePunct(string s) =>
        s.Replace('¡', ' ').Replace('¿', ' ').ToUpperInvariant();

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
        return string.Join(" ; ", zones.Take(6).Select(kv => $"[{kv.Key}] {kv.Value}"));
    }

    /// <summary>
    /// Optional read-only official game logs / localhost APIs (zero memory read, 100% Anti-Cheat safe).
    /// </summary>
    private async Task<string> ReadGameLogOrLocalApiAsync(GameProfile profile, CancellationToken ct)
    {
        var gid = profile.Manifest.GameId.ToLowerInvariant();
        if (gid == "rocket_league" || profile.MatchesProcess("RocketLeague.exe"))
            return await Task.Run(ReadRocketLeagueLogTail, ct).ConfigureAwait(false);

        if (gid.Contains("valorant") || profile.MatchesProcess("VALORANT-Win64-Shipping.exe"))
            return await Task.Run(ReadValorantLogTail, ct).ConfigureAwait(false);

        if (gid.Contains("league") || profile.MatchesProcess("League of Legends.exe"))
            return await ReadLeagueLiveClientApiAsync(ct).ConfigureAwait(false);

        if (_gsiService is not null && (gid.Contains("cs2") || profile.MatchesProcess("cs2.exe")))
        {
            var gsi = _gsiService.GetRecentSummary("cs2");
            if (!string.IsNullOrEmpty(gsi)) return gsi;
        }

        if (_gsiService is not null && (gid.Contains("dota") || profile.MatchesProcess("dota2.exe")))
        {
            var gsi = _gsiService.GetRecentSummary("dota2");
            if (!string.IsNullOrEmpty(gsi)) return gsi;
        }

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

            var tail = ReadTailTextSafe(logPath, maxAgeMinutes: 120);
            if (string.IsNullOrEmpty(tail))
                return string.Empty;

            var lines = tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = CoachingPersonaBuilder.SanitizeField(lines[i], 120);
                if (l.Contains("Freeplay", StringComparison.OrdinalIgnoreCase)) return "RL_LOG: Freeplay activo";
                if (l.Contains("GoalScored", StringComparison.OrdinalIgnoreCase)) return "RL_LOG: Gol anotado reciente";
                if (l.Contains("MatchEnded", StringComparison.OrdinalIgnoreCase)) return "RL_LOG: Partido finalizado";
                if (l.Contains("Menu_Main", StringComparison.OrdinalIgnoreCase)) return "RL_LOG: En menú principal";
                if (l.Contains("LoadMap:", StringComparison.OrdinalIgnoreCase)) return $"RL_LOG: {l[^Math.Min(60, l.Length)..]}";
            }
        }
        catch { }
        return string.Empty;
    }

    private static string ReadValorantLogTail()
    {
        try
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logPath = Path.Combine(localApp, "VALORANT", "Saved", "Logs", "ShooterGame.log");
            if (!File.Exists(logPath))
                return string.Empty;

            var tail = ReadTailTextSafe(logPath, maxAgeMinutes: 120);
            if (string.IsNullOrEmpty(tail))
                return string.Empty;

            var lines = tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = CoachingPersonaBuilder.SanitizeField(lines[i], 120);
                if (l.Contains("MENU", StringComparison.OrdinalIgnoreCase)) return "VAL_LOG: Menu / Lobby";
                if (l.Contains("PREGAME", StringComparison.OrdinalIgnoreCase)) return "VAL_LOG: Selección de agentes";
                if (l.Contains("INGAME", StringComparison.OrdinalIgnoreCase)) return "VAL_LOG: Partida en curso";
            }
        }
        catch { }
        return string.Empty;
    }

    internal static string ReadTailTextSafe(string path, double maxAgeMinutes)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return string.Empty;

        // Generous cutoff (NTFS defers LastWriteTime flush on open game logs during long sessions!)
        if ((DateTime.UtcNow - info.LastWriteTimeUtc).TotalMinutes > maxAgeMinutes)
            return string.Empty;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - 4096);
        start &= ~1L; // Keep even boundary in case file is UTF-16 LE!
        fs.Seek(start, SeekOrigin.Begin);
        int toRead = (int)Math.Min(4096, fs.Length - start);
        if (toRead <= 0) return string.Empty;

        byte[] buf = ArrayPool<byte>.Shared.Rent(toRead);
        try
        {
            int read = fs.Read(buf, 0, toRead);
            if (read <= 0) return string.Empty;

            // Detect UE3 UTF-16 LE encoding (BOM 0xFF 0xFE or interleaved NUL high bytes)
            bool looksUtf16Le = (read >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) ||
                                (read >= 6 && buf[1] == 0 && buf[3] == 0 && buf[5] == 0);
            return looksUtf16Le
                ? Encoding.Unicode.GetString(buf, 0, read & ~1)
                : Encoding.UTF8.GetString(buf, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static async Task<string> ReadLeagueLiveClientApiAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(260));

            // Fetch ONLY compact activeplayer + gamestats (~2 KB) rather than full 80 KB allgamedata!
            var tPlayer = LocalApiHttp.GetAsync("https://127.0.0.1:2999/liveclientdata/activeplayer", timeoutCts.Token);
            var tStats = LocalApiHttp.GetAsync("https://127.0.0.1:2999/liveclientdata/gamestats", timeoutCts.Token);
            await Task.WhenAll(tPlayer, tStats).ConfigureAwait(false);

            using var respPlayer = await tPlayer.ConfigureAwait(false);
            using var respStats = await tStats.ConfigureAwait(false);
            if (!respPlayer.IsSuccessStatusCode || !respStats.IsSuccessStatusCode)
                return string.Empty;

            await using var sPlayer = await respPlayer.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            await using var sStats = await respStats.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var docPlayer = await JsonDocument.ParseAsync(sPlayer, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            using var docStats = await JsonDocument.ParseAsync(sStats, cancellationToken: timeoutCts.Token).ConfigureAwait(false);

            double gameTime = docStats.RootElement.TryGetProperty("gameTime", out var gt) ? gt.GetDouble() : 0;
            int lvl = docPlayer.RootElement.TryGetProperty("level", out var lv) ? lv.GetInt32() : 1;
            double gold = docPlayer.RootElement.TryGetProperty("currentGold", out var cg) ? cg.GetDouble() : 0;

            double hp = 1, maxHp = 1;
            if (docPlayer.RootElement.TryGetProperty("championStats", out var st))
            {
                if (st.TryGetProperty("currentHealth", out var ch)) hp = ch.GetDouble();
                if (st.TryGetProperty("maxHealth", out var mh)) maxHp = Math.Max(1.0, mh.GetDouble());
            }

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
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Proxy = null,
            ServerCertificateCustomValidationCallback = ValidateLeagueLoopbackCert
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(350),
            MaxResponseContentBufferSize = 512 * 1024
        };
    }

    internal static bool ValidateLeagueLoopbackCert(
        HttpRequestMessage req,
        X509Certificate2? cert,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        // Strictly restrict to literal IPv4 127.0.0.1:2999 HTTPS (CWE-295 / CWE-918)
        if (req.RequestUri is not { Scheme: "https", Host: "127.0.0.1", Port: 2999 })
            return false;
        if (errors == SslPolicyErrors.None)
            return true;
        // Reject missing certificate or SAN/CN hostname mismatch; only evaluate Riot's custom self-signed root chain
        if (cert is null || errors != SslPolicyErrors.RemoteCertificateChainErrors)
            return false;

        var now = DateTime.UtcNow;
        if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime())
            return false;

        // Require strong cryptographic key (>= 2048-bit RSA or >= 256-bit ECDSA)
        int keyBits = cert.GetRSAPublicKey()?.KeySize ?? cert.GetECDsaPublicKey()?.KeySize ?? 0;
        bool isEcdsa = cert.GetECDsaPublicKey() is not null;
        if (keyBits < (isEcdsa ? 256 : 2048))
            return false;

        // Verify X.509 Subject Alternative Name extension covers 127.0.0.1 or riotgames.com
        bool sanMatches = false;
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value == "2.5.29.17") // Subject Alternative Name
            {
                var sanText = ext.Format(multiLine: false);
                if (sanText.Contains("127.0.0.1", StringComparison.Ordinal) ||
                    sanText.Contains("riotgames", StringComparison.OrdinalIgnoreCase))
                {
                    sanMatches = true;
                    break;
                }
            }
        }
        if (!sanMatches)
            return false;

        bool hasRiotIdentity =
            cert.Subject.Contains("Riot Games", StringComparison.OrdinalIgnoreCase) ||
            cert.Issuer.Contains("Riot Games", StringComparison.OrdinalIgnoreCase);

        if (!hasRiotIdentity)
            return false;

        // Only trust custom loopback cert while Riot's actual League process is active locally
        try
        {
            if (Process.GetProcessesByName("League of Legends").Length == 0)
                return false;
        }
        catch
        {
            return false;
        }

        return true;
    }
}
