using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

public sealed class OpenAICompatibleProvider : IAIProvider
{
    public ProviderAccount Account { get; }
    public bool SupportsNativeMultiFrame =>
        Account.SupportsVision &&
        (Account.Provider ?? string.Empty).Trim().ToLowerInvariant() is "openai" or "xai";
    private readonly HttpClient _http;

    public OpenAICompatibleProvider(ProviderAccount account, HttpClient? httpClient = null)
    {
        Account = account;
        _http = httpClient ?? SharedHttpFactory.GetSharedClient(
            allowLoopback: account.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveModelsEndpoint(string baseUrl) =>
        Account.Provider.Equals("pollinations", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Account.ApiKey)
            ? "https://text.pollinations.ai/models"
            : $"{baseUrl}/models";

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        var baseUrl = Account.ResolvedBaseUrl();
        bool allowLoopback = Account.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        var (okUrl, _) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopback);
        if (!okUrl) return;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));
        using var req = new HttpRequestMessage(HttpMethod.Get, ResolveModelsEndpoint(baseUrl));
        var key = Account.ApiKey.Trim();
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            await resp.Content.CopyToAsync(Stream.Null, timeoutCts.Token);
        }
        catch { /* Best-effort warmup */ }
    }

    private static string ResolveChatCompletionsEndpoint(string baseUrl) =>
        baseUrl.EndsWith("/openai", StringComparison.OrdinalIgnoreCase) ||
        baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/chat/completions";

    private static bool IsReasoningModel(string? model)
    {
        var m = (model ?? string.Empty).Trim().ToLowerInvariant();
        return m.StartsWith("o1", StringComparison.Ordinal) ||
               m.StartsWith("o3", StringComparison.Ordinal) ||
               m.StartsWith("o4", StringComparison.Ordinal) ||
               m.StartsWith("gpt-5", StringComparison.Ordinal) ||
               m.Contains("/o1", StringComparison.Ordinal) ||
               m.Contains("/o3", StringComparison.Ordinal) ||
               m.Contains("/o4", StringComparison.Ordinal);
    }

    private static Dictionary<string, object> BuildChatCompletionPayload(string model, object[] messages, bool isChat, int maxTokensOverride = 0)
    {
        var dict = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages
        };
        if (IsReasoningModel(model))
        {
            dict["max_completion_tokens"] = maxTokensOverride > 0 ? Math.Max(maxTokensOverride, 480) : (isChat ? 520 : 420);
        }
        else
        {
            dict["temperature"] = isChat ? 0.45 : 0.35;
            dict["max_tokens"] = maxTokensOverride > 0 ? maxTokensOverride : (isChat ? 180 : 120);
            if (!isChat)
                dict["stop"] = new[] { "\n" };
        }
        return dict;
    }

    public Task<ProviderCallResult> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        string spatialTelemetry = "",
        HudSensorSnapshot? sensorSnapshot = null,
        CancellationToken ct = default) =>
        AnalyzeFrameSequenceAsync(
            new TemporalVisualPayload(
                jpegBytes is { Length: > 0 } ? [new TemporalFrameSample(jpegBytes, DateTimeOffset.UtcNow, 0)] : [],
                jpegBytes, jpegBytes is { Length: > 0 } ? 1 : 0, string.Empty),
            profile, config, recentHistory, forceAdvice, spatialTelemetry, sensorSnapshot, ct);

    public async Task<ProviderCallResult> AnalyzeFrameSequenceAsync(
        TemporalVisualPayload visual,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        string spatialTelemetry = "",
        HudSensorSnapshot? sensorSnapshot = null,
        CancellationToken ct = default)
    {
        if (!Account.SupportsVision)
        {
            if (sensorSnapshot is null || !sensorSnapshot.HasUsefulData)
            {
                return forceAdvice
                    ? new ProviderCallResult(null, 422, "Sin datos OCR/log legibles en este modelo ciego")
                    : new ProviderCallResult(CoachResponse.Silent(), null, string.Empty);
            }

            var sensorPrompt = CompactWireProtocol.BuildTextOnlySensorPrompt(
                profile, config, sensorSnapshot, recentHistory,
                visualTimeline: spatialTelemetry, forceAdvice: forceAdvice);

            return await SendOpenAiRequestAsync([], sensorPrompt, isChat: false, maxTokensOverride: 0, ct);
        }

        var combinedTimeline = string.IsNullOrWhiteSpace(visual.SequenceNote)
            ? spatialTelemetry
            : (string.IsNullOrWhiteSpace(spatialTelemetry) ? visual.SequenceNote : $"{visual.SequenceNote} | {spatialTelemetry}");
        var prompt = CompactWireProtocol.BuildRealtimePrompt(
            profile, config, recentHistory, forceAdvice,
            visualTimeline: combinedTimeline);
        var frames = SelectFrames(visual, maxFrames: forceAdvice ? 5 : 3);
        return await SendOpenAiRequestAsync(frames, prompt, isChat: false, maxTokensOverride: 0, ct);
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
        CancellationToken ct = default) =>
        AskQuestionSequenceAsync(
            jpegBytes is { Length: > 0 }
                ? new TemporalVisualPayload([new TemporalFrameSample(jpegBytes, DateTimeOffset.UtcNow, 0)], jpegBytes, 1, string.Empty)
                : null,
            userQuestion, profile, config, recentHistory, visualTimeline, sensorSnapshot, recentChatHistory, ct);

    public Task<ProviderCallResult> AskQuestionSequenceAsync(
        TemporalVisualPayload? visual,
        string userQuestion,
        GameProfile profile,
        UserConfig config,
        string recentHistory = "",
        string visualTimeline = "",
        HudSensorSnapshot? sensorSnapshot = null,
        string recentChatHistory = "",
        CancellationToken ct = default)
    {
        var combinedTimeline = visual is null || string.IsNullOrWhiteSpace(visual.SequenceNote)
            ? visualTimeline
            : (string.IsNullOrWhiteSpace(visualTimeline) ? visual.SequenceNote : $"{visual.SequenceNote} ; {visualTimeline}");
        var prompt = CompactWireProtocol.BuildInteractiveChatPrompt(
            profile, config, userQuestion, recentHistory, combinedTimeline,
            Account.SupportsVision ? null : sensorSnapshot,
            recentChatHistory);
        var frames = visual is null || !Account.SupportsVision ? Array.Empty<byte[]>() : SelectFrames(visual, maxFrames: 5);
        return SendOpenAiRequestAsync(frames, prompt, isChat: true, maxTokensOverride: 180, ct);
    }

    public Task<ProviderCallResult> AskRawTextAsync(
        string prompt,
        UserConfig config,
        int maxTokens = 320,
        CancellationToken ct = default) =>
        SendOpenAiRequestAsync([], prompt, isChat: true, maxTokensOverride: maxTokens, ct);

    private IReadOnlyList<byte[]> SelectFrames(TemporalVisualPayload visual, int maxFrames)
    {
        if (SupportsNativeMultiFrame && visual.Frames.Count > 0)
            return visual.Frames.TakeLast(maxFrames).Select(f => f.JpegBytes).Where(b => b.Length > 0).ToArray();
        return visual.FallbackSingleJpeg is { Length: > 0 } single ? [single] : [];
    }

    private async Task<ProviderCallResult> SendOpenAiRequestAsync(
        IReadOnlyList<byte[]> frames,
        string prompt,
        bool isChat,
        int maxTokensOverride,
        CancellationToken ct)
    {
        var baseUrl = Account.ResolvedBaseUrl();
        bool allowLoopback = Account.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        var (okUrl, errUrl) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopback);
        if (!okUrl)
            return new ProviderCallResult(null, 400, errUrl);

        var endpoint = ResolveChatCompletionsEndpoint(baseUrl);
        string targetModel = string.IsNullOrWhiteSpace(Account.Model) ? "openai-fast" : Account.Model;

        object messageContent;
        if (Account.SupportsVision && frames.Count > 0)
        {
            var parts = new List<object>(frames.Count + 1)
            {
                new { type = "text", text = prompt }
            };
            foreach (var jpegBytes in frames)
            {
                if (jpegBytes is { Length: > 0 })
                    parts.Add(new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{Convert.ToBase64String(jpegBytes)}", detail = isChat ? "high" : "auto" } });
            }
            messageContent = parts.ToArray();
        }
        else
        {
            messageContent = prompt;
        }

        var payloadObj = BuildChatCompletionPayload(
            targetModel,
            [new { role = "user", content = messageContent }],
            isChat: isChat,
            maxTokensOverride: maxTokensOverride);

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payloadObj), Encoding.UTF8, "application/json")
        };
        var key = Account.ApiKey.Trim();
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (Account.Provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/hudmind");
            req.Headers.TryAddWithoutValidation("X-Title", "HUDMind");
        }

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return SharedHttpFactory.RedactedHttpFailure(resp, body, key);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var content = SharedHttpFactory.ExtractOpenAiMessageText(choices[0]);
                if (!isChat)
                {
                    var parsed = CompactWireProtocol.ParseCompactLine(content);
                    return parsed is not null
                        ? new ProviderCallResult(parsed)
                        : new ProviderCallResult(null, 422, "Salida compacta no válida");
                }

                var reply = content?.Trim();
                return !string.IsNullOrWhiteSpace(reply)
                    ? new ProviderCallResult(new CoachResponse(true, UrgencyLevel.Medium, reply, reply, "[CHAT]"))
                    : new ProviderCallResult(null, 422, "Sin respuesta de chat.");
            }
            return new ProviderCallResult(null, 422, isChat ? "Sin respuesta de chat." : "Sin choices en respuesta OpenAI-compatible");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return new ProviderCallResult(null, 408, "timeout"); }
        catch (Exception ex) { return new ProviderCallResult(null, 503, SecretRedactor.Redact(ex.Message, [key])); }
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var baseUrl = Account.ResolvedBaseUrl();
        bool allowLoopback = Account.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        var (okUrl, errUrl) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopback);
        if (!okUrl)
            return (false, errUrl);

        var modelsUrl = ResolveModelsEndpoint(baseUrl);

        using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        var key = Account.ApiKey.Trim();
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var safeEndpoint = SecretRedactor.Redact(Account.Provider, [key]);
            return resp.IsSuccessStatusCode
                ? (true, $"OK — Proveedor '{safeEndpoint}' accesible (HTTP/{resp.Version}).")
                : (false, $"HTTP {(int)resp.StatusCode} en '{safeEndpoint}'.");
        }
        catch (Exception ex)
        {
            return (false, $"Error de conexión: {SecretRedactor.Redact(ex.Message, [key])}");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
