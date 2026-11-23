using System.Collections.Concurrent;
using System.Diagnostics;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

/// <summary>
/// Fault-tolerant multi-provider router with circuit breakers, hedged cloud vision racing,
/// lazy OCR+Log blind sensor gating, interactive chat, and post-match review orchestration.
/// </summary>
public sealed class ProviderFailoverRouter : IAsyncDisposable
{
    private sealed class CircuitState
    {
        public int ConsecutiveTransientFailures;
        public DateTimeOffset OpenUntilUtc = DateTimeOffset.MinValue;
    }

    private readonly List<IAIProvider> _providers;
    private readonly ConcurrentDictionary<string, CircuitState> _circuits = new();
    private readonly ITextOnlySensorService? _textOnlySensor;
    private readonly double _hedgeDelaySeconds;
    private readonly double _freshnessDeadlineMs;
    private readonly Action<string>? _onLog;
    private int _activeIndex;

    private static readonly HashSet<string> LocalSilentPhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "MENU", "REPLAY", "LOADING", "DESKTOP", "PAUSE"
    };

    public string ActiveProviderLabel
    {
        get
        {
            if (_providers.Count == 0) return "(sin proveedores)";
            int idx = Math.Clamp(Volatile.Read(ref _activeIndex), 0, _providers.Count - 1);
            return $"{_providers[idx].Account.Label} [{_providers[idx].Account.Model}]";
        }
    }

    public ProviderFailoverRouter(
        IEnumerable<ProviderAccount> accounts,
        Func<ProviderAccount, IAIProvider>? factory = null,
        double hedgeDelaySeconds = 1.45,
        double freshnessDeadlineMs = 3400.0,
        Action<string>? onLog = null,
        ITextOnlySensorService? textOnlySensor = null)
    {
        _hedgeDelaySeconds = Math.Max(0.05, hedgeDelaySeconds);
        _freshnessDeadlineMs = freshnessDeadlineMs;
        _onLog = onLog;
        _textOnlySensor = textOnlySensor;
        var create = factory ?? CreateDefaultAdapter;

        // Include all ready providers so pure-text Chat / Post-Match Review always route cleanly;
        // AnalyzeFrameAsync individually skips text-only ("blind") providers unless _textOnlySensor is attached.
        _providers = accounts
            .Where(a => a.IsReady())
            .OrderBy(a => a.Priority)
            .Select(create)
            .ToList();
    }

    public static IAIProvider CreateDefaultAdapter(ProviderAccount acc) =>
        ProviderRegistry.CreateHttpAdapter(acc);

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        if (_providers.Count == 0)
            return;

        var tasks = _providers.Take(2).Select(p => p.PrewarmAsync(ct));
        await Task.WhenAll(tasks);
        _onLog?.Invoke($"Proveedores precalentados (HTTP/2 Keep-Alive): {ActiveProviderLabel}");
    }

    private bool IsCircuitOpen(string accountId, DateTimeOffset nowUtc)
    {
        if (!_circuits.TryGetValue(accountId, out var st))
            return false;
        lock (st)
        {
            if (nowUtc >= st.OpenUntilUtc)
            {
                st.OpenUntilUtc = DateTimeOffset.MinValue;
                return false;
            }
            return true;
        }
    }

    private void RecordSuccess(string accountId)
    {
        if (_circuits.TryGetValue(accountId, out var st))
        {
            lock (st)
            {
                st.ConsecutiveTransientFailures = 0;
                st.OpenUntilUtc = DateTimeOffset.MinValue;
            }
        }
    }

    public async Task<(CoachResponse? Response, double LatencyMs, string ProviderModel)> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        string spatialTelemetry = "",
        CancellationToken ct = default)
    {
        if (_providers.Count == 0)
            return (null, 0, "none");

        var now = DateTimeOffset.UtcNow;
        var candidates = _providers
            .Select((prov, idx) => (idx, prov))
            .Where(x => !IsCircuitOpen(x.prov.Account.Id, now))
            .ToList();

        if (candidates.Count == 0)
        {
            if (!forceAdvice)
                return (null, 0, "cooldown");
            candidates.Add((0, _providers[0]));
        }

        // Per-frame adaptive deadline: never block 6-8s for auto frames discarded at 2.4s!
        using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!forceAdvice)
            frameCts.CancelAfter(TimeSpan.FromMilliseconds(_freshnessDeadlineMs + 350.0));

        var attemptedIds = new HashSet<string>();
        if (candidates.Count >= 2 &&
            candidates[0].prov.Account.IsFastCloudVision &&
            candidates[1].prov.Account.IsFastCloudVision)
        {
            var hedged = await TryHedgedPairAsync(
                candidates[0], candidates[1],
                jpegBytes, profile, config, recentHistory, forceAdvice, spatialTelemetry,
                attemptedIds, frameCts.Token);

            if (hedged.Handled)
                return (hedged.Response, hedged.LatencyMs, hedged.Model);
        }

        HudSensorSnapshot? lazySensor = null;
        bool sensorExtracted = false;

        foreach (var (idx, prov) in candidates)
        {
            if (attemptedIds.Contains(prov.Account.Id))
                continue;
            if (ct.IsCancellationRequested)
                break;

            // Lazy OCR + Game Log sensors: executed EXCLUSIVELY for blind/text-only models (0ms on Vision!)
            if (!prov.Account.SupportsVision)
            {
                if (_textOnlySensor is null)
                    continue;

                if (!sensorExtracted)
                {
                    sensorExtracted = true;
                    lazySensor = await _textOnlySensor.ExtractAsync(jpegBytes, profile, frameCts.Token);
                }

                if (lazySensor is not null)
                {
                    bool anyVisionAhead = candidates.Any(c => !attemptedIds.Contains(c.prov.Account.Id) && c.prov.Account.SupportsVision);
                    if (!anyVisionAhead && !forceAdvice && LocalSilentPhases.Contains(lazySensor.Phase))
                        return (CoachResponse.Silent($"[{lazySensor.Phase}] {lazySensor.OcrZonesSummary}"), lazySensor.OcrMs, "ocr-local-filter");

                    if (!lazySensor.HasUsefulData)
                    {
                        if (anyVisionAhead)
                            continue; // Skip unhelpful blind candidate -> let backup Vision provider inspect pixels!
                        if (!forceAdvice)
                            return (CoachResponse.Silent("[SIN-HUD] Sin texto/logs legibles"), lazySensor.OcrMs, "ocr-local-filter");
                    }
                }
            }

            var sw = Stopwatch.StartNew();
            ProviderCallResult res;
            try
            {
                res = await prov.AnalyzeFrameAsync(
                    jpegBytes, profile, config, recentHistory, forceAdvice, spatialTelemetry,
                    sensorSnapshot: prov.Account.SupportsVision ? null : lazySensor,
                    ct: frameCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                res = new ProviderCallResult(null, 408, "deadline");
            }
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;

            if (res.Response is not null)
            {
                RecordSuccess(prov.Account.Id);
                Volatile.Write(ref _activeIndex, idx);
                return (ApplyFreshnessDeadline(res.Response, ms, forceAdvice, prov.Account.Model), ms, prov.Account.Model);
            }

            // On HTTP 422 (malformed output), continue failover to backup without penalizing cooldown!
            if (res.ErrorCode == 422)
                continue;

            ApplyFailureCircuit(prov, res.ErrorCode, res.RetryAfterSeconds);
        }

        return (null, 0, "all-failed");
    }

    public async Task<(string? Reply, double LatencyMs, string ProviderModel)> AskQuestionAsync(
        byte[]? jpegBytes,
        string userQuestion,
        GameProfile profile,
        UserConfig config,
        string recentHistory = "",
        string visualTimeline = "",
        string recentChatHistory = "",
        CancellationToken ct = default)
    {
        if (_providers.Count == 0)
            return (null, 0, "none");

        var now = DateTimeOffset.UtcNow;
        var candidates = _providers
            .Select((prov, idx) => (idx, prov))
            .Where(x => !IsCircuitOpen(x.prov.Account.Id, now))
            .ToList();

        if (candidates.Count == 0)
            candidates.Add((0, _providers[0]));

        HudSensorSnapshot? lazySensor = null;
        bool sensorExtracted = false;

        foreach (var (idx, prov) in candidates)
        {
            if (ct.IsCancellationRequested) break;

            if (!prov.Account.SupportsVision && jpegBytes is { Length: > 0 } && _textOnlySensor is not null && !sensorExtracted)
            {
                sensorExtracted = true;
                lazySensor = await _textOnlySensor.ExtractAsync(jpegBytes, profile, ct);
            }

            var sw = Stopwatch.StartNew();
            ProviderCallResult res;
            try
            {
                res = await prov.AskQuestionAsync(
                    jpegBytes, userQuestion, profile, config, recentHistory, visualTimeline,
                    sensorSnapshot: prov.Account.SupportsVision ? null : lazySensor,
                    recentChatHistory: recentChatHistory,
                    ct: ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                res = new ProviderCallResult(null, 408, "timeout");
            }
            sw.Stop();

            if (res.Response is not null && !string.IsNullOrWhiteSpace(res.Response.OverlayText))
            {
                RecordSuccess(prov.Account.Id);
                Volatile.Write(ref _activeIndex, idx);
                return (res.Response.OverlayText.Trim(), sw.Elapsed.TotalMilliseconds, prov.Account.Model);
            }

            if (res.ErrorCode is not (422 or 501))
                ApplyFailureCircuit(prov, res.ErrorCode, res.RetryAfterSeconds);
        }

        return (null, 0, "all-failed");
    }

    public async Task<(string? Review, double LatencyMs, string ProviderModel)> GenerateMatchReviewAsync(
        string gameDisplayName,
        string sessionDigest,
        UserConfig config,
        CancellationToken ct = default,
        GameProfile? profile = null)
    {
        if (_providers.Count == 0 || string.IsNullOrWhiteSpace(sessionDigest))
            return (null, 0, "none");

        var prompt = CoachingPersonaBuilder.BuildPostMatchReviewPrompt(gameDisplayName, sessionDigest, config, profile);
        var now = DateTimeOffset.UtcNow;
        var candidates = _providers
            .Select((prov, idx) => (idx, prov))
            .Where(x => !IsCircuitOpen(x.prov.Account.Id, now))
            .ToList();
        if (candidates.Count == 0)
            candidates.Add((0, _providers[0]));

        foreach (var (idx, prov) in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var sw = Stopwatch.StartNew();
            ProviderCallResult res;
            try
            {
                res = await prov.AskRawTextAsync(prompt, config, maxTokens: 320, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                res = new ProviderCallResult(null, 408, "timeout");
            }
            sw.Stop();

            if (res.Response is not null && !string.IsNullOrWhiteSpace(res.Response.OverlayText))
            {
                RecordSuccess(prov.Account.Id);
                Volatile.Write(ref _activeIndex, idx);
                return (res.Response.OverlayText.Trim(), sw.Elapsed.TotalMilliseconds, prov.Account.Model);
            }

            if (res.ErrorCode is not (422 or 501))
                ApplyFailureCircuit(prov, res.ErrorCode, res.RetryAfterSeconds);
        }

        return (null, 0, "all-failed");
    }

    private async Task<(bool Handled, CoachResponse? Response, double LatencyMs, string Model)> TryHedgedPairAsync(
        (int idx, IAIProvider prov) first,
        (int idx, IAIProvider prov) second,
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        string spatialTelemetry,
        HashSet<string> attemptedIds,
        CancellationToken outerCt)
    {
        var raceCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        attemptedIds.Add(first.prov.Account.Id);

        async Task<(int idx, IAIProvider prov, ProviderCallResult res, double ms)> RunOne((int idx, IAIProvider prov) item)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var r = await item.prov.AnalyzeFrameAsync(
                    jpegBytes, profile, config, recentHistory, forceAdvice, spatialTelemetry, null, raceCts.Token);
                sw.Stop();
                return (item.idx, item.prov, r, sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
            {
                sw.Stop();
                return (item.idx, item.prov, new ProviderCallResult(null, 408, "cancelled"), sw.Elapsed.TotalMilliseconds);
            }
        }

        var t1 = RunOne(first);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(_hedgeDelaySeconds), raceCts.Token);
        var firstWinner = await Task.WhenAny(t1, delayTask);

        if (outerCt.IsCancellationRequested)
        {
            ObserveAndDispose(t1, raceCts);
            return (true, null, 0, first.prov.Account.Model);
        }

        if (firstWinner == t1)
        {
            await raceCts.CancelAsync();
            var (idx, prov, res, ms) = await t1;
            raceCts.Dispose();

            if (res.Response is not null)
            {
                RecordSuccess(prov.Account.Id);
                Volatile.Write(ref _activeIndex, idx);
                return (true, ApplyFreshnessDeadline(res.Response, ms, forceAdvice, prov.Account.Model), ms, prov.Account.Model);
            }
            if (res.ErrorCode != 422)
                ApplyFailureCircuit(prov, res.ErrorCode, res.RetryAfterSeconds);
            return (false, null, ms, prov.Account.Model);
        }

        // Primary stalled beyond hedge threshold -> launch secondary in parallel!
        attemptedIds.Add(second.prov.Account.Id);
        var t2 = RunOne(second);
        var pending = new List<Task<(int idx, IAIProvider prov, ProviderCallResult res, double ms)>> { t1, t2 };
        bool disposedRace = false;

        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);

                (int idx, IAIProvider prov, ProviderCallResult res, double ms) outcome;
                try
                {
                    outcome = await done;
                }
                catch (OperationCanceledException)
                {
                    continue;
                }

                if (outcome.res.Response is not null)
                {
                    await raceCts.CancelAsync();
                    if (pending.Count > 0)
                    {
                        ObserveAndDispose(pending[0], raceCts);
                        pending.Clear();
                        disposedRace = true;
                    }
                    else
                    {
                        raceCts.Dispose();
                        disposedRace = true;
                    }

                    RecordSuccess(outcome.prov.Account.Id);
                    Volatile.Write(ref _activeIndex, outcome.idx);
                    return (true, ApplyFreshnessDeadline(outcome.res.Response, outcome.ms, forceAdvice, outcome.prov.Account.Model), outcome.ms, outcome.prov.Account.Model);
                }

                if (outcome.res.ErrorCode != 422)
                    ApplyFailureCircuit(outcome.prov, outcome.res.ErrorCode, outcome.res.RetryAfterSeconds);
            }
        }
        finally
        {
            if (!disposedRace)
            {
                try { raceCts.Cancel(); } catch { }
                if (pending.Count > 0)
                {
                    foreach (var p in pending)
                        _ = p.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                raceCts.Dispose();
            }
        }

        return (false, null, 0, first.prov.Account.Model);
    }

    private static void ObserveAndDispose(Task loser, CancellationTokenSource raceCts)
    {
        _ = loser.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; // Mark observed to avoid UnobservedTaskException
                ((CancellationTokenSource)state!).Dispose();
            },
            raceCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private CoachResponse ApplyFreshnessDeadline(CoachResponse resp, double latencyMs, bool forceAdvice, string model)
    {
        if (!forceAdvice && resp.ShouldIntervene && latencyMs > _freshnessDeadlineMs)
        {
            _onLog?.Invoke($"Consejo descartado por latencia alta ({latencyMs:F0}ms > {_freshnessDeadlineMs:F0}ms) [{model}]");
            return CoachResponse.Silent(resp.MatchContext);
        }
        return resp;
    }

    private void ApplyFailureCircuit(IAIProvider prov, int? code, double? retryAfterSeconds)
    {
        var st = _circuits.GetOrAdd(prov.Account.Id, _ => new CircuitState());
        double secs;

        lock (st)
        {
            if (code is 401 or 403)
            {
                secs = 600.0;
            }
            else if (code == 402)
            {
                secs = 300.0;
            }
            else if (code == 429)
            {
                secs = retryAfterSeconds ?? 45.0;
            }
            else
            {
                // Transient 5xx/timeout: open circuit only after 2 consecutive failures (ISO 25010 Fault Tolerance)
                st.ConsecutiveTransientFailures++;
                if (st.ConsecutiveTransientFailures < 2)
                    return;
                secs = Math.Min(120.0, 15.0 * Math.Pow(2, st.ConsecutiveTransientFailures - 2));
            }

            st.OpenUntilUtc = DateTimeOffset.UtcNow.AddSeconds(secs);
        }

        var safeLabel = SecretRedactor.Redact(prov.Account.Label, [prov.Account.ApiKey]);
        _onLog?.Invoke($"Failover silencioso: '{safeLabel}' (HTTP {code?.ToString() ?? "?"}) en pausa {secs:F0}s.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
            await p.DisposeAsync();
    }
}
