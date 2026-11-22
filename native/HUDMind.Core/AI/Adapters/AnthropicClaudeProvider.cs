using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

public sealed class AnthropicClaudeProvider : IAIProvider
{
    public ProviderAccount Account { get; }
    private readonly HttpClient _http;

    public AnthropicClaudeProvider(ProviderAccount account, HttpClient? httpClient = null)
    {
        Account = account;
        _http = httpClient ?? SharedHttpFactory.GetSharedClient(allowLoopback: false);
    }

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key)) return;
        var baseUrl = Account.ResolvedBaseUrl();
        var (okUrl, _) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopbackHttp: false);
        if (!okUrl) return;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            await resp.Content.CopyToAsync(Stream.Null, timeoutCts.Token);
        }
        catch { /* best effort */ }
    }

    public Task<ProviderCallResult> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        string spatialTelemetry = "",
        HudSensorSnapshot? sensorSnapshot = null,
        CancellationToken ct = default)
    {
        var prompt = CompactWireProtocol.BuildRealtimePrompt(profile, config, recentHistory, forceAdvice, visualTimeline: spatialTelemetry);
        return SendMessagesAsync(jpegBytes, prompt, temperature: 0.35, maxTokens: 115, stopOnNewline: true, parseAsCompactWire: true, ct);
    }

    public Task<ProviderCallResult> AskQuestionAsync(
        byte[]? jpegBytes,
        string userQuestion,
        GameProfile profile,
        UserConfig config,
        string recentHistory = "",
        string visualTimeline = "",
        HudSensorSnapshot? sensorSnapshot = null,
        string recentChatHistory = "",
        CancellationToken ct = default)
    {
        var prompt = CompactWireProtocol.BuildInteractiveChatPrompt(
            profile, config, userQuestion, recentHistory, visualTimeline, sensorSnapshot, recentChatHistory);
        return SendMessagesAsync(jpegBytes, prompt, temperature: 0.45, maxTokens: 180, stopOnNewline: false, parseAsCompactWire: false, ct);
    }

    public Task<ProviderCallResult> AskRawTextAsync(
        string prompt,
        UserConfig config,
        int maxTokens = 320,
        CancellationToken ct = default) =>
        SendMessagesAsync(null, prompt, temperature: 0.45, maxTokens: maxTokens, stopOnNewline: false, parseAsCompactWire: false, ct);

    private async Task<ProviderCallResult> SendMessagesAsync(
        byte[]? jpegBytes,
        string prompt,
        double temperature,
        int maxTokens,
        bool stopOnNewline,
        bool parseAsCompactWire,
        CancellationToken ct)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key))
            return new ProviderCallResult(null, 401, "Falta API key de Anthropic Claude.");
        var baseUrl = Account.ResolvedBaseUrl();
        var (okUrl, errUrl) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopbackHttp: false);
        if (!okUrl)
            return new ProviderCallResult(null, 400, errUrl);

        var partsList = new List<object>();
        if (jpegBytes is { Length: > 0 })
            partsList.Add(new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = Convert.ToBase64String(jpegBytes) } });
        partsList.Add(new { type = "text", text = prompt });

        var payloadObj = new Dictionary<string, object>
        {
            ["model"] = Account.Model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["messages"] = new[] { new { role = "user", content = partsList.ToArray() } }
        };
        if (stopOnNewline)
            payloadObj["stop_sequences"] = new[] { "\n" };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payloadObj), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return SharedHttpFactory.RedactedHttpFailure(resp, body, key);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("content", out var blocks))
            {
                var sb = new StringBuilder();
                foreach (var b in blocks.EnumerateArray())
                {
                    if (b.TryGetProperty("text", out var t))
                        sb.Append(t.GetString());
                }
                if (parseAsCompactWire)
                {
                    var parsed = CompactWireProtocol.ParseCompactLine(sb.ToString());
                    return parsed is not null
                        ? new ProviderCallResult(parsed)
                        : new ProviderCallResult(null, 422, "Salida Claude no válida");
                }
                var reply = sb.ToString().Trim();
                return !string.IsNullOrWhiteSpace(reply)
                    ? new ProviderCallResult(new CoachResponse(true, UrgencyLevel.Medium, reply, reply, "[CHAT]"))
                    : new ProviderCallResult(null, 422, "Respuesta vacía de Claude.");
            }
            return new ProviderCallResult(null, 422, "Respuesta vacía de Claude");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return new ProviderCallResult(null, 408, "timeout"); }
        catch (Exception ex) { return new ProviderCallResult(null, 503, SecretRedactor.Redact(ex.Message, [key])); }
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key)) return (false, "Falta API key de Claude.");
        var baseUrl = Account.ResolvedBaseUrl();
        var (okUrl, errUrl) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopbackHttp: false);
        if (!okUrl) return (false, errUrl);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode
                ? (true, $"OK — Anthropic Claude verificado ({Account.Model}).")
                : (false, $"HTTP {(int)resp.StatusCode} verificando Claude.");
        }
        catch (Exception ex)
        {
            return (false, $"Error de red: {SecretRedactor.Redact(ex.Message, [key])}");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
