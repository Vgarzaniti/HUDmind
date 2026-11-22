using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

public sealed class GeminiFastProvider : IAIProvider
{
    public ProviderAccount Account { get; }
    private readonly HttpClient _http;

    public GeminiFastProvider(ProviderAccount account, HttpClient? httpClient = null)
    {
        Account = account;
        _http = httpClient ?? SharedHttpFactory.GetSharedClient(allowLoopback: false);
    }

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key) || !SharedHttpFactory.IsSafeModelSegment(Account.Model))
            return;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));
        var safeModel = Uri.EscapeDataString(Account.Model.Trim());
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://generativelanguage.googleapis.com/v1beta/models/{safeModel}");
        req.Headers.Add("x-goog-api-key", key);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            // Drain stream cleanly so the warm HTTP/2 socket returns to the connection pool
            await resp.Content.CopyToAsync(Stream.Null, timeoutCts.Token);
        }
        catch { /* Best-effort socket warmup */ }
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
        var modelLow = Account.Model.ToLowerInvariant();
        int maxOut = modelLow.Contains("gemini-3") ? 160 : 120;
        return SendGenerateContentAsync(jpegBytes, prompt, temperature: 0.35, maxOutputTokens: maxOut, stopOnNewline: true, parseAsCompactWire: true, ct);
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
        return SendGenerateContentAsync(jpegBytes, prompt, temperature: 0.45, maxOutputTokens: 180, stopOnNewline: false, parseAsCompactWire: false, ct);
    }

    public Task<ProviderCallResult> AskRawTextAsync(
        string prompt,
        UserConfig config,
        int maxTokens = 320,
        CancellationToken ct = default) =>
        SendGenerateContentAsync(null, prompt, temperature: 0.45, maxOutputTokens: maxTokens, stopOnNewline: false, parseAsCompactWire: false, ct);

    private async Task<ProviderCallResult> SendGenerateContentAsync(
        byte[]? jpegBytes,
        string prompt,
        double temperature,
        int maxOutputTokens,
        bool stopOnNewline,
        bool parseAsCompactWire,
        CancellationToken ct)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key))
            return new ProviderCallResult(null, 401, "Falta API key de Gemini.");
        if (!SharedHttpFactory.IsSafeModelSegment(Account.Model))
            return new ProviderCallResult(null, 400, "Nombre de modelo inválido.");

        var safeModel = Uri.EscapeDataString(Account.Model.Trim());
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{safeModel}:generateContent";
        var modelLow = Account.Model.ToLowerInvariant();
        bool isGen3 = modelLow.Contains("gemini-3");
        bool isGen25Flash = modelLow.Contains("2.5") && !modelLow.Contains("pro");

        var buf = new ArrayBufferWriter<byte>((jpegBytes?.Length ?? 0) * 4 / 3 + 1024);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WritePropertyName("contents");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WritePropertyName("parts");
            w.WriteStartArray();
            if (jpegBytes is { Length: > 0 })
            {
                w.WriteStartObject();
                w.WritePropertyName("inlineData");
                w.WriteStartObject();
                w.WriteString("mimeType", "image/jpeg");
                w.WriteBase64String("data", jpegBytes);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteStartObject();
            w.WriteString("text", prompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WritePropertyName("generationConfig");
            w.WriteStartObject();
            w.WriteNumber("temperature", temperature);
            w.WriteNumber("maxOutputTokens", maxOutputTokens);
            if (jpegBytes is { Length: > 0 })
                w.WriteString("mediaResolution", "MEDIA_RESOLUTION_MEDIUM");
            if (stopOnNewline)
            {
                w.WritePropertyName("stopSequences");
                w.WriteStartArray();
                w.WriteStringValue("\n");
                w.WriteEndArray();
            }
            if (isGen25Flash)
            {
                w.WritePropertyName("thinkingConfig");
                w.WriteStartObject();
                w.WriteNumber("thinkingBudget", 0);
                w.WriteEndObject();
            }
            else if (isGen3)
            {
                w.WritePropertyName("thinkingConfig");
                w.WriteStartObject();
                w.WriteString("thinkingLevel", "MINIMAL");
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        using var content = new ReadOnlyMemoryContent(buf.WrittenMemory);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Add("x-goog-api-key", key);

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return SharedHttpFactory.RedactedHttpFailure(resp, body, key);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0 &&
                cands[0].TryGetProperty("content", out var contentEl) && contentEl.TryGetProperty("parts", out var parts))
            {
                var textOut = SharedHttpFactory.ExtractGeminiTextExcludingThoughts(parts);
                if (parseAsCompactWire)
                {
                    var parsed = CompactWireProtocol.ParseCompactLine(textOut);
                    return parsed is not null
                        ? new ProviderCallResult(parsed)
                        : new ProviderCallResult(null, 422, "Salida compacta no válida");
                }
                var trimmed = textOut.Trim();
                return !string.IsNullOrWhiteSpace(trimmed)
                    ? new ProviderCallResult(new CoachResponse(true, UrgencyLevel.Medium, trimmed, trimmed, "[CHAT]"))
                    : new ProviderCallResult(null, 422, "Respuesta de chat vacía.");
            }
            return new ProviderCallResult(null, 422, "Sin candidatos en respuesta Gemini");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return new ProviderCallResult(null, 408, "timeout"); }
        catch (Exception ex) { return new ProviderCallResult(null, 503, SecretRedactor.Redact(ex.Message, [key])); }
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key))
            return (false, "Falta API key de Gemini.");
        if (!SharedHttpFactory.IsSafeModelSegment(Account.Model))
            return (false, "Nombre de modelo inválido.");
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
