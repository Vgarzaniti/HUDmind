using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;

int passed = 0;
int failed = 0;

async Task RunTest(string name, Func<Task> body)
{
    try
    {
        await body();
        Console.WriteLine($"[PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        failed++;
    }
}

void Assert(bool cond, string msg)
{
    if (!cond) throw new Exception(msg);
}

// 1. Compact Wire Protocol Parser
await RunTest("01. CompactWireProtocol parses H/M/L, NONE, and JSON fallback", () =>
{
    var rNone = CompactWireProtocol.ParseCompactLine("NONE");
    Assert(rNone is { ShouldIntervene: false }, "NONE should not intervene");

    var rHigh = CompactWireProtocol.ParseCompactLine("H|Rotá segundo palo, tenés 18 de boost");
    Assert(rHigh is { ShouldIntervene: true, Urgency: UrgencyLevel.High }, "H should parse as High");
    Assert(rHigh!.OverlayText.Contains("18 de boost"), "OverlayText preserved");

    var rJson = CompactWireProtocol.ParseCompactLine("{\"should_intervene\":true,\"urgency\":\"medium\",\"overlay_text\":\"Usá flash\",\"voice_line\":\"Tirá flash\"}");
    Assert(rJson is { ShouldIntervene: true, Urgency: UrgencyLevel.Medium, OverlayText: "Usá flash" }, "JSON fallback works");
    return Task.CompletedTask;
});

// 2. 64-bit dHash + Hardware POPCNT Hamming Distance
await RunTest("02. PerceptualHasher computes deterministic 64-bit dHash & Hamming distance", () =>
{
    byte[] imgA = new byte[72];
    byte[] imgB = new byte[72];
    for (int i = 0; i < 72; i++)
    {
        imgA[i] = (byte)(i * 3);
        imgB[i] = (byte)(255 - i * 3);
    }
    ulong hA = PerceptualHasher.ComputeDHash64(imgA);
    ulong hB = PerceptualHasher.ComputeDHash64(imgB);
    Assert(PerceptualHasher.HammingDistance(hA, hA) == 0, "Identical image must have 0 distance");
    Assert(PerceptualHasher.HammingDistance(hA, hB) >= 32, "Inverted gradient must have high Hamming distance");
    return Task.CompletedTask;
});

// 3. Anti-Repetition Memory + TTL
await RunTest("03. InterventionMemory blocks semantic duplicates and expires by TTL", () =>
{
    var mem = new InterventionMemory(maxItems: 5, ttlSeconds: 60);
    var t0 = DateTimeOffset.UtcNow;
    var r1 = new CoachResponse(true, UrgencyLevel.Medium, "Rotá segundo palo con poco boost", "Rotá segundo palo");
    var rDup = new CoachResponse(true, UrgencyLevel.Medium, "Rotá al segundo palo por poco boost", "Rotá");
    var rNew = new CoachResponse(true, UrgencyLevel.High, "Cuidá el contragolpe rival por derecha", "Cuidá contragolpe");

    mem.Record(r1, t0);
    Assert(mem.IsRepetitive(rDup, t0.AddSeconds(10)), "Near duplicate should be flagged repetitive");
    Assert(!mem.IsRepetitive(rNew, t0.AddSeconds(10)), "Distinct callout should pass");
    Assert(!mem.IsRepetitive(rDup, t0.AddSeconds(120)), "Expired entry should no longer block");
    return Task.CompletedTask;
});

// 4. OWASP Secret Redactor (Gemini, Groq, Bearer, query params, HF, xAI)
await RunTest("04. SecretRedactor masks all key prefixes, Bearer tokens, and URL query keys", () =>
{
    var raw = "AQ.Ab8RN6L_testsecret12345 gsk_abcd1234567890efgh Bearer tok_abcdef123456 ?api_key=mysecret999 hf_1234567890abcd";
    var clean = SecretRedactor.Redact(raw);
    Assert(!clean.Contains("Ab8RN6L") && !clean.Contains("gsk_") && !clean.Contains("tok_abcdef") && !clean.Contains("mysecret999") && !clean.Contains("hf_1234"),
        $"Secrets leaked: {clean}");
    return Task.CompletedTask;
});

// 5. OWASP SSRF (CWE-918) & Cleartext HTTP (CWE-319) Validator
await RunTest("05. BaseUrlSecurityValidator blocks cleartext HTTP, metadata SSRF, RFC1918 & userinfo", () =>
{
    Assert(BaseUrlSecurityValidator.Validate("https://api.groq.com/openai/v1").IsValid, "HTTPS remote allowed");
    Assert(BaseUrlSecurityValidator.Validate("http://localhost:11434/v1", allowLoopbackHttp: true).IsValid, "Loopback HTTP allowed for Ollama");
    Assert(!BaseUrlSecurityValidator.Validate("http://api.remote.example/v1", allowLoopbackHttp: false).IsValid, "Remote HTTP blocked");
    Assert(!BaseUrlSecurityValidator.Validate("https://169.254.169.254/latest/meta-data").IsValid, "Metadata IP blocked");
    Assert(!BaseUrlSecurityValidator.Validate("https://192.168.1.50/v1").IsValid, "LAN RFC1918 blocked");
    Assert(!BaseUrlSecurityValidator.Validate("https://user:pass@api.openai.com/v1").IsValid, "UserInfo blocked");
    Assert(!BaseUrlSecurityValidator.Validate("file:///C:/Windows/win.ini").IsValid, "File scheme blocked");
    return Task.CompletedTask;
});

// 6. ConfigRepository Isolation, [JsonExtensionData] Preservation, Corrupt Backup & Path Traversal (CWE-22)
await RunTest("06. ConfigRepository preserves Python ExtraFields, backs up corrupt JSON & blocks CWE-22", () =>
{
    var tmp = Path.Combine(Path.GetTempPath(), $"hudmind_test_{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(Path.Combine(tmp, "config"));
        Directory.CreateDirectory(Path.Combine(tmp, "profiles", "demo_game"));
        File.WriteAllText(Path.Combine(tmp, "config", "user_config.json"),
            "{\"coach_mode\":\"auto\",\"tts_voice\":\"es-AR-TomasNeural\",\"overlay_theme\":\"minimal-glass\"}");
        File.WriteAllText(Path.Combine(tmp, "profiles", "demo_game", "manifest.json"),
            "{\"game_id\":\"demo_game\",\"display_name\":\"Demo Game\",\"process_names\":[\"demo.exe\"]}");

        var repo = new ConfigRepository(tmp);
        var cfg = repo.LoadUserConfig();
        cfg.TtsVolume = 0.65;
        repo.SaveUserConfig(cfg);

        var savedJson = File.ReadAllText(Path.Combine(tmp, "config", "user_config.json"));
        Assert(savedJson.Contains("es-AR-TomasNeural") && savedJson.Contains("minimal-glass"), "Python fields preserved!");

        // Path traversal block
        var profiles = repo.LoadProfiles();
        Assert(profiles.Count == 1, "Loaded demo_game");
        var maliciousProfile = new GameProfile { Manifest = profiles[0].Manifest, DirectoryPath = Path.Combine(tmp, "config") };
        bool threw = false;
        try { repo.SaveCustomRules(maliciousProfile, "hack"); }
        catch (UnauthorizedAccessException) { threw = true; }
        Assert(threw, "SaveCustomRules must reject paths outside ProfilesDir");

        // Corrupt accounts backup
        File.WriteAllText(Path.Combine(tmp, "config", "accounts.json"), "{ BROKEN JSON !!!");
        var accs = repo.LoadAccounts();
        Assert(accs.Count >= 1 && !string.IsNullOrEmpty(repo.LastLoadWarning), "Fallback + warning on corrupt JSON");
        Assert(Directory.GetFiles(Path.Combine(tmp, "config"), "accounts.json.corrupt.*.bak").Length == 1, "Corrupt backup created");
    }
    finally
    {
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
    return Task.CompletedTask;
});

// 7. Hedged Vision Racing beats stalled primary
await RunTest("07. ProviderFailoverRouter Hedged Vision beats stalled primary without unobserved errors", async () =>
{
    var a1 = new ProviderAccount { Id = "p1", Label = "SlowPrimary", Provider = "gemini", ApiKey = "AIzaTest12345678", Model = "gemini-2.5-flash", Priority = 1 };
    var a2 = new ProviderAccount { Id = "p2", Label = "FastHedge", Provider = "groq", ApiKey = "gsk_test12345678", Model = "llama-4-scout", Priority = 2 };
    var expected = new CoachResponse(true, UrgencyLevel.High, "Cuidá segundo palo", "Cuidá segundo palo");

    await using var router = new ProviderFailoverRouter(
        [a1, a2],
        factory: acc => new StubProvider(acc, acc.Id == "p1" ? 1200 : 20, new ProviderCallResult(expected)),
        hedgeDelaySeconds: 0.12
    );

    var profile = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rl", DisplayName = "Rocket League" } };
    var sw = Stopwatch.StartNew();
    var (resp, _, model) = await router.AnalyzeFrameAsync([1, 2, 3], profile, new UserConfig(), "", false);
    sw.Stop();

    Assert(resp == expected && model == "llama-4-scout", $"Expected hedge winner, got {model}");
    Assert(sw.ElapsedMilliseconds < 500, $"Hedge should beat 1200ms primary (took {sw.ElapsedMilliseconds}ms)");
});

// 8. Failover on HTTP 429 & HTTP 422 (does NOT abort failover on 422!)
await RunTest("08. ProviderFailoverRouter fails over cleanly on 422 and 429 to backup account", async () =>
{
    var a1 = new ProviderAccount { Id = "p1", Label = "P1", Provider = "gemini", ApiKey = "AIzaTest12345678", Model = "gemini-3.5-flash-lite", Priority = 1 };
    var a2 = new ProviderAccount { Id = "p2", Label = "P2", Provider = "openrouter", ApiKey = "sk-or-test123456", Model = "google/gemma:free", Priority = 2 };
    var expected = new CoachResponse(true, UrgencyLevel.Medium, "Rotá atrás", "Rotá atrás");

    Assert(!a2.IsFastCloudVision, "OpenRouter :free must be excluded from IsFastCloudVision hedging");

    await using var router = new ProviderFailoverRouter(
        [a1, a2],
        factory: acc => acc.Id == "p1"
            ? new StubProvider(acc, 5, new ProviderCallResult(null, 422, "empty"))
            : new StubProvider(acc, 5, new ProviderCallResult(expected))
    );

    var profile = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rl", DisplayName = "Rocket League" } };
    var (resp, _, model) = await router.AnalyzeFrameAsync([1, 2, 3], profile, new UserConfig(), "", false);
    Assert(resp == expected && model == "google/gemma:free", "422 must fall back to backup provider");
});

// 9. Freshness Deadline discards slow automatic advice but keeps manual F10
await RunTest("09. ProviderFailoverRouter Freshness Deadline discards slow auto advice, keeps F10", async () =>
{
    var a1 = new ProviderAccount { Id = "p1", Label = "Slow", Provider = "gemini", ApiKey = "AIzaTest12345678", Model = "gemini-2.5-flash", Priority = 1 };
    var callout = new CoachResponse(true, UrgencyLevel.High, "Viejo", "Viejo");

    await using var router = new ProviderFailoverRouter(
        [a1],
        factory: acc => new StubProvider(acc, 180, new ProviderCallResult(callout)),
        freshnessDeadlineMs: 100.0
    );

    var profile = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rl", DisplayName = "Rocket League" } };
    var (autoResp, _, _) = await router.AnalyzeFrameAsync([1, 2, 3], profile, new UserConfig(), "", forceAdvice: false);
    Assert(autoResp is { ShouldIntervene: false }, "Stale auto advice must be silenced");

    var (f10Resp, _, _) = await router.AnalyzeFrameAsync([1, 2, 3], profile, new UserConfig(), "", forceAdvice: true);
    Assert(f10Resp is { ShouldIntervene: true }, "Manual F10 advice must be delivered");
});

// 10. GeminiFastProvider HTTP stub verifies x-goog-api-key header, stopSequences & Gen3 MINIMAL maxOutputTokens=64
await RunTest("10. GeminiFastProvider sends x-goog-api-key header, stopSequences & Gen3 64-token budget", async () =>
{
    string? seenBody = null;
    string? seenHeaderKey = null;
    string? seenUrl = null;
    var handler = new FakeHttpHandler(async req =>
    {
        seenUrl = req.RequestUri?.ToString();
        seenHeaderKey = req.Headers.TryGetValues("x-goog-api-key", out var vals) ? vals.FirstOrDefault() : null;
        seenBody = await req.Content!.ReadAsStringAsync();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"H|Cuidá segundo palo\"}]}}]}", Encoding.UTF8, "application/json")
        };
    });

    var acc = new ProviderAccount { Provider = "gemini", ApiKey = "AIzaSecretHeaderTest999", Model = "gemini-3.5-flash-lite" };
    await using var provider = new GeminiFastProvider(acc, new HttpClient(handler));
    var profile = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rl", DisplayName = "Rocket League" } };
    var res = await provider.AnalyzeFrameAsync([0xFF, 0xD8, 0xFF, 0xD9], profile, new UserConfig(), "", false);

    Assert(res.Response is { ShouldIntervene: true, Urgency: UrgencyLevel.High }, "Parsed Gemini response");
    Assert(seenHeaderKey == "AIzaSecretHeaderTest999", "Key must be in x-goog-api-key header");
    Assert(seenUrl is not null && !seenUrl.Contains("AIzaSecret"), "Key must NEVER appear in URL");
    Assert(seenBody is not null && seenBody.Contains("\"maxOutputTokens\":64") && seenBody.Contains("\"stopSequences\":[\"\\n\"]") && seenBody.Contains("\"thinkingLevel\":\"MINIMAL\""),
        $"Unexpected Gemini payload: {seenBody}");
});

// 11. AnthropicClaudeProvider HTTP stub verifies headers & /v1/messages image payload
await RunTest("11. AnthropicClaudeProvider formats x-api-key, anthropic-version & base64 image block", async () =>
{
    string? seenKey = null;
    string? seenVer = null;
    string? seenBody = null;
    var handler = new FakeHttpHandler(async req =>
    {
        seenKey = req.Headers.TryGetValues("x-api-key", out var k) ? k.FirstOrDefault() : null;
        seenVer = req.Headers.TryGetValues("anthropic-version", out var v) ? v.FirstOrDefault() : null;
        seenBody = await req.Content!.ReadAsStringAsync();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"M|Juntá boost chico\"}]}", Encoding.UTF8, "application/json")
        };
    });

    var acc = new ProviderAccount { Provider = "claude", ApiKey = "sk-ant-test12345678", Model = "claude-3-5-haiku-latest" };
    await using var provider = new AnthropicClaudeProvider(acc, new HttpClient(handler));
    var profile = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rl", DisplayName = "Rocket League" } };
    var res = await provider.AnalyzeFrameAsync([0xFF, 0xD8, 0xFF, 0xD9], profile, new UserConfig(), "", false);

    Assert(res.Response is { ShouldIntervene: true, OverlayText: "Juntá boost chico" }, "Parsed Claude response");
    Assert(seenKey == "sk-ant-test12345678" && seenVer == "2023-06-01", "Anthropic headers verified");
    Assert(seenBody is not null && seenBody.Contains("\"media_type\":\"image/jpeg\"") && seenBody.Contains("\"stop_sequences\":[\"\\n\"]"), "Claude body verified");
});

// 12. CoachOrchestrator F10 instant wake + pause sync + delivery
await RunTest("12. CoachOrchestrator wakes immediately on F10, delivers to overlay & voice, and syncs pause", async () =>
{
    var a1 = new ProviderAccount { Id = "p1", Label = "Stub", Provider = "gemini", ApiKey = "AIzaTest12345678", Model = "gemini-2.5-flash", Priority = 1 };
    var expected = new CoachResponse(true, UrgencyLevel.High, "Defendé arco", "Defendé arco");
    var router = new ProviderFailoverRouter([a1], factory: acc => new StubProvider(acc, 5, new ProviderCallResult(expected)));
    var profile = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rl", DisplayName = "Rocket League", CaptureFps = 0.5, CooldownSeconds = 30 } };

    var overlay = new RecordingOverlay();
    var voice = new RecordingVoice();
    var pauseEvents = new List<bool>();

    await using var orch = new CoachOrchestrator(
        new UserConfig { EnableVoice = true, EnableOverlay = true, MinInferenceIntervalSeconds = 10 },
        [profile],
        router,
        new StubWindowDetector("RocketLeague.exe"),
        new StubCapture(),
        voice,
        overlay,
        testMode: true,
        forcedGameId: "rl",
        onPauseChanged: p => pauseEvents.Add(p)
    );

    await orch.StartAsync();
    orch.TogglePause();
    Assert(orch.IsPaused && pauseEvents.Last() == true, "Pause toggled on");

    var sw = Stopwatch.StartNew();
    orch.TriggerOnDemandAnalysis(); // Unpauses + triggers <1ms wake!
    for (int i = 0; i < 40 && voice.Spoken.Count == 0; i++)
        await Task.Delay(25);
    sw.Stop();

    Assert(!orch.IsPaused && pauseEvents.Last() == false, "F10 unpaused and notified UI");
    Assert(voice.Spoken.Contains("Defendé arco"), "Voice spoke F10 advice");
    Assert(overlay.Shown.Any(s => s.Contains("Defendé arco")), "Overlay showed F10 advice");
    Assert(sw.ElapsedMilliseconds < 800, $"F10 should bypass 2000ms capture sleep (took {sw.ElapsedMilliseconds}ms)");
});

Console.WriteLine($"\nRESULT: {passed} passed, {failed} failed.");
Environment.Exit(failed > 0 ? 1 : 0);

sealed class StubProvider(ProviderAccount account, int delayMs, ProviderCallResult outcome) : IAIProvider
{
    public ProviderAccount Account { get; } = account;
    public Task PrewarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async Task<ProviderCallResult> AnalyzeFrameAsync(byte[] jpegBytes, GameProfile profile, UserConfig config, string recentHistory, bool forceAdvice, string spatialTelemetry = "", CancellationToken ct = default)
    {
        await Task.Delay(delayMs, ct);
        return outcome;
    }
    public Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        handler(request);
}

sealed class StubWindowDetector(string exe) : IForegroundWindowDetector
{
    public string? GetForegroundExecutable() => exe;
    public (int Left, int Top, int Width, int Height)? GetForegroundClientRect() => (0, 0, 1280, 720);
}

sealed class StubCapture : IScreenCaptureService
{
    private ulong _h = 1;
    public (CapturedFrameData? Frame, bool SkippedByHash) CaptureIfChanged(
        int monitorIndex, (int Left, int Top, int Width, int Height)? regionRect,
        int targetWidth, int targetHeight, int jpegQuality, long frameId,
        ulong? lastAcceptedHash, int phashThreshold, bool isForced)
    {
        _h = (_h << 1) ^ 0x5A5A5A5AUL;
        return (new CapturedFrameData([0xFF, 0xD8, 0xFF, 0xD9], _h, frameId, 1.0, 1.0, DateTimeOffset.UtcNow, isForced), false);
    }
    public void Dispose() { }
}

sealed class RecordingVoice : IVoiceOutputService
{
    public List<string> Spoken { get; } = [];
    public string EngineName => "recording-test";
    public void WarmUp(string language, double volume, string gender = "male", string style = "neural") { }
    public void UpdateSettings(string language, double volume, string gender = "male", string style = "neural") { }
    public double SpeakInstant(string text) { Spoken.Add(text); return 1.0; }
    public void StopSpeaking() { }
    public void Dispose() { }
}

sealed class RecordingOverlay : IOverlayOutputService
{
    public List<string> Shown { get; } = [];
    public void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds) => Shown.Add(text);
}
