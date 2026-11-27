using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Windows;
using HUDMind.Windows.Overlay;
using HUDMind.Windows.Security;

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
        if (string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"::error::{name}: {ex.Message}");
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

// W10. Win32StreamingPcmPlayer jitter prebuffer/flush/odd-byte + SAPI5 TtsRate parity (deterministic even on headless CI)
await RunTest("W10. Win32StreamingPcmPlayer prebuffer/flush/interrupt & WinRt/SAPI5 speech rate parity hold cleanly", () =>
{
    Assert(Math.Abs(WinRtNaturalVoiceService.ParseRateFactor(null) - 1.05) < 0.001, "Default natural rate should be +5% (1.05)");
    Assert(WinRtNaturalVoiceService.ParseSapiRateOffset("-10%") == -1, "-10% -> SAPI rate -1");
    Assert(WinRtNaturalVoiceService.ParseSapiRateOffset("+5%") == 0, "+5% -> SAPI rate 0");
    Assert(WinRtNaturalVoiceService.ParseSapiRateOffset("+15%") == 1, "+15% -> SAPI rate 1");
    Assert(WinRtNaturalVoiceService.ParseSapiRateOffset("+25%") == 2, "+25% -> SAPI rate 2");

    var slices = new List<int>();
    using (var vPlayer = new Win32StreamingPcmPlayer(bytes => slices.Add(bytes), headlessVirtualMode: true))
    {
        vPlayer.SetVolume(0.6);
        vPlayer.EnqueuePcm24k(new byte[12001]); // under 14400+192 startup prime + odd byte
        Assert(slices.Count == 0, $"Below 300ms startup prebuffer must hold silently (got {slices.Count})");

        vPlayer.EnqueuePcm24k(new byte[4000]); // crosses prime (16001 >= 14592) -> submits 1920 lead-in + (16000-192)=15808
        Assert(slices.Count == 1 && slices[0] == 1920 + (16000 - 192),
            $"Startup prime slice unexpected: count={slices.Count}, bytes={(slices.Count > 0 ? slices[0] : 0)}");
        Assert(vPlayer.IsActivelyPlaying && vPlayer.IsPlayingAudio, "Playback state must become active after prime submit");

        vPlayer.FlushBuffered(); // flushes retained 192+1 odd->192 byte tail + 2304 tail padding
        Assert(slices.Count == 2 && slices[1] == 192 + 2304,
            $"FlushBuffered tail slice unexpected: count={slices.Count}, bytes={(slices.Count > 1 ? slices[1] : 0)}");

        vPlayer.InterruptAndClear();
        Assert(!vPlayer.IsPlayingAudio && !vPlayer.IsActivelyPlaying, "InterruptAndClear must clear both audio & active states immediately");
    }

    // Hardware safety smoke (works cleanly with or without physical audio hardware)
    using var hwPlayer = new Win32StreamingPcmPlayer();
    hwPlayer.SetVolume(0.6);
    hwPlayer.EnqueuePcm24k(new byte[16000]);
    hwPlayer.FlushBuffered();
    hwPlayer.InterruptAndClear();
    Assert(!hwPlayer.IsPlayingAudio, "Hardware InterruptAndClear must clear IsPlayingAudio cleanly");

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

    var sixSamples = new List<TemporalFrameSample>(samples)
    {
        new(MakeTestJpeg(System.Drawing.Color.DarkMagenta), now.AddMilliseconds(200), 5UL),
        new(MakeTestJpeg(System.Drawing.Color.DarkGoldenrod), now.AddMilliseconds(400), 6UL)
    };
    var strip6 = composer.ComposeSequenceJpeg(sixSamples, 1620, 720, 84);
    Assert(strip6 is { Length: > 1000 }, "6-frame 3x2 filmstrip must produce valid JPEG bytes");
    using (var ms6 = new MemoryStream(strip6!))
    using (var img6 = System.Drawing.Image.FromStream(ms6))
    {
        Assert(img6.Width == 1620 && img6.Height == 720, $"Expected 1620x720 3x2 filmstrip, got {img6.Width}x{img6.Height}");
    }
    return Task.CompletedTask;
});

// W12. Long multi-slice DMA streaming, tail-fade holdback, and clean TTS markdown speech sanitizer
await RunTest("W12. Win32StreamingPcmPlayer slices >400ms buffers smoothly & SanitizeForSpeech removes markdown noise", () =>
{
    var clean = HUDMind.Core.AI.CoachContentGuard.SanitizeForSpeech("**Rotá** [AHORA] | guardá `boost`...");
    Assert(!clean.Contains('*') && !clean.Contains('|') && !clean.Contains('`') && !clean.Contains('['),
        $"SanitizeForSpeech left symbols that cause TTS hesitations: '{clean}'");

    var slices = new List<int>();
    using (var vPlayer = new Win32StreamingPcmPlayer(bytes => slices.Add(bytes), headlessVirtualMode: true))
    {
        vPlayer.SetVolume(0.75);
        vPlayer.EnqueuePcm24k(new byte[12000]); // held under 300ms startup prebuffer (0 slices yet)
        Assert(slices.Count == 0, "12000 bytes must stay buffered under 300ms startup prime");

        vPlayer.EnqueuePcm24k(new byte[48000]); // 1000ms chunk -> splits into <=400ms (19200 byte) DMA blocks
        Assert(slices.Count >= 4 && slices.All(s => s is > 0 and <= 19200),
            $"Multi-slice DMA blocks must all be <=19200 bytes (got [{string.Join(", ", slices)}])");

        int beforeFlush = slices.Count;
        vPlayer.FlushBuffered();
        Assert(slices.Count == beforeFlush + 1 && slices[^1] == 192 + 2304,
            $"FlushBuffered must emit retained 4ms tail-fade + 48ms zero padding ({slices[^1]})");

        vPlayer.InterruptAndClear();
        Assert(!vPlayer.IsPlayingAudio && !vPlayer.IsActivelyPlaying, "InterruptAndClear must reset states after multi-slice write");
    }

    using var hwPlayer = new Win32StreamingPcmPlayer();
    hwPlayer.SetVolume(0.75);
    hwPlayer.EnqueuePcm24k(new byte[16000]);
    hwPlayer.EnqueuePcm24k(new byte[48000]);
    hwPlayer.FlushBuffered();
    hwPlayer.InterruptAndClear();
    return Task.CompletedTask;
});

// W13. Runtime hardening integrity probe (never blocks smoke-startup or non-single-file test runner)
await RunTest("W13. RuntimeHardeningGuard & encrypted ProfileVault integrity check safely allow tests and smoke mode", () =>
{
    Assert(!HUDMind.Windows.Security.RuntimeHardeningGuard.IsExternalDebuggerAttached(isSmokeMode: true),
        "Smoke startup mode must always bypass debugger integrity block cleanly");
    Assert(!HUDMind.Windows.Security.RuntimeHardeningGuard.IsExternalDebuggerAttached(isSmokeMode: false),
        "Non-single-file dev/test assemblies must remain debuggable cleanly");

    var entries = HUDMind.Core.Security.ProfileVaultReader.GetDefaultProfileEntries();
    Assert(entries.Count >= 21 && HUDMind.Core.Security.ProfileVaultReader.IsVaultIntegrityHealthy,
        $"Encrypted vault must unseal >=21 profile assets cleanly (got {entries.Count})");
    return Task.CompletedTask;
});

// W14. Win32StreamingPcmPlayer hardware PreOpenDevice & mid-turn jitter recovery without re-priming lead-in
await RunTest("W14. Win32StreamingPcmPlayer PreOpenDevice & mid-turn gap recovery submit steady slices cleanly", () =>
{
    var slices = new List<int>();
    using (var vPlayer = new Win32StreamingPcmPlayer(bytes => slices.Add(bytes), headlessVirtualMode: true))
    {
        vPlayer.PreOpenDevice();
        vPlayer.SetVolume(0.8);
        vPlayer.EnqueuePcm24k(new byte[16000]); // primes >= 14592 -> emits 1920 + (16000 - 192)
        Assert(slices.Count == 1, $"Startup prime must emit 1 slice (got {slices.Count})");

        // Simulate mid-turn jitter drain (_inFlight hits 0 within < 950ms idle cutoff)
        vPlayer.SimulateInFlightDrainForTest();
        vPlayer.EnqueuePcm24k(new byte[2400]); // below steady threshold (4800 + 192 = 4992)
        Assert(slices.Count == 1, "2400 bytes during mid-turn recovery must buffer cleanly without tiny DMA fragments");

        vPlayer.EnqueuePcm24k(new byte[3000]); // total (192 retained + 2400 + 3000 = 5592 >= 4992) -> submits WITHOUT 1920 startup lead-in!
        Assert(slices.Count == 2 && slices[1] == 5592 - 192,
            $"Mid-turn gap recovery must emit 5400 steady bytes without re-priming 1920 lead-in (got {slices[1]})");
        vPlayer.FlushBuffered();
        vPlayer.InterruptAndClear();
        Assert(!vPlayer.IsPlayingAudio && !vPlayer.IsActivelyPlaying, "Must clear virtual state cleanly");
    }

    using var hwPlayer = new Win32StreamingPcmPlayer();
    hwPlayer.PreOpenDevice();
    hwPlayer.SetVolume(0.8);
    hwPlayer.EnqueuePcm24k(new byte[16000]);
    hwPlayer.FlushBuffered();
    hwPlayer.InterruptAndClear();
    Assert(!hwPlayer.IsPlayingAudio, "InterruptAndClear after PreOpenDevice must leave player quiescent");
    return Task.CompletedTask;
});

// W15. Passive WASAPI Core Audio monitor & zero-cut continuation queue contracts on Windows voice engines
await RunTest("W15. WasapiAudioSceneMonitor reads non-self mixer peaks safely & HybridVoiceService supports continuation queue", async () =>
{
    using var monitor = new WasapiAudioSceneMonitor();
    var snap = monitor.SampleOtherAudio();
    Assert(snap.OtherSessionsPeak01 is >= 0.0 and <= 1.0,
        $"WASAPI peak sample must stay bounded [0,1] (got {snap.OtherSessionsPeak01})");
    // Must never block or throw on STA threads
    AudioSceneSnapshot staSnap = AudioSceneSnapshot.Quiet;
    var staThread = new Thread(() => { staSnap = monitor.SampleOtherAudio(); });
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    Assert(staThread.Join(TimeSpan.FromMilliseconds(500)) && staSnap.OtherSessionsPeak01 is >= 0.0 and <= 1.0,
        "STA caller must sample WasapiAudioSceneMonitor cleanly without blocking on COM");

    using var voice = new HybridVoiceService();
    Assert(voice.SupportsContinuationQueue, "HybridVoiceService must advertise continuation queue support");
    // Concurrent WarmUp calls on SAPI/Instant fallback must remain thread-safe without dropping _ready
    using var sapi = new InstantSapiVoiceService();
    Assert(sapi.SupportsContinuationQueue, "InstantSapiVoiceService must advertise continuation queue support");
    await Task.WhenAll(
        Task.Run(() => sapi.WarmUp("es", 0.5, "male")),
        Task.Run(() => sapi.WarmUp("es", 0.5, "male")));
    sapi.StopSpeaking();
    _ = sapi.SpeakOrQueue("Uno purga", chainIfSpeaking: true, interruptCurrent: false);
    _ = sapi.SpeakOrQueue("Dos cadena", chainIfSpeaking: true, interruptCurrent: false);
    _ = sapi.SpeakOrQueue("Tres cadena", chainIfSpeaking: true, interruptCurrent: false);
    _ = sapi.SpeakOrQueue("Cuatro evicta cabeza", chainIfSpeaking: true, interruptCurrent: false);
    await Task.Delay(40);
    sapi.StopSpeaking();

    using var winRt = new WinRtNaturalVoiceService();
    _ = winRt.SpeakOrQueue("Uno interrumpe", chainIfSpeaking: false, interruptCurrent: true);
    _ = winRt.SpeakOrQueue("Dos cadena", chainIfSpeaking: true, interruptCurrent: false);
    _ = winRt.SpeakOrQueue("Tres evicta manteniendo interrupcion", chainIfSpeaking: true, interruptCurrent: false);
    await Task.Delay(30);
    winRt.StopSpeaking();
});

// W16. Native Windows DPAPI (crypt32.dll), HKCU Startup Opt-In Safety & Quick-Chat Window Helpers
await RunTest("W16. Win32DpapiSecretProtector crypt32 round-trip, WindowsStartupManager HKCU opt-in & foreground focus guards", () =>
{
    var dpapi = new Win32DpapiSecretProtector();
    Assert(dpapi.IsSupported, "DPAPI must be supported on Windows");
    string sealedVal = dpapi.Protect("AIzaRealSampleSecret987654");
    Assert(sealedVal.StartsWith(ConfigRepository.ProtectedSecretPrefix, StringComparison.Ordinal) &&
           !sealedVal.Contains("AIzaRealSampleSecret987654", StringComparison.Ordinal),
        "DPAPI Protect must emit dpapi: prefix without plaintext leakage");
    Assert(dpapi.Unprotect(sealedVal) == "AIzaRealSampleSecret987654", "DPAPI Unprotect must recover exact secret");
    Assert(dpapi.Protect(string.Empty) == string.Empty && dpapi.Unprotect(string.Empty) == string.Empty, "Empty secrets stay empty");

    // Tampered DPAPI payload must throw CryptographicException
    var tamperedBytes = Convert.FromBase64String(sealedVal[ConfigRepository.ProtectedSecretPrefix.Length..]);
    tamperedBytes[^1] ^= 0xFF;
    bool threwTamper = false;
    try { _ = dpapi.Unprotect($"{ConfigRepository.ProtectedSecretPrefix}{Convert.ToBase64String(tamperedBytes)}"); }
    catch (CryptographicException) { threwTamper = true; }
    Assert(threwTamper, "Tampered DPAPI blob must fail verification");

    // Isolated HKCU registry subkey test for WindowsStartupManager (default stays off unless explicitly applied)
    Assert(!WindowsStartupManager.IsSafeExecutablePath(@"\\unc-share\evil\hudmind.exe"), "UNC path blocked for startup Run key");
    string testSubKey = $@"Software\HUDMind_Test_{Guid.NewGuid():N}";
    try
    {
        Assert(!WindowsStartupManager.IsEnabled(testSubKey), "New startup key starts disabled by default");
        string dummyExe = Environment.ProcessPath ?? Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (File.Exists(dummyExe) && dummyExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Assert(WindowsStartupManager.Apply(true, dummyExe, testSubKey) && WindowsStartupManager.IsEnabled(testSubKey),
                "Startup opt-in must write quoted --minimized Run entry");
            Assert(WindowsStartupManager.Apply(false, subKey: testSubKey) && !WindowsStartupManager.IsEnabled(testSubKey),
                "Disabling Startup must remove Run entry cleanly");
        }
    }
    finally
    {
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testSubKey, throwOnMissingSubKey: false); } catch { }
    }

    Assert(!Win32WindowDetector.TryRestoreForegroundWindow(IntPtr.Zero), "Null HWND restore returns false safely");
    return Task.CompletedTask;
});

// W17. Win32DisplayHelper active monitor resolution, FastScreenCapture SparseLumaDelta & TextOnlySensorService OCR cache + fresh telemetry
await RunTest("W17. Win32DisplayHelper.TryGetMonitorIndexForWindow, SparseLumaDelta & OCR-cached fresh telemetry", async () =>
{
    Assert(Win32DisplayHelper.TryGetMonitorIndexForWindow(IntPtr.Zero) is null,
        "TryGetMonitorIndexForWindow(IntPtr.Zero) must return null");
    var areas = Win32DisplayHelper.GetAllWorkingAreas();
    IntPtr selfHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
    if (selfHwnd != IntPtr.Zero)
    {
        int? mon = Win32DisplayHelper.TryGetMonitorIndexForWindow(selfHwnd);
        Assert(mon is >= 1 && mon <= areas.Length,
            $"Valid window handle monitor index must stay inside 1..{areas.Length} (got {mon})");
    }

    using var cap = new FastScreenCapture();
    var (f1, skip1) = cap.CaptureIfChanged(1, null, 640, 360, 75, 1, null, 4, isForced: true);
    if (f1 is not null)
        Assert(!skip1 && f1.SparseLumaDelta >= 0.0, "Forced capture must report SparseLumaDelta >= 0");

    // Verify TextOnlySensorService caches expensive OCR per identical image while ALWAYS reading live telemetry!
    int telemetryCalls = 0;
    var mockSensor = new StubDynamicTelemetrySensor(() =>
    {
        int call = Interlocked.Increment(ref telemetryCalls);
        return call == 1 ? "RL_PHASE=PLAY" : "RL_PHASE=MENU";
    });
    var svc = new TextOnlySensorService(additionalSensors: [mockSensor]);
    var dummyProfile = new HUDMind.Core.Models.GameProfile
    {
        DirectoryPath = ".",
        Manifest = new HUDMind.Core.Models.GameManifest { GameId = "rocket_league", DisplayName = "Rocket League" }
    };
    byte[] minimalImg = [0xFF, 0xD8, 0xFF, 0xD9];
    var snap1 = await svc.ExtractAsync(minimalImg, dummyProfile);
    var snap2 = await svc.ExtractAsync(minimalImg, dummyProfile);
    Assert(telemetryCalls >= 2 && snap2.LogSummary.Contains("MENU", StringComparison.Ordinal),
        $"Cached OCR window must still poll fresh telemetry on every call (calls={telemetryCalls}, log2={snap2.LogSummary})");
});

// W18. LoopbackRconToastBridge loopback RFC 6455 auth + toast sanitization & RocketLeagueLogSensor PII strip
await RunTest("W18. LoopbackRconToastBridge WebSocket toast + RocketLeagueLogSensor PII safety", async () =>
{
    var received = new List<string>();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

    var serverTask = Task.Run(async () =>
    {
        using var tcp = await listener.AcceptTcpClientAsync(serverCts.Token);
        await using var ns = tcp.GetStream();
        using var reader = new StreamReader(ns, Encoding.ASCII, leaveOpen: true);
        string? secKey = null;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(serverCts.Token)))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                secKey = line["Sec-WebSocket-Key:".Length..].Trim();
        }
        byte[] acceptHash = System.Security.Cryptography.SHA1.HashData(
            Encoding.ASCII.GetBytes($"{secKey}258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
        string accept = Convert.ToBase64String(acceptHash);
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), serverCts.Token);

        while (!serverCts.Token.IsCancellationRequested && received.Count < 2)
        {
            string? frameMsg = await ReadMaskedWsTextFrameAsync(ns, serverCts.Token);
            if (frameMsg is null) break;
            received.Add(frameMsg);
            if (frameMsg.StartsWith("rcon_password ", StringComparison.Ordinal))
                await WriteUnmaskedWsTextFrameAsync(ns, "authyes", serverCts.Token);
        }
    });

    await using var rconClient = new LoopbackRconToastBridge(
        isEnabled: () => true,
        isSupportedGameActive: () => true,
        settingsResolver: () => new HUDMind.Core.Overlay.LoopbackRconBridgeSettings(true, port, "TestPass99"));

    // CoachLive partial MUST be ignored (no toast flood); CoachFinal MUST send sanitized command!
    rconClient.ShowMessage(new OverlayCalloutMessage("Parcial...", UrgencyLevel.Low, "Rocket League", 3.0, OverlayEntryKind.CoachLive, "Coach"));
    rconClient.ShowMessage(new OverlayCalloutMessage("Rotá segundo palo; quit", UrgencyLevel.High, "Rocket League", 4.0, OverlayEntryKind.CoachFinal, "Coach"));

    await serverTask;
    Assert(received.Count >= 2 &&
           received[0] == "rcon_password TestPass99" &&
           received[1] == "toast \"HUDMind\" \"Rotá segundo palo quit\"",
        $"Unexpected RCON WebSocket sequence: {string.Join(" | ", received)}");

    // Verify RocketLeagueLogSensor tail rules do not leak player '?Name=' query args
    var tmpRl = Path.Combine(Path.GetTempPath(), $"hudmind_w18_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpRl);
    try
    {
        var logFile = Path.Combine(tmpRl, "Launch.log");
        File.WriteAllText(logFile, "[0090.09] Log: LoadMap: 15.228.137.3:9035/CHN_Stadium_Day_P?Name=SecretPlayer?game=TAGame.GameInfo_Soccar_TA\n");
        var rlSensor = new RocketLeagueLogSensor(logFile);
        var rlProf = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rocket_league", DisplayName = "Rocket League", ProcessNames = ["RocketLeague.exe"] } };
        string matched = await rlSensor.ReadAsync(rlProf);
        Assert(matched.Contains("Partido Soccar activo", StringComparison.Ordinal) &&
               !matched.Contains("SecretPlayer", StringComparison.Ordinal) &&
               !matched.Contains("15.228.137.3", StringComparison.Ordinal),
            $"RocketLeagueLogSensor must classify match state cleanly without leaking IP or player name: {matched}");
    }
    finally
    {
        try { Directory.Delete(tmpRl, recursive: true); } catch { }
    }
});

// W19. InGameOverlayBridgeSetup assisted rcon_enabled config + LoopbackWebSocketTelemetrySensor configurable multi-game WS feed
await RunTest("W19. InGameOverlayBridgeSetup rcon_enabled automation & configurable multi-game WS sensor", async () =>
{
    var tmpBridge = Path.Combine(Path.GetTempPath(), $"hudmind_w19_{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(tmpBridge, "cfg"));
    try
    {
        File.WriteAllText(Path.Combine(tmpBridge, "cfg", "config.cfg"), "rcon_port \"9004\"\nrcon_enabled \"0\"\n");
        var (ok, _, msg) = await HUDMind.Windows.Overlay.InGameOverlayBridgeSetup.ConfigureDirectoryAsync(tmpBridge, attemptLiveVerify: false);
        Assert(ok, $"ConfigureDirectoryAsync failed: {msg}");

        var allowText = File.ReadAllText(Path.Combine(tmpBridge, "data", "rcon_commands.cfg"));
        var cfgText = File.ReadAllText(Path.Combine(tmpBridge, "cfg", "config.cfg"));
        var loaded = HUDMind.Windows.Overlay.InGameOverlayBridgeSetup.TryLoadBridgeSettings(tmpBridge);
        Assert(allowText.Contains("toast", StringComparison.Ordinal) &&
               loaded is { Enabled: true, Port: 9004 } &&
               !string.IsNullOrWhiteSpace(loaded.SecretToken) &&
               !cfgText.Contains("rcon_enabled \"0\"", StringComparison.Ordinal),
            $"Assisted bridge setup must allowlist 'toast', enable rcon_enabled & keep port 9004 (cfg={cfgText})");
    }
    finally
    {
        try { Directory.Delete(tmpBridge, recursive: true); } catch { }
    }

    // Live loopback WebSocket server on dynamic port feeding generic hudmind:telemetry to another game profile (cs2)
    using var wsListener = new TcpListener(IPAddress.Loopback, 0);
    wsListener.Start();
    int wsPort = ((IPEndPoint)wsListener.LocalEndpoint).Port;
    using var wsCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

    var wsServer = Task.Run(async () =>
    {
        using var tcp = await wsListener.AcceptTcpClientAsync(wsCts.Token);
        await using var ns = tcp.GetStream();
        using var reader = new StreamReader(ns, Encoding.ASCII, leaveOpen: true);
        string? secKey = null;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(wsCts.Token)))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                secKey = line["Sec-WebSocket-Key:".Length..].Trim();
        }
        byte[] acceptHash = System.Security.Cryptography.SHA1.HashData(
            Encoding.ASCII.GetBytes($"{secKey}258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {Convert.ToBase64String(acceptHash)}\r\n\r\n"), wsCts.Token);

        await WriteUnmaskedWsTextFrameAsync(ns,
            "{\"event\":\"hudmind:telemetry\",\"phase\":\"play\",\"resource\":10,\"fact\":\"Eco buy round\"}",
            wsCts.Token);
        await Task.Delay(250, wsCts.Token);
    });

    using var sensor = new LoopbackWebSocketTelemetrySensor(port: wsPort, enabled: true);
    var cs2Prof = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "cs2", DisplayName = "Counter-Strike 2" } };
    Assert(sensor.Supports(cs2Prof), "Configurable LoopbackWebSocketTelemetrySensor must support non-desktop profiles");

    string telemetry = string.Empty;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < 3.0)
    {
        telemetry = await sensor.ReadAsync(cs2Prof);
        if (!string.IsNullOrEmpty(telemetry))
            break;
        await Task.Delay(60);
    }
    await wsServer;
    Assert(telemetry.Contains("LOCAL_WS_TELEMETRY:", StringComparison.Ordinal) &&
           telemetry.Contains("Eco buy round", StringComparison.Ordinal),
        $"Expected live generic WS telemetry snapshot on custom port {wsPort} (got '{telemetry}')");
});

// W20. Multi-profile Windows DPAPI workspace secret isolation & encrypted archive interoperability
await RunTest("W20. UserProfileStore + Win32DpapiSecretProtector isolates per-profile API secrets on Windows", () =>
{
    var tmpRoot = Path.Combine(Path.GetTempPath(), $"hudmind_win_profiles_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpRoot);
    try
    {
        var store = new UserProfileStore(tmpRoot);
        var userA = store.CreateProfile("Jugador Alpha", "⚡", "#22D3EE", "cs2", pin: "1357");
        var userB = store.CreateProfile("Jugador Beta", "🔥", "#F59E0B", "rocket_league");
        Assert(UserProfileStore.VerifyPin(userA.PinHash, "1357") && !UserProfileStore.VerifyPin(userA.PinHash, "0000"),
            "Windows profile PBKDF2 PIN mismatch");

        var dpapi = new Win32DpapiSecretProtector();
        var repoA = new ConfigRepository(store.ResolveWorkspaceRoot(userA.ProfileId), secretProtector: dpapi);
        var repoB = new ConfigRepository(store.ResolveWorkspaceRoot(userB.ProfileId), secretProtector: dpapi);

        repoA.SaveAccounts([new ProviderAccount { Label = "AlphaKey", Provider = "gemini", ApiKey = "AIzaWinAlphaSecretKey987654", Enabled = true }]);
        repoB.SaveAccounts([new ProviderAccount { Label = "BetaKey", Provider = "openrouter", ApiKey = "sk-or-v1-WinBetaSecret123456", Enabled = true }]);

        string rawA = File.ReadAllText(repoA.AccountsPath, Encoding.UTF8);
        string rawB = File.ReadAllText(repoB.AccountsPath, Encoding.UTF8);
        Assert(rawA.Contains(ConfigRepository.ProtectedSecretPrefix, StringComparison.Ordinal) &&
               !rawA.Contains("AIzaWinAlphaSecretKey987654", StringComparison.Ordinal) &&
               !rawA.Contains("sk-or-v1-WinBetaSecret123456", StringComparison.Ordinal) &&
               rawB.Contains(ConfigRepository.ProtectedSecretPrefix, StringComparison.Ordinal),
            "Raw accounts.json files must be DPAPI sealed and cross-profile isolated");

        var loadedA = repoA.LoadAccounts();
        var loadedB = repoB.LoadAccounts();
        Assert(loadedA.Count == 1 && loadedA[0].ApiKey == "AIzaWinAlphaSecretKey987654" &&
               loadedB.Count == 1 && loadedB[0].ApiKey == "sk-or-v1-WinBetaSecret123456",
            "Per-profile DPAPI unprotect mismatch");
    }
    finally
    {
        try { Directory.Delete(tmpRoot, recursive: true); } catch { }
    }
    return Task.CompletedTask;
});

// W21. SingleInstancePipeServer IPC wakeup, SystemDiagnosticsProbe health checks & multi-monitor pixel clamping
await RunTest("W21. SingleInstancePipeServer wakeup, SystemDiagnosticsProbe & Win32DisplayHelper clamp work on Windows", async () =>
{
    string pipeName = $"hudmind_win_test_pipe_{Guid.NewGuid():N}";
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var server = new SingleInstancePipeServer(pipeName, cmd => received.TrySetResult(cmd));

    bool sent = SingleInstancePipeServer.TrySignalExistingInstance(pipeName, "SHOW", timeoutMs: 1500);
    Assert(sent, "TrySignalExistingInstance must deliver SHOW command to active current-user named pipe");
    var completed = await Task.WhenAny(received.Task, Task.Delay(2000));
    Assert(completed == received.Task && string.Equals(await received.Task, "SHOW", StringComparison.Ordinal),
        "SingleInstancePipeServer must invoke SHOW handler cleanly");

    var clamped = Win32DisplayHelper.ClampPixelRectToWorkingArea(
        -5000,
        9999,
        440,
        120,
        [new MonitorBoundsRect(0, 0, 1920, 1040)]);
    Assert(clamped.Left >= 0 && clamped.Left <= 1920 - 440 &&
           clamped.Top >= 0 && clamped.Top <= 1040 - 120,
        $"Expected off-screen overlay bounds clamped inside (0..1920, 0..1040), got ({clamped.Left},{clamped.Top})");

    var tmpDiagRoot = Path.Combine(Path.GetTempPath(), $"hudmind_win_diag_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDiagRoot);
    try
    {
        var diagRepo = new HUDMind.Core.Config.ConfigRepository(tmpDiagRoot);
        var checks = await SystemDiagnosticsProbe.RunChecksAsync(
            new HUDMind.Core.Models.UserConfig(),
            diagRepo,
            "SoftwareOnly (0% GPU)",
            isCompanionActive: false,
            isGsiActive: false,
            probeMicSample: false);
        Assert(checks.Count >= 6 &&
               checks.Any(c => c.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) && c.Detail.Contains("SoftwareOnly", StringComparison.OrdinalIgnoreCase) && c.Passed) &&
               checks.Any(c => c.Name.Contains("Workspace", StringComparison.OrdinalIgnoreCase) && c.Passed),
            $"Expected SystemDiagnosticsProbe to report valid GPU SoftwareOnly and Workspace checks (count={checks.Count})");
    }
    finally
    {
        try { Directory.Delete(tmpDiagRoot, recursive: true); } catch { }
    }
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

static async Task<string?> ReadMaskedWsTextFrameAsync(NetworkStream ns, CancellationToken ct)
{
    byte[] hdr = new byte[2];
    if (await ns.ReadAsync(hdr, ct) < 2) return null;
    int len = hdr[1] & 0x7F;
    if (len == 126)
    {
        byte[] ext = new byte[2];
        await ns.ReadExactlyAsync(ext, ct);
        len = (ext[0] << 8) | ext[1];
    }
    byte[] mask = new byte[4];
    if ((hdr[1] & 0x80) != 0)
        await ns.ReadExactlyAsync(mask, ct);
    byte[] payload = new byte[len];
    await ns.ReadExactlyAsync(payload, ct);
    if ((hdr[1] & 0x80) != 0)
    {
        for (int i = 0; i < len; i++)
            payload[i] ^= mask[i & 3];
    }
    return Encoding.UTF8.GetString(payload);
}

static Task WriteUnmaskedWsTextFrameAsync(NetworkStream ns, string text, CancellationToken ct)
{
    byte[] data = Encoding.UTF8.GetBytes(text);
    byte[] frame = new byte[2 + data.Length];
    frame[0] = 0x81;
    frame[1] = (byte)data.Length;
    Buffer.BlockCopy(data, 0, frame, 2, data.Length);
    return ns.WriteAsync(frame, ct).AsTask();
}

sealed class StubDynamicTelemetrySensor(Func<string> onRead) : IGameTelemetrySensor
{
    public string SensorId => "stub-dynamic";
    public bool Supports(HUDMind.Core.Models.GameProfile profile) => true;
    public Task<string> ReadAsync(HUDMind.Core.Models.GameProfile profile, CancellationToken ct = default) =>
        Task.FromResult(onRead());
}
