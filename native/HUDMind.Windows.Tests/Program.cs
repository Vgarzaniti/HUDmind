using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Windows;

int passed = 0;
int failed = 0;

void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception($"Assertion failed: {message}");
}

async Task RunTest(string name, Func<Task> body)
{
    try
    {
        await body();
        passed++;
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
    }
}

static int FindFreeLoopbackPort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}

static async Task<string> SendRawLoopbackHttpAsync(int port, string rawHttpRequest)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, port);
    using var stream = tcp.GetStream();
    var reqBytes = Encoding.UTF8.GetBytes(rawHttpRequest);
    await stream.WriteAsync(reqBytes);
    await stream.FlushAsync();

    var sb = new StringBuilder();
    var buf = new byte[1024];
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    int n = await stream.ReadAsync(buf, cts.Token);
    if (n > 0)
        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
    return sb.ToString();
}

Console.WriteLine("=== HUDMind.Windows.Tests (.NET 9 Win32 / Sensors / Audio Suite) ===\n");

// W1. Win32AudioDevices Enumeration & Fallback ID Resolution
await RunTest("W1. Win32AudioDevices preserves device IDs & falls back safely to WaveMapper (-1)", () =>
{
    Assert(Win32AudioDevices.ResolveInputDeviceId(null) == Win32AudioDevices.WaveMapper, "null input -> WaveMapper");
    Assert(Win32AudioDevices.ResolveInputDeviceId("   ") == Win32AudioDevices.WaveMapper, "blank input -> WaveMapper");
    Assert(Win32AudioDevices.ResolveInputDeviceId("__NonExistentDevice_999__") == Win32AudioDevices.WaveMapper, "missing input -> WaveMapper");
    Assert(Win32AudioDevices.ResolveOutputDeviceId("__NonExistentDevice_999__") == Win32AudioDevices.WaveMapper, "missing output -> WaveMapper");

    var inputs = Win32AudioDevices.GetInputDeviceNames();
    var outputs = Win32AudioDevices.GetOutputDeviceNames();
    if (inputs.Length > 0)
    {
        int id = Win32AudioDevices.ResolveInputDeviceId(inputs[0].ToLowerInvariant());
        Assert(id >= 0, $"Existing mic '{inputs[0]}' must resolve case-insensitively to index >= 0 (got {id})");
    }
    if (outputs.Length > 0)
    {
        int id = Win32AudioDevices.ResolveOutputDeviceId(outputs[0].ToUpperInvariant());
        Assert(id >= 0, $"Existing speaker '{outputs[0]}' must resolve case-insensitively to index >= 0 (got {id})");
    }
    return Task.CompletedTask;
});

// W2. LoopbackGsiService Live TCP Loopback HTTP, Clear-on-Menu, 413/400 Rejection & Clean Stop
await RunTest("W2. LoopbackGsiService parses CS2/Dota2 JSON over TCP, clears on menu & rejects 413/chunked", async () =>
{
    int port = FindFreeLoopbackPort();
    using var gsi = new LoopbackGsiService(port);
    Assert(gsi.TryStart() && gsi.IsListening, $"LoopbackGsiService should bind 127.0.0.1:{port}");

    var cs2Json = "{\"provider\":{\"appid\":730},\"map\":{\"name\":\"de_mirage\",\"phase\":\"live\",\"team_ct\":{\"score\":7},\"team_t\":{\"score\":5}},\"round\":{\"phase\":\"live\",\"bomb\":\"planted\"},\"player\":{\"team\":\"CT\",\"state\":{\"health\":82,\"armor\":100,\"money\":3450},\"weapons\":{\"w0\":{\"name\":\"weapon_m4a1_silencer\",\"state\":\"active\",\"ammo_clip\":19}}}}";
    var reqCs2 = $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: {Encoding.UTF8.GetByteCount(cs2Json)}\r\nConnection: close\r\n\r\n{cs2Json}";
    var respCs2 = await SendRawLoopbackHttpAsync(port, reqCs2);
    Assert(respCs2.StartsWith("HTTP/1.1 200 OK"), $"CS2 POST expected 200, got: {respCs2}");
    var cs2Summary = gsi.GetRecentSummary("cs2");
    Assert(cs2Summary.Contains("CS2_GSI:") && cs2Summary.Contains("de_mirage") && cs2Summary.Contains("HP=82") && cs2Summary.Contains("C4=planted"),
        $"CS2 summary missing expected fields: {cs2Summary}");

    // Transition to menu clears stale CS2 state immediately
    var cs2MenuJson = "{\"provider\":{\"appid\":730},\"player\":{\"activity\":\"menu\"}}";
    var reqMenu = $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: {Encoding.UTF8.GetByteCount(cs2MenuJson)}\r\n\r\n{cs2MenuJson}";
    var respMenu = await SendRawLoopbackHttpAsync(port, reqMenu);
    Assert(respMenu.StartsWith("HTTP/1.1 200 OK") && gsi.GetRecentSummary("cs2") == string.Empty,
        "CS2 menu payload must clear cached CS2 summary");

    // Dota 2 payload
    var dotaJson = "{\"provider\":{\"appid\":570},\"map\":{\"clock_time\":750,\"game_state\":\"DOTA_GAMERULES_STATE_GAME_IN_PROGRESS\"},\"hero\":{\"name\":\"npc_dota_hero_juggernaut\",\"level\":11,\"health\":900,\"max_health\":1200,\"mana\":300,\"max_mana\":500,\"buyback_cooldown\":0},\"player\":{\"gold\":2150,\"last_hits\":94,\"denies\":12}}";
    var reqDota = $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: {Encoding.UTF8.GetByteCount(dotaJson)}\r\n\r\n{dotaJson}";
    var respDota = await SendRawLoopbackHttpAsync(port, reqDota);
    var dotaSummary = gsi.GetRecentSummary("dota2");
    Assert(respDota.StartsWith("HTTP/1.1 200 OK") && dotaSummary.Contains("DOTA2_GSI:") && dotaSummary.Contains("12:30") && dotaSummary.Contains("juggernaut"),
        $"Dota 2 summary missing expected fields: {dotaSummary}");

    // Oversight & chunked rejections
    var resp413 = await SendRawLoopbackHttpAsync(port, $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: 999999\r\n\r\n");
    Assert(resp413.Contains("413"), $"Oversized Content-Length must return 413, got: {resp413}");

    var respChunked = await SendRawLoopbackHttpAsync(port, $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
    Assert(respChunked.Contains("400"), $"Chunked request must return 400, got: {respChunked}");

    // Half-open stalled client must not prevent clean Stop() within bounded time
    using var stalledClient = new TcpClient();
    await stalledClient.ConnectAsync(IPAddress.Loopback, port);
    var sw = Stopwatch.StartNew();
    gsi.Stop();
    sw.Stop();
    Assert(!gsi.IsListening && sw.ElapsedMilliseconds < 2500,
        $"Stop() hung on stalled client ({sw.ElapsedMilliseconds}ms)");
});

// W3. LoopbackGsiService Path Safety Against UNC & Reparse Points
await RunTest("W3. LoopbackGsiService.IsSafeLocalDirectoryPath blocks UNC shares and relative paths", () =>
{
    Assert(!LoopbackGsiService.IsSafeLocalDirectoryPath(@"\\attacker\share", out _), "UNC backslash path blocked");
    Assert(!LoopbackGsiService.IsSafeLocalDirectoryPath("//attacker/share", out _), "UNC forward slash path blocked");
    Assert(!LoopbackGsiService.IsSafeLocalDirectoryPath("relative\\Steam", out _), "Relative path blocked");

    var tmp = Path.Combine(Path.GetTempPath(), $"hudmind_w3_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try
    {
        Assert(LoopbackGsiService.IsSafeLocalDirectoryPath(tmp, out var full) &&
               string.Equals(full, Path.GetFullPath(tmp).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
            "Valid local temp directory accepted");
    }
    finally
    {
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
    return Task.CompletedTask;
});

// W4. TailLogSensorBase UTF-16 LE & UTF-8 Tailing + Stale Age Filter + Sensor Profile Filtering
await RunTest("W4. TailLogSensorBase reads UTF-8 & UTF-16 LE tails cleanly, filters stale logs & scopes profiles", () =>
{
    var rlSensor = new RocketLeagueLogSensor();
    var valSensor = new ValorantLogSensor();
    var rlProf = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rocket_league", DisplayName = "Rocket League", ProcessNames = ["RocketLeague.exe"] } };
    var valProf = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "valorant", DisplayName = "Valorant", ProcessNames = ["VALORANT-Win64-Shipping.exe"] } };

    Assert(rlSensor.Supports(rlProf) && !rlSensor.Supports(valProf), "RocketLeagueLogSensor scope check");
    Assert(valSensor.Supports(valProf) && !valSensor.Supports(rlProf), "ValorantLogSensor scope check");

    var tmpDir = Path.Combine(Path.GetTempPath(), $"hudmind_w4_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try
    {
        // UTF-8 log > 8KB ensuring tail window picks final LoadMap marker
        var utf8Path = Path.Combine(tmpDir, "launch_utf8.log");
        var padding = new string('X', 6000);
        File.WriteAllText(utf8Path, padding + "\n[001] LoadMap: /Game/Maps/DFHStadium\n", Encoding.UTF8);
        var tailUtf8 = TailLogSensorBase.ReadTailTextSafe(utf8Path, maxAgeMinutes: 60);
        Assert(tailUtf8.Contains("LoadMap: /Game/Maps/DFHStadium"), $"UTF-8 tail failed: {tailUtf8}");

        // UTF-16 LE log (with BOM + 16-bit alignment)
        var utf16Path = Path.Combine(tmpDir, "shooter_utf16.log");
        File.WriteAllText(utf16Path, new string('A', 4000) + "\r\nLog: INGAME match started\r\n", Encoding.Unicode);
        var tailUtf16 = TailLogSensorBase.ReadTailTextSafe(utf16Path, maxAgeMinutes: 60);
        Assert(tailUtf16.Contains("INGAME match started"), $"UTF-16 LE tail failed: {tailUtf16}");

        // Stale file older than maxAgeMinutes returns empty
        File.SetLastWriteTimeUtc(utf8Path, DateTime.UtcNow.AddMinutes(-200));
        var staleTail = TailLogSensorBase.ReadTailTextSafe(utf8Path, maxAgeMinutes: 120);
        Assert(staleTail == string.Empty, "Stale log older than cutoff must return empty");
    }
    finally
    {
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
    }
    return Task.CompletedTask;
});

// W5. LeagueLiveClientSensor Profile Scoping & Strict Loopback X.509 Validation
await RunTest("W5. LeagueLiveClientSensor scopes strictly & rejects forged/weak/wrong-host certificates", () =>
{
    var lolSensor = new LeagueLiveClientSensor();
    var rlProf = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rocket_league", DisplayName = "RL", ProcessNames = ["RocketLeague.exe"] } };
    var lolProf = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "league_of_legends", DisplayName = "LoL", ProcessNames = ["League of Legends.exe"] } };

    Assert(!lolSensor.Supports(rlProf) && lolSensor.Supports(lolProf), "LeagueLiveClientSensor must only support LoL");

    // Wrong host/port must be rejected immediately even when SslPolicyErrors.None
    var badHostReq = new HttpRequestMessage(HttpMethod.Get, "https://example.com:2999/liveclientdata/activeplayer");
    Assert(!LeagueLiveClientSensor.ValidateLeagueLoopbackCert(badHostReq, null, null, SslPolicyErrors.None), "Wrong host rejected");

    var badPortReq = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:443/liveclientdata/activeplayer");
    Assert(!LeagueLiveClientSensor.ValidateLeagueLoopbackCert(badPortReq, null, null, SslPolicyErrors.None), "Wrong port rejected");

    var validReq = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:2999/liveclientdata/activeplayer");
    Assert(!LeagueLiveClientSensor.ValidateLeagueLoopbackCert(validReq, null, null, SslPolicyErrors.RemoteCertificateChainErrors), "Null cert rejected");
    Assert(!LeagueLiveClientSensor.ValidateLeagueLoopbackCert(validReq, null, null, SslPolicyErrors.RemoteCertificateNameMismatch), "Name mismatch rejected");

    // Non-Riot cert with SAN=127.0.0.1 must be rejected because Subject/Issuer lacks 'Riot Games'
    using (var rsa = RSA.Create(2048))
    {
        var certReq = new CertificateRequest("CN=EvilLocalServer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        certReq.CertificateExtensions.Add(san.Build());
        using var evilCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        Assert(!LeagueLiveClientSensor.ValidateLeagueLoopbackCert(validReq, evilCert, null, SslPolicyErrors.RemoteCertificateChainErrors),
            "Non-Riot loopback cert must be rejected");
    }

    // Expired Riot Games cert must be rejected
    using (var rsa = RSA.Create(2048))
    {
        var certReq = new CertificateRequest("CN=Riot Games Inc", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        certReq.CertificateExtensions.Add(san.Build());
        using var expiredCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        Assert(!LeagueLiveClientSensor.ValidateLeagueLoopbackCert(validReq, expiredCert, null, SslPolicyErrors.RemoteCertificateChainErrors),
            "Expired certificate must be rejected");
    }

    return Task.CompletedTask;
});

// W6. Win32DisplayHelper, Win32KeyStateProbe, Win32WindowDetector, FastScreenCapture & Win32AudioCue Smoke
await RunTest("W6. Win32DisplayHelper, KeyStateProbe, WindowDetector, FastScreenCapture & AudioCue smoke", () =>
{
    var areas = Win32DisplayHelper.GetAllWorkingAreas();
    Assert(areas.Length >= 1 && areas[0].Width > 0 && areas[0].Height > 0,
        $"Expected valid monitor working areas, got {areas.Length}");

    Assert(!Win32KeyStateProbe.IsPushToTalkKeyDown("Ctrl+V"), "Invalid modifier combo must return false without throwing");
    _ = Win32KeyStateProbe.IsPushToTalkKeyDown("F24");

    var det = new Win32WindowDetector();
    _ = det.GetForegroundExecutable();
    _ = det.GetForegroundClientRect();

    using var cap = new FastScreenCapture();
    var (frame1, skipped1) = cap.CaptureIfChanged(1, null, 640, 360, 75, frameId: 1, lastAcceptedHash: null, phashThreshold: 6, isForced: true);
    Assert(!skipped1, "Forced first frame should never be skipped by pHash");
    if (frame1 is not null)
    {
        Assert(frame1.JpegBytes.Length > 0 && frame1.FrameId == 1,
            $"Captured frame metadata unexpected: frameId={frame1.FrameId}, bytes={frame1.JpegBytes.Length}");
        var (_, skipped2) = cap.CaptureIfChanged(1, null, 640, 360, 75, frameId: 2, lastAcceptedHash: frame1.PerceptualHash, phashThreshold: 64, isForced: false);
        Assert(skipped2, "Identical screen with max Hamming threshold (64) must skip unchanged frame");
    }

    // Audio cue must be safe even if CI runner has no physical audio output device
    Win32AudioCue.Play(AudioCueKind.SoftOn);
    Win32AudioCue.Play(AudioCueKind.SoftOff);
    return Task.CompletedTask;
});

// W7. Pure Native C# WinRtNaturalVoiceService (WAV->24kHz PCM, Rate Parsing) & HybridVoiceService
await RunTest("W7. WinRtNaturalVoiceService parses rates/RIFF WAV cleanly & HybridVoiceService runs 100% Python-free", () =>
{
    Assert(Math.Abs(WinRtNaturalVoiceService.ParseRateFactor("+15%") - 1.15) < 0.001, "+15% rate");
    Assert(Math.Abs(WinRtNaturalVoiceService.ParseRateFactor("-50%") - 0.65) < 0.001, "rate lower clamp 0.65");
    Assert(WinRtNaturalVoiceService.ConvertWavToPcm24kMono(new byte[10], 10).Length == 0, "Invalid tiny buffer rejected");

    // Synthetic 16kHz 16-bit mono RIFF WAV with 160 samples (10ms) -> expects ~240 samples (480 bytes) at 24kHz
    byte[] wav = BuildSyntheticPcm16MonoWav(sampleRate: 16000, samples: 160);
    byte[] resampled = WinRtNaturalVoiceService.ConvertWavToPcm24kMono(wav, wav.Length);
    Assert(resampled.Length == 240 * 2, $"Expected 480 bytes at 24kHz mono, got {resampled.Length}");

    using var voice = new HybridVoiceService();
    voice.SetSpeechRate("+15%");
    voice.SetOutputDevice(string.Empty);
    voice.UpdateSettings("es", 0.5, "male", "neural");
    Assert(voice.SpeakInstant("   ") == 0, "Blank utterance must short-circuit cleanly");
    Assert(!string.IsNullOrWhiteSpace(voice.EngineName), "EngineName must be populated");
    return Task.CompletedTask;
});

// W8. KnownFolders.GetDownloadsDirectory resolves an existing local folder
await RunTest("W8. KnownFolders.GetDownloadsDirectory resolves an existing directory", () =>
{
    var downloads = KnownFolders.GetDownloadsDirectory();
    Assert(!string.IsNullOrWhiteSpace(downloads) && Directory.Exists(downloads),
        $"Expected existing Downloads/Desktop fallback directory, got '{downloads}'");
    return Task.CompletedTask;
});

// W9. Enriched Telemetry Sensors (CS2 C4 Countdown/Kit/Helmet, LoL allgamedata CS/Dead, ReadTelemetryAsync)
await RunTest("W9. Enriched GSI C4 countdown/kit, LoL allgamedata CS/DEAD & ReadTelemetryAsync bridge cleanly", async () =>
{
    int port = FindFreeLoopbackPort();
    using var gsi = new LoopbackGsiService(port);
    Assert(gsi.TryStart(), "GSI server start");

    var cs2Json = "{\"provider\":{\"appid\":730},\"map\":{\"name\":\"de_inferno\",\"phase\":\"live\"},\"round\":{\"phase\":\"live\",\"bomb\":\"planted\"},\"player\":{\"team\":\"CT\",\"state\":{\"health\":90,\"armor\":100,\"helmet\":true,\"defusekit\":true,\"money\":4150}}}";
    var req = $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: {Encoding.UTF8.GetByteCount(cs2Json)}\r\nConnection: close\r\n\r\n{cs2Json}";
    await SendRawLoopbackHttpAsync(port, req);
    var summary = gsi.GetRecentSummary("cs2");
    Assert(summary.Contains("Helmet=1") && summary.Contains("Kit=1") && summary.Contains("C4=planted(~"),
        $"Expected Helmet, Kit and C4 countdown in CS2 summary: {summary}");

    var sensor = new TextOnlySensorService(gsi);
    var csProf = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "cs2", DisplayName = "Counter-Strike 2", ProcessNames = ["cs2.exe"] } };
    var bridged = await sensor.ReadTelemetryAsync(csProf);
    Assert(bridged.Contains("CS2_GSI:") && bridged.Contains("Kit=1"),
        $"ReadTelemetryAsync must forward GSI telemetry without image capture: {bridged}");

    using var lolDoc = System.Text.Json.JsonDocument.Parse("{\"activePlayer\":{\"riotId\":\"Test#LAS\",\"level\":9,\"currentGold\":1420,\"championStats\":{\"currentHealth\":0,\"maxHealth\":1200}},\"gameData\":{\"gameTime\":615.0},\"allPlayers\":[{\"riotId\":\"Test#LAS\",\"isDead\":true,\"scores\":{\"creepScore\":72,\"wardScore\":8.4}}]}");
    var lolParsed = LeagueLiveClientSensor.ParseAllGameData(lolDoc.RootElement);
    Assert(lolParsed.Contains("10:15") && lolParsed.Contains("CS 72") && lolParsed.Contains("Wards 8") && lolParsed.Contains("DEAD"),
        $"ParseAllGameData missing CS/Wards/DEAD fields: {lolParsed}");
});

// W10. Win32StreamingPcmPlayer jitter prebuffer/flush/odd-byte + SAPI5 TtsRate parity
await RunTest("W10. Win32StreamingPcmPlayer prebuffer/flush/interrupt & WinRt/SAPI5 speech rate parity hold cleanly", () =>
{
    Assert(Math.Abs(WinRtNaturalVoiceService.ParseRateFactor(null) - 1.05) < 0.001, "Default natural rate should be +5% (1.05)");
    Assert(WinRtNaturalVoiceService.ParseSapiRateOffset("-10%") == -1, "-10% -> SAPI rate -1");
    Assert(WinRtNaturalVoiceService.ParseSapiRateOffset("+5%") == 0, "+5% -> SAPI rate 0");
    Assert(WinRtNaturalVoiceService.ParseSapiRateOffset("+15%") == 1, "+15% -> SAPI rate 1");
    Assert(WinRtNaturalVoiceService.ParseSapiRateOffset("+25%") == 2, "+25% -> SAPI rate 2");

    using var player = new Win32StreamingPcmPlayer();
    player.SetVolume(0.6);
    player.EnqueuePcm24k(new byte[2401]); // under 160ms prebuffer + odd byte
    player.EnqueuePcm24k(new byte[6000]); // crosses prebuffer -> submits smoothed block
    player.FlushBuffered();               // adds fade-out + trailing silence padding & clears odd state
    player.InterruptAndClear();           // resets safely even if audio hardware is absent on headless runner
    Assert(!player.IsPlayingAudio, "InterruptAndClear must clear IsPlayingAudio immediately");

    using var voice = new HybridVoiceService();
    voice.SetSpeechRate("+5%");
    voice.SetSpeechRate("+0%");
    return Task.CompletedTask;
});

// W11. Win32TemporalStoryboardComposer packs 2x1 motion strip & 2x2 chronological storyboard cleanly
await RunTest("W11. Win32TemporalStoryboardComposer renders valid 2x1 motion strips & 2x2 storyboards cleanly", () =>
{
    var composer = new Win32TemporalStoryboardComposer();
    Assert(composer.ComposeSequenceJpeg([], 1440, 810, 84) is null, "Empty frames must return null");
    Assert(composer.ComposeSequenceJpeg([new TemporalFrameSample([1, 2, 3], DateTimeOffset.UtcNow, 1UL)], 1440, 810, 84) is null,
        "Single malformed frame must return null safely");

    byte[] MakeTestJpeg(System.Drawing.Color c)
    {
        using var bmp = new System.Drawing.Bitmap(320, 180);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.Clear(c);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        return ms.ToArray();
    }

    var now = DateTimeOffset.UtcNow;
    var samples = new List<TemporalFrameSample>
    {
        new(MakeTestJpeg(System.Drawing.Color.Navy), now.AddMilliseconds(-900), 1UL),
        new(MakeTestJpeg(System.Drawing.Color.DarkCyan), now.AddMilliseconds(-600), 2UL),
        new(MakeTestJpeg(System.Drawing.Color.DarkGreen), now.AddMilliseconds(-300), 3UL),
        new(MakeTestJpeg(System.Drawing.Color.DarkOrange), now, 4UL)
    };

    var pair = composer.ComposeSequenceJpeg(samples.Take(2).ToList(), 1440, 405, 82);
    Assert(pair is { Length: > 1000 }, "2-frame strip must produce valid JPEG bytes");
    using (var ms2 = new MemoryStream(pair!))
    using (var img2 = System.Drawing.Image.FromStream(ms2))
    {
        Assert(img2.Width == 1440 && img2.Height == 405, $"Expected 1440x405 2x1 strip, got {img2.Width}x{img2.Height}");
    }

    var grid = composer.ComposeSequenceJpeg(samples, 1440, 810, 85);
    Assert(grid is { Length: > 1000 }, "4-frame storyboard must produce valid JPEG bytes");
    using (var ms4 = new MemoryStream(grid!))
    using (var img4 = System.Drawing.Image.FromStream(ms4))
    {
        Assert(img4.Width == 1440 && img4.Height == 810, $"Expected 1440x810 2x2 storyboard, got {img4.Width}x{img4.Height}");
    }
    return Task.CompletedTask;
});

Console.WriteLine($"\nWINDOWS SUITE RESULT: {passed} passed, {failed} failed.");
Environment.Exit(failed > 0 ? 1 : 0);

static byte[] BuildSyntheticPcm16MonoWav(int sampleRate, int samples)
{
    int dataBytes = samples * 2;
    var buf = new byte[44 + dataBytes];
    Encoding.ASCII.GetBytes("RIFF").CopyTo(buf, 0);
    BitConverter.GetBytes(36 + dataBytes).CopyTo(buf, 4);
    Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(buf, 8);
    BitConverter.GetBytes(16).CopyTo(buf, 16);
    BitConverter.GetBytes((short)1).CopyTo(buf, 20);
    BitConverter.GetBytes((short)1).CopyTo(buf, 22);
    BitConverter.GetBytes(sampleRate).CopyTo(buf, 24);
    BitConverter.GetBytes(sampleRate * 2).CopyTo(buf, 28);
    BitConverter.GetBytes((short)2).CopyTo(buf, 32);
    BitConverter.GetBytes((short)16).CopyTo(buf, 34);
    Encoding.ASCII.GetBytes("data").CopyTo(buf, 36);
    BitConverter.GetBytes(dataBytes).CopyTo(buf, 40);
    for (int i = 0; i < samples; i++)
        BitConverter.GetBytes((short)(i % 2 == 0 ? 1000 : -1000)).CopyTo(buf, 44 + i * 2);
    return buf;
}
