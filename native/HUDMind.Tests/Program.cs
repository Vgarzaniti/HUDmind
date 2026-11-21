using System.Diagnostics;
using HUDMind.Core.AI;
using HUDMind.Core.Config;
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
await RunTest("CompactWireProtocol parses H/M/L, NONE, and JSON fallback", () =>
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
await RunTest("PerceptualHasher computes deterministic 64-bit dHash & Hamming distance", () =>
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
await RunTest("InterventionMemory blocks semantic duplicates and expires by TTL", () =>
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

// 4. Secret Redactor
await RunTest("SecretRedactor masks Gemini AQ/AIza and Groq gsk keys", () =>
{
    var raw = "Error with AQ.Ab8RN6L_testsecret12345 and gsk_abcd1234567890efgh";
    var clean = SecretRedactor.Redact(raw);
    Assert(!clean.Contains("Ab8RN6L") && !clean.Contains("gsk_"), $"Secrets leaked: {clean}");
    return Task.CompletedTask;
});

// 5. Hedged Vision Racing beats slow primary spike
await RunTest("ProviderFailoverRouter Hedged Vision beats stalled primary", async () =>
{
    var a1 = new ProviderAccount { Id = "p1", Label = "SlowPrimary", Provider = "gemini", ApiKey = "AIzaTest12345678", Model = "gemini-2.5-flash", Priority = 1 };
    var a2 = new ProviderAccount { Id = "p2", Label = "FastHedge", Provider = "groq", ApiKey = "gsk_test12345678", Model = "llama-4-scout", Priority = 2 };
    var expected = new CoachResponse(true, UrgencyLevel.High, "Cuidá segundo palo", "Cuidá segundo palo");

    await using var router = new ProviderFailoverRouter(
        [a1, a2],
        factory: acc => new StubProvider(acc, acc.Id == "p1" ? 1200 : 25, expected),
        hedgeDelaySeconds: 0.12
    );

    var profile = new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "rl", DisplayName = "Rocket League" } };
    var sw = Stopwatch.StartNew();
    var (resp, _, model) = await router.AnalyzeFrameAsync([1, 2, 3], profile, new UserConfig(), "", false);
    sw.Stop();

    Assert(resp == expected, "Expected hedge response");
    Assert(model == "llama-4-scout", $"Expected hedge winner, got {model}");
    Assert(sw.ElapsedMilliseconds < 500, $"Hedge should beat 1200ms primary (took {sw.ElapsedMilliseconds}ms)");
});

// 6. Real workspace compatibility test (reads existing profiles/ & config/)
await RunTest("ConfigRepository reads existing workspace profiles and config cleanly", () =>
{
    var repo = new ConfigRepository();
    var profiles = repo.LoadProfiles();
    Assert(profiles.Count >= 4, $"Expected >=4 game profiles, found {profiles.Count}");
    Assert(profiles.Any(p => p.Manifest.GameId == "rocket_league"), "rocket_league profile loaded");
    var cfg = repo.LoadUserConfig();
    Assert(!string.IsNullOrWhiteSpace(cfg.CoachMode), "UserConfig loaded");
    return Task.CompletedTask;
});

Console.WriteLine($"\nRESULT: {passed} passed, {failed} failed.");
Environment.Exit(failed > 0 ? 1 : 0);

sealed class StubProvider(ProviderAccount account, int delayMs, CoachResponse response) : IAIProvider
{
    public ProviderAccount Account { get; } = account;
    public Task PrewarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    public async Task<ProviderCallResult> AnalyzeFrameAsync(byte[] jpegBytes, GameProfile profile, UserConfig config, string recentHistory, bool forceAdvice, CancellationToken ct = default)
    {
        await Task.Delay(delayMs, ct);
        return new ProviderCallResult(response);
    }
    public Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
