using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

public sealed record ProviderCallResult(
    CoachResponse? Response,
    int? ErrorCode = null,
    string ErrorMessage = ""
);

public interface IAIProvider : IAsyncDisposable
{
    ProviderAccount Account { get; }
    Task PrewarmAsync(CancellationToken ct = default);
    Task<ProviderCallResult> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        CancellationToken ct = default);
    Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default);
}

public static class SharedHttpFactory
{
    public static HttpClient CreateFastKeepAliveClient(double timeoutSeconds = 6.0)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2.0, 20.0)),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HUDMind-Native/1.0");
        return client;
    }
}

public sealed class GeminiFastProvider : IAIProvider
{
    public ProviderAccount Account { get; }
    private readonly HttpClient _http;

    public GeminiFastProvider(ProviderAccount account, HttpClient? httpClient = null)
    {
        Account = account;
        _http = httpClient ?? SharedHttpFactory.CreateFastKeepAliveClient(6.0);
    }

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key))
            return;

        var safeModel = Uri.EscapeDataString(Account.Model.Trim());
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://generativelanguage.googleapis.com/v1beta/models/{safeModel}");
        req.Headers.Add("x-goog-api-key", key);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch
        {
            // Best-effort TLS + HTTP/2 socket prewarm
        }
    }

    public async Task<ProviderCallResult> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        CancellationToken ct = default)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key))
            return new ProviderCallResult(null, 401, "Falta API key de Gemini.");

        var safeModel = Uri.EscapeDataString(Account.Model.Trim());
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{safeModel}:generateContent";
        var prompt = CompactWireProtocol.BuildRealtimePrompt(profile, config, recentHistory, forceAdvice);
        var b64 = Convert.ToBase64String(jpegBytes);
        var modelLow = Account.Model.ToLowerInvariant();

        var genCfg = new Dictionary<string, object>
        {
            ["temperature"] = 0.55,
            ["maxOutputTokens"] = 24,
            ["mediaResolution"] = "MEDIA_RESOLUTION_LOW"
        };
        if (modelLow.Contains("2.5"))
            genCfg["thinkingConfig"] = new { thinkingBudget = 0 };
        else if (modelLow.Contains("3.") || modelLow.Contains("3-"))
            genCfg["thinkingConfig"] = new { thinkingLevel = "MINIMAL" };

        var payloadObj = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inlineData = new { mimeType = "image/jpeg", data = b64 } },
                        new { text = prompt }
                    }
                }
            },
            generationConfig = genCfg
        };

        var jsonPayload = JsonSerializer.Serialize(payloadObj);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-goog-api-key", key);

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var safeErr = SecretRedactor.Redact(body.Length > 200 ? body[..200] : body, [key]);
                return new ProviderCallResult(null, (int)resp.StatusCode, safeErr);
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0)
            {
                var parts = cands[0].GetProperty("content").GetProperty("parts");
                var sb = new StringBuilder();
                foreach (var p in parts.EnumerateArray())
                {
                    if (p.TryGetProperty("text", out var t))
                        sb.Append(t.GetString());
                }
                var parsed = CompactWireProtocol.ParseCompactLine(sb.ToString());
                return parsed is not null
                    ? new ProviderCallResult(parsed)
                    : new ProviderCallResult(null, 422, "Salida compacta no válida");
            }
            return new ProviderCallResult(null, 422, "Sin candidatos en respuesta Gemini");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new ProviderCallResult(null, 408, "timeout");
        }
        catch (Exception ex)
        {
            return new ProviderCallResult(null, 503, SecretRedactor.Redact(ex.Message, [key]));
        }
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key))
            return (false, "Falta API key de Gemini.");
        try
        {
            var safeModel = Uri.EscapeDataString(Account.Model.Trim());
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://generativelanguage.googleapis.com/v1beta/models/{safeModel}");
            req.Headers.Add("x-goog-api-key", key);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode
                ? (true, $"OK — Modelo '{Account.Model}' verificado (HTTP/{resp.Version}).")
                : (false, $"HTTP {(int)resp.StatusCode} verificando '{Account.Model}'.");
        }
        catch (Exception ex)
        {
            return (false, $"Error de red: {SecretRedactor.Redact(ex.Message, [key])}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class OpenAICompatibleProvider : IAIProvider
{
    public ProviderAccount Account { get; }
    private readonly HttpClient _http;

    public OpenAICompatibleProvider(ProviderAccount account, HttpClient? httpClient = null)
    {
        Account = account;
        _http = httpClient ?? SharedHttpFactory.CreateFastKeepAliveClient(8.0);
    }

    public Task PrewarmAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<ProviderCallResult> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        CancellationToken ct = default)
    {
        var baseUrl = Account.ResolvedBaseUrl();
        if (string.IsNullOrEmpty(baseUrl))
            return new ProviderCallResult(null, 400, "Base URL vacía.");

        var endpoint = baseUrl.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/chat/completions";

        var prompt = CompactWireProtocol.BuildRealtimePrompt(profile, config, recentHistory, forceAdvice);
        var isKeyless = Account.Provider is "pollinations" or "airforce" && string.IsNullOrWhiteSpace(Account.ApiKey);

        object payloadObj;
        if (isKeyless)
        {
            payloadObj = new
            {
                model = string.IsNullOrWhiteSpace(Account.Model) ? "openai-fast" : Account.Model,
                temperature = 0.7,
                max_tokens = 32,
                messages = new object[]
                {
                    new { role = "user", content = prompt }
                }
            };
        }
        else
        {
            var b64 = Convert.ToBase64String(jpegBytes);
            payloadObj = new
            {
                model = Account.Model,
                temperature = 0.5,
                max_tokens = 26,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new
                            {
                                type = "image_url",
                                image_url = new { url = $"data:image/jpeg;base64,{b64}", detail = "low" }
                            }
                        }
                    }
                }
            };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payloadObj), Encoding.UTF8, "application/json")
        };
        var key = Account.ApiKey.Trim();
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (Account.Provider == "openrouter")
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/hudmind");
            req.Headers.TryAddWithoutValidation("X-Title", "HUDMind");
        }

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var safeErr = SecretRedactor.Redact(body.Length > 200 ? body[..200] : body, [key]);
                return new ProviderCallResult(null, (int)resp.StatusCode, safeErr);
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var content = choices[0].GetProperty("message").GetProperty("content").GetString();
                var parsed = CompactWireProtocol.ParseCompactLine(content);
                return parsed is not null
                    ? new ProviderCallResult(parsed)
                    : new ProviderCallResult(null, 422, "Salida compacta no válida");
            }
            return new ProviderCallResult(null, 422, "Sin choices en respuesta OpenAI-compatible");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new ProviderCallResult(null, 408, "timeout");
        }
        catch (Exception ex)
        {
            return new ProviderCallResult(null, 503, SecretRedactor.Redact(ex.Message, [key]));
        }
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var baseUrl = Account.ResolvedBaseUrl();
        if (string.IsNullOrEmpty(baseUrl))
            return (false, "Base URL vacía.");

        var modelsUrl = Account.Provider == "pollinations" && string.IsNullOrWhiteSpace(Account.ApiKey)
            ? "https://text.pollinations.ai/models"
            : $"{baseUrl}/models";

        using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        var key = Account.ApiKey.Trim();
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode
                ? (true, $"OK — Endpoint '{Account.Provider}' accesible.")
                : (false, $"HTTP {(int)resp.StatusCode} en {modelsUrl}.");
        }
        catch (Exception ex)
        {
            return (false, $"Error de conexión: {SecretRedactor.Redact(ex.Message, [key])}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ProviderFailoverRouter : IAsyncDisposable
{
    private readonly List<IAIProvider> _providers;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldownUntil = new();
    private readonly double _hedgeDelaySeconds;
    private readonly double _freshnessDeadlineMs;
    private readonly Action<string>? _onLog;
    private int _activeIndex;

    public string ActiveProviderLabel =>
        _providers.Count == 0
            ? "(sin proveedores)"
            : $"{_providers[Math.Min(_activeIndex, _providers.Count - 1)].Account.Label} [{_providers[Math.Min(_activeIndex, _providers.Count - 1)].Account.Model}]";

    public ProviderFailoverRouter(
        IEnumerable<ProviderAccount> accounts,
        Func<ProviderAccount, IAIProvider>? factory = null,
        double hedgeDelaySeconds = 1.10,
        double freshnessDeadlineMs = 2400.0,
        Action<string>? onLog = null)
    {
        _hedgeDelaySeconds = Math.Max(0.05, hedgeDelaySeconds);
        _freshnessDeadlineMs = freshnessDeadlineMs;
        _onLog = onLog;
        var create = factory ?? CreateDefaultAdapter;

        _providers = accounts
            .Where(a => a.IsReady() && !string.Equals(a.Provider, "ollama", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Priority)
            .Select(create)
            .ToList();
    }

    public static IAIProvider CreateDefaultAdapter(ProviderAccount acc) =>
        acc.Provider.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            ? new GeminiFastProvider(acc)
            : new OpenAICompatibleProvider(acc);

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        if (_providers.Count == 0)
            return;

        var tasks = _providers.Take(2).Select(p => p.PrewarmAsync(ct));
        await Task.WhenAll(tasks);
        _onLog?.Invoke($"Proveedores precalentados (HTTP/2 Keep-Alive): {ActiveProviderLabel}");
    }

    public async Task<(CoachResponse? Response, double LatencyMs, string ProviderModel)> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        CancellationToken ct = default)
    {
        if (_providers.Count == 0)
            return (null, 0, "none");

        var now = DateTimeOffset.UtcNow;
        var candidates = _providers
            .Select((prov, idx) => (idx, prov))
            .Where(x => !_cooldownUntil.TryGetValue(x.prov.Account.Id, out var until) || now >= until)
            .ToList();

        if (candidates.Count == 0)
        {
            if (!forceAdvice)
                return (null, 0, "cooldown");
            candidates.Add((0, _providers[0]));
        }

        // Quota-smart adaptive hedged vision racing when top 2 are fast cloud vision accounts
        var skippedIds = new HashSet<string>();
        if (candidates.Count >= 2 &&
            candidates[0].prov.Account.IsFastCloudVision &&
            candidates[1].prov.Account.IsFastCloudVision)
        {
            var hedged = await TryHedgedPairAsync(
                candidates[0], candidates[1],
                jpegBytes, profile, config, recentHistory, forceAdvice, ct);

            if (hedged.Handled)
                return (hedged.Response, hedged.LatencyMs, hedged.Model);

            skippedIds.Add(candidates[0].prov.Account.Id);
            if (_cooldownUntil.ContainsKey(candidates[1].prov.Account.Id))
                skippedIds.Add(candidates[1].prov.Account.Id);
        }

        foreach (var (idx, prov) in candidates)
        {
            if (skippedIds.Contains(prov.Account.Id))
                continue;

            var sw = Stopwatch.StartNew();
            var res = await prov.AnalyzeFrameAsync(jpegBytes, profile, config, recentHistory, forceAdvice, ct);
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;

            if (res.Response is not null)
            {
                _activeIndex = idx;
                return (ApplyFreshnessDeadline(res.Response, ms, forceAdvice, prov.Account.Model), ms, prov.Account.Model);
            }

            if (res.ErrorCode == 422)
                return (null, ms, prov.Account.Model);

            ApplyCooldown(prov, res.ErrorCode);
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
        CancellationToken outerCt)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);

        async Task<(int idx, IAIProvider prov, ProviderCallResult res, double ms)> RunOne((int idx, IAIProvider prov) item)
        {
            var sw = Stopwatch.StartNew();
            var r = await item.prov.AnalyzeFrameAsync(jpegBytes, profile, config, recentHistory, forceAdvice, raceCts.Token);
            sw.Stop();
            return (item.idx, item.prov, r, sw.Elapsed.TotalMilliseconds);
        }

        var t1 = RunOne(first);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(_hedgeDelaySeconds), raceCts.Token);
        var firstWinner = await Task.WhenAny(t1, delayTask);

        if (firstWinner == t1)
        {
            await raceCts.CancelAsync();
            var (idx, prov, res, ms) = await t1;
            if (res.Response is not null)
            {
                _activeIndex = idx;
                return (true, ApplyFreshnessDeadline(res.Response, ms, forceAdvice, prov.Account.Model), ms, prov.Account.Model);
            }
            if (res.ErrorCode == 422)
                return (true, null, ms, prov.Account.Model);
            ApplyCooldown(prov, res.ErrorCode);
            return (false, null, ms, prov.Account.Model);
        }

        // Primary stalled beyond hedge threshold -> launch secondary in parallel and race!
        var t2 = RunOne(second);
        var pending = new List<Task<(int idx, IAIProvider prov, ProviderCallResult res, double ms)>> { t1, t2 };

        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            try
            {
                var (idx, prov, res, ms) = await done;
                if (res.Response is not null)
                {
                    await raceCts.CancelAsync();
                    _activeIndex = idx;
                    return (true, ApplyFreshnessDeadline(res.Response, ms, forceAdvice, prov.Account.Model), ms, prov.Account.Model);
                }
                if (res.ErrorCode != 422)
                    ApplyCooldown(prov, res.ErrorCode);
            }
            catch (OperationCanceledException)
            {
                // Ignored for cancelled loser
            }
        }

        return (false, null, 0, first.prov.Account.Model);
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

    private void ApplyCooldown(IAIProvider prov, int? code)
    {
        var secs = code switch
        {
            401 or 403 => 600.0,
            402 => 300.0,
            429 => 45.0,
            _ => 20.0
        };
        _cooldownUntil[prov.Account.Id] = DateTimeOffset.UtcNow.AddSeconds(secs);
        _onLog?.Invoke($"Failover silencioso: '{prov.Account.Label}' (HTTP {code?.ToString() ?? "?"}) en pausa {secs:F0}s.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
            await p.DisposeAsync();
    }
}
