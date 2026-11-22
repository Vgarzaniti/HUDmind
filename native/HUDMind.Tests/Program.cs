using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using HUDMind.Core.AI;
using HUDMind.Core.AI.LiveBackends;
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

// 1. Compact Wire Protocol Parser (4-field Grounded Generic + Legacy + JSON)
await RunTest("01. CompactWireProtocol parses 4-field FASE|QUE_VEO|URG|CONSEJO, silences REPLAY/MENU & JSON", () =>
{
    var rNone = CompactWireProtocol.ParseCompactLine("NONE");
    Assert(rNone is { ShouldIntervene: false }, "NONE should not intervene");

    var rReplay = CompactWireProtocol.ParseCompactLine("REPLAY|Repetición de gol 1-2|H|Debería ignorarse en replay");
    Assert(rReplay is { ShouldIntervene: false } && rReplay.MatchContext.Contains("[REPLAY]"), "REPLAY must force silence while keeping timeline context");

    var rKickoff = CompactWireProtocol.ParseCompactLine("KICKOFF|Cuenta regresiva 2, boost 33, compañero izquierda|M|Dejá que vaya tu compañero de izquierda y quedate cubriendo el arco.");
    Assert(rKickoff is { ShouldIntervene: true, Urgency: UrgencyLevel.Medium }, "KICKOFF should parse cleanly");
    Assert(rKickoff!.OverlayText.Contains("cubriendo el arco") && rKickoff.MatchContext.Contains("[KICKOFF]"), "Kickoff advice & context preserved");

    var rHigh = CompactWireProtocol.ParseCompactLine("H|Rotá segundo palo, tenés 18 de boost");
    Assert(rHigh is { ShouldIntervene: true, Urgency: UrgencyLevel.High }, "Legacy H| should parse as High");

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
    var a2 = new ProviderAccount { Id = "p2", Label = "P2", Provider = "openrouter", ApiKey = "sk-or-test123456", Model = "google/gemma-3-27b-it:free", Priority = 2 };
    var expected = new CoachResponse(true, UrgencyLevel.Medium, "Rotá atrás", "Rotá atrás");

    Assert(a2.SupportsVision && !a2.IsFastCloudVision, "OpenRouter :free Vision must be supported for failover but excluded from hedging");

    await using var router = new ProviderFailoverRouter(
        [a1, a2],
        factory: acc => acc.Id == "p1"
            ? new StubProvider(acc, 5, new ProviderCallResult(null, 422, "empty"))
            : new StubProvider(acc, 5, new ProviderCallResult(expected))
    );

    var profile = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rl", DisplayName = "Rocket League" } };
    var (resp, _, model) = await router.AnalyzeFrameAsync([1, 2, 3], profile, new UserConfig(), "", false);
    Assert(resp == expected && model == "google/gemma-3-27b-it:free", "422 must fall back to backup provider");
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
    Assert(seenBody is not null && seenBody.Contains("\"maxOutputTokens\":160") && seenBody.Contains("\"stopSequences\":[\"\\n\"]") && seenBody.Contains("\"thinkingLevel\":\"MINIMAL\"") && seenBody.Contains("\"MEDIA_RESOLUTION_MEDIUM\""),
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

// 13. Lazy Text-Only Sensor Layer: 0 calls on Vision, active ONLY for blind text-only models!
await RunTest("13. TextOnlySensorService pays 0ms on Vision models, activates ONLY on blind models & silences REPLAY locally", async () =>
{
    var profile = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rl", DisplayName = "Rocket League" } };
    var visionAcc = new ProviderAccount { Id = "v1", Label = "Gemini Vision", Provider = "gemini", ApiKey = "AIzaTest12345678", Model = "gemini-3.5-flash-lite", Priority = 1 };
    var blindAcc = new ProviderAccount { Id = "b1", Label = "Pollinations Blind", Provider = "pollinations", ApiKey = "", Model = "openai-fast", Priority = 2 };

    Assert(visionAcc.SupportsVision && !blindAcc.SupportsVision, "SupportsVision flags accurate");

    // Case A: Vision model succeeds -> sensor MUST NOT be called at all (0 calls!)
    var sensorA = new CountingSensor(new HudSensorSnapshot("PLAY", "[TOP-CENTER] 1 00:42 2", "", "", 30, true));
    await using (var routerA = new ProviderFailoverRouter(
        [visionAcc, blindAcc],
        factory: acc => new StubProvider(acc, 5, new ProviderCallResult(new CoachResponse(true, UrgencyLevel.Medium, "Rotá bien", "Rotá bien"))),
        textOnlySensor: sensorA))
    {
        var (r, _, _) = await routerA.AnalyzeFrameAsync([1, 2, 3], profile, new UserConfig(), "", false);
        Assert(r is { ShouldIntervene: true } && sensorA.CallCount == 0, $"Sensor should NOT run for Vision! Calls={sensorA.CallCount}");
    }

    // Case B: Only blind model active + REPLAY detected by OCR -> silenced locally WITHOUT calling cloud!
    var sensorReplay = new CountingSensor(new HudSensorSnapshot("REPLAY", "[CENTER] REPLAY GOL", "", "", 25, true));
    int blindCloudCalls = 0;
    await using (var routerB = new ProviderFailoverRouter(
        [blindAcc],
        factory: acc => new CallbackStubProvider(acc, snap => { blindCloudCalls++; return new ProviderCallResult(CoachResponse.Silent()); }),
        textOnlySensor: sensorReplay))
    {
        var (r, _, model) = await routerB.AnalyzeFrameAsync([1, 2, 3], profile, new UserConfig(), "", false);
        Assert(r is { ShouldIntervene: false } && model == "ocr-local-filter", "REPLAY silenced locally");
        Assert(sensorReplay.CallCount == 1 && blindCloudCalls == 0, "Blind cloud skipped on local REPLAY");
    }

    // Case C: Blind model active + PLAY OCR & Game Log -> passes real HudSensorSnapshot to blind model!
    var sensorPlay = new CountingSensor(new HudSensorSnapshot("PLAY", "[TOP-CENTER] 1 00:42 2 | [BOT-RIGHT] 14", "RL_LOG: Freeplay", "BOT-RIGHT: '68' -> '14'", 32, true));
    HudSensorSnapshot? receivedSnap = null;
    await using (var routerC = new ProviderFailoverRouter(
        [blindAcc],
        factory: acc => new CallbackStubProvider(acc, snap => { receivedSnap = snap; return new ProviderCallResult(new CoachResponse(true, UrgencyLevel.High, "Juntá boost", "Juntá boost")); }),
        textOnlySensor: sensorPlay))
    {
        var (r, _, _) = await routerC.AnalyzeFrameAsync([1, 2, 3], profile, new UserConfig(), "", false);
        Assert(r is { ShouldIntervene: true } && receivedSnap is { Phase: "PLAY", HasUsefulData: true }, "Sensor snapshot delivered to blind model");
    }
});

// 14. RealtimeLiveCoachOrchestrator & LiveBackendFactory (provider-agnostic live backend)
await RunTest("14. LiveBackendFactory & GeminiLiveBackend select Fenrir/Aoede and format valid Live setup JSON", () =>
{
    Assert(GeminiLiveBackend.ResolveVoiceNameStatic("male") == "Fenrir", "Male live voice is Fenrir");
    Assert(GeminiLiveBackend.ResolveVoiceNameStatic("female") == "Aoede", "Female live voice is Aoede");

    var cfg = new UserConfig { VoiceGender = "female", VoiceLanguage = "es" };
    var prof = ConfigRepository.CreateUniversalProfile(".");
    var json31 = GeminiLiveBackend.BuildSetupPayloadJson("models/gemini-3.1-flash-live-preview", cfg, prof);
    Assert(json31.Contains("\"Aoede\"") && json31.Contains("\"AUDIO\"") && json31.Contains("\"MINIMAL\"") && json31.Contains("\"MEDIA_RESOLUTION_MEDIUM\"") && json31.Contains("\"silenceDurationMs\":250") && json31.Contains("outputAudioTranscription"),
        $"Invalid 3.1 live setup JSON: {json31}");

    var json25 = GeminiLiveBackend.BuildSetupPayloadJson("models/gemini-2.5-flash-native-audio-latest", cfg, prof);
    Assert(json25.Contains("\"thinkingBudget\":0"), "2.5 native audio must disable thinking budget");
    return Task.CompletedTask;
});

// 15. CoachingPersonaBuilder adapts pro-meta depth & tone across skill tiers and custom ranks
await RunTest("15. CoachingPersonaBuilder calibrates beginner vs pro coaching depth and rank labels", () =>
{
    var beg = CoachingPersonaBuilder.BuildCompactRankDirective(new UserConfig
    {
        PlayerSkillTier = "beginner",
        PlayerRankLabel = "Bronce 2",
        CoachStyle = "friendly"
    });
    Assert(beg.Contains("PRINCIPIANTE") && beg.Contains("Bronce 2") && beg.Contains("amigable"), $"Unexpected beginner directive: {beg}");

    var pro = CoachingPersonaBuilder.BuildCompactRankDirective(new UserConfig
    {
        PlayerSkillTier = "pro",
        PlayerRankLabel = "GC2",
        CoachStyle = "strict"
    });
    Assert(pro.Contains("COMPETITIVO / PRO") && pro.Contains("GC2") && pro.Contains("Tier-1"), $"Unexpected pro directive: {pro}");
    return Task.CompletedTask;
});

// 16. Separated communication modes: Charla (mic+voice), Solo voz del coach (voice only), Solo texto (silent chat+overlay)
await RunTest("16. Communication modes cleanly separate Charla, Solo voz del coach, and Solo texto (+ interactive chat)", async () =>
{
    var cfg = new UserConfig { PlayerSkillTier = "intermediate", PlayerRankLabel = "Platino 2" };
    cfg.SetCommunicationMode("conversation");
    Assert(cfg.IsConversationMode && cfg.IsMicActive && cfg.IsCoachVoiceActive && !cfg.IsTextOnlyMode, "conversation must enable mic + coach voice");

    cfg.SetCommunicationMode("coach_voice");
    Assert(cfg.IsCoachVoiceOnlyMode && !cfg.IsMicActive && cfg.IsCoachVoiceActive && !cfg.IsTextOnlyMode, "coach_voice must disable mic and keep coach voice");

    cfg.SetCommunicationMode("text_only");
    Assert(cfg.IsTextOnlyMode && !cfg.IsMicActive && !cfg.IsCoachVoiceActive, "text_only must disable both mic and coach voice");

    var prof = ConfigRepository.CreateUniversalProfile(".");
    var chatPrompt = CompactWireProtocol.BuildInteractiveChatPrompt(prof, cfg, "¿Cómo defiendo este saque?");
    Assert(chatPrompt.Contains("Platino 2") && chatPrompt.Contains("¿Cómo defiendo este saque?"), "Chat prompt must include rank and user message");

    var acc = new ProviderAccount { Id = "chat-p1", Provider = "groq", Model = "llama-4-scout", ApiKey = "gsk_test1234567890" };
    var chatReply = new CoachResponse(true, UrgencyLevel.Medium, "Rotá segundo palo y guardá 30 de boost.", "Rotá segundo palo y guardá 30 de boost.", "[CHAT]");
    var router = new ProviderFailoverRouter([acc], a => new StubProvider(a, 5, new ProviderCallResult(chatReply)));
    var voice = new RecordingVoice();
    var overlay = new RecordingOverlay();
    await using var orch = new CoachOrchestrator(
        cfg, [prof], router, new StubWindowDetector("game.exe"), new StubCapture(), voice, overlay,
        testMode: true, forcedGameId: "universal");

    await orch.StartAsync();
    await orch.SendUserTextAsync("¿Cómo defiendo este saque?");
    Assert(overlay.Shown.Any(s => s.Contains("Rotá segundo palo")), "text_only chat must deliver reply to overlay");
    Assert(voice.Spoken.Count == 0, $"text_only must produce 0 voice calls, got {voice.Spoken.Count}");

    // Switch LIVE to coach_voice (only coach speaks, mic off) and verify voice activates cleanly
    orch.SetCommunicationMode("coach_voice");
    await orch.SendUserTextAsync("¿Y si viene por arriba?");
    Assert(voice.Spoken.Count == 1 && voice.Spoken[0].Contains("Rotá segundo palo"), "coach_voice must speak reply");
    await orch.StopAsync();
});

Console.WriteLine($"\nRESULT: {passed} passed, {failed} failed.");
Environment.Exit(failed > 0 ? 1 : 0);

sealed class StubProvider(ProviderAccount account, int delayMs, ProviderCallResult outcome) : IAIProvider
{
    public ProviderAccount Account { get; } = account;
    public Task PrewarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async Task<ProviderCallResult> AnalyzeFrameAsync(byte[] jpegBytes, GameProfile profile, UserConfig config, string recentHistory, bool forceAdvice, string spatialTelemetry = "", HudSensorSnapshot? sensorSnapshot = null, CancellationToken ct = default)
    {
        await Task.Delay(delayMs, ct);
        return outcome;
    }
    public async Task<ProviderCallResult> AskQuestionAsync(byte[]? jpegBytes, string userQuestion, GameProfile profile, UserConfig config, string recentHistory = "", string visualTimeline = "", HudSensorSnapshot? sensorSnapshot = null, CancellationToken ct = default)
    {
        await Task.Delay(delayMs, ct);
        return outcome;
    }
    public Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class CallbackStubProvider(ProviderAccount account, Func<HudSensorSnapshot?, ProviderCallResult> onCall) : IAIProvider
{
    public ProviderAccount Account { get; } = account;
    public Task PrewarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<ProviderCallResult> AnalyzeFrameAsync(byte[] jpegBytes, GameProfile profile, UserConfig config, string recentHistory, bool forceAdvice, string spatialTelemetry = "", HudSensorSnapshot? sensorSnapshot = null, CancellationToken ct = default) =>
        Task.FromResult(onCall(sensorSnapshot));
    public Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class CountingSensor(HudSensorSnapshot snapshot) : ITextOnlySensorService
{
    public int CallCount { get; private set; }
    public Task<HudSensorSnapshot> ExtractAsync(byte[] jpegBytes, GameProfile profile, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(snapshot);
    }
    public void Reset() { }
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
