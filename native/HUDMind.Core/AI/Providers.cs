using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

public sealed record ProviderCallResult(
    CoachResponse? Response,
    int? ErrorCode = null,
    string ErrorMessage = "",
    double? RetryAfterSeconds = null
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
        string spatialTelemetry = "",
        HudSensorSnapshot? sensorSnapshot = null,
        CancellationToken ct = default);
    Task<ProviderCallResult> AskQuestionAsync(
        byte[]? jpegBytes,
        string userQuestion,
        GameProfile profile,
        UserConfig config,
        string recentHistory = "",
        string visualTimeline = "",
        HudSensorSnapshot? sensorSnapshot = null,
        string recentChatHistory = "",
        CancellationToken ct = default) =>
        Task.FromResult(new ProviderCallResult(null, 501, "Chat interactivo no implementado en este adaptador."));
    Task<ProviderCallResult> AskRawTextAsync(
        string prompt,
        UserConfig config,
        int maxTokens = 320,
        CancellationToken ct = default) =>
        AskQuestionAsync(
            null,
            prompt,
            new GameProfile { DirectoryPath = ".", Manifest = new GameManifest { GameId = "universal", DisplayName = "Post-Match Review" } },
            config,
            ct: ct);
    Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default);
}

public static class SharedHttpFactory
{
    private static readonly Lazy<HttpClient> SharedRemoteClient = new(() => CreateSafeClient(allowLoopback: false));
    private static readonly Lazy<HttpClient> SharedLoopbackClient = new(() => CreateSafeClient(allowLoopback: true));

    public static HttpClient GetSharedClient(bool allowLoopback = false) =>
        allowLoopback ? SharedLoopbackClient.Value : SharedRemoteClient.Value;

    public static HttpClient CreateSafeClient(bool allowLoopback = false)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            AllowAutoRedirect = false, // Mitigates SSRF via 302/307 redirect (CWE-918)
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addrs = await Dns.GetHostAddressesAsync(host, ct);
                IPAddress? chosen = null;
                foreach (var ip in addrs)
                {
                    if (BaseUrlSecurityValidator.IsBlockedAddress(ip, allowLoopback))
                        throw new HttpRequestException($"Conexión bloqueada a IP privada/metadata (CWE-918): {host}");
                    chosen ??= ip;
                }
                if (chosen is null)
                    throw new HttpRequestException($"No se pudo resolver el host: {host}");

                var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(chosen, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12.0), // Per-call timeouts enforced via linked CTS
            MaxResponseContentBufferSize = 2 * 1024 * 1024, // Prevents CWE-400 unbounded 2GB read DoS
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HUDMind-Native/1.1");
        return client;
    }

    internal static bool IsSafeModelSegment(string? model)
    {
        var m = (model ?? string.Empty).Trim();
        return m.Length is >= 1 and <= 128 &&
               !m.Contains("..", StringComparison.Ordinal) &&
               !m.Contains('?') &&
               !m.Contains('#') &&
               m.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/');
    }

    internal static string ExtractGeminiTextExcludingThoughts(JsonElement parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts.EnumerateArray())
        {
            if (p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True)
                continue;
            if (p.TryGetProperty("text", out var t))
                sb.Append(t.GetString());
        }
        return sb.ToString();
    }

    internal static string? ExtractOpenAiMessageText(JsonElement choice)
    {
        if (!choice.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var contentEl))
            return null;

        if (contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString();

        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in contentEl.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                    sb.Append(part.GetString());
                else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var tEl))
                    sb.Append(tEl.GetString());
            }
            return sb.ToString();
        }
        return null;
    }

    internal static double? ExtractRetryAfterSeconds(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta.HasValue) return Math.Clamp(ra.Delta.Value.TotalSeconds, 1.0, 300.0);
        if (ra.Date.HasValue) return Math.Clamp((ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds, 1.0, 300.0);
        return null;
    }
}

public sealed class GeminiFastProvider : IAIProvider
{
    public ProviderAccount Account { get; }
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GeminiFastProvider(ProviderAccount account, HttpClient? httpClient = null)
    {
        Account = account;
        _ownsHttp = false;
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
        catch
        {
            // Best-effort socket warmup
        }
    }

    public async Task<ProviderCallResult> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        string spatialTelemetry = "",
        HudSensorSnapshot? sensorSnapshot = null,
        CancellationToken ct = default)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key))
            return new ProviderCallResult(null, 401, "Falta API key de Gemini.");
        if (!SharedHttpFactory.IsSafeModelSegment(Account.Model))
            return new ProviderCallResult(null, 400, "Nombre de modelo inválido.");

        var safeModel = Uri.EscapeDataString(Account.Model.Trim());
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{safeModel}:generateContent";
        var prompt = CompactWireProtocol.BuildRealtimePrompt(profile, config, recentHistory, forceAdvice, visualTimeline: spatialTelemetry);
        var modelLow = Account.Model.ToLowerInvariant();
        bool isGen3 = modelLow.Contains("gemini-3");
        bool isGen25Flash = modelLow.Contains("2.5") && !modelLow.Contains("pro");
        // Grounded 4-field line (FASE|QUE_VEO|URG|CONSEJO) + clear 10-16 word sentence + stopSequences=["\n"]
        int maxOut = isGen3 ? 160 : 120;

        var buf = new ArrayBufferWriter<byte>(jpegBytes.Length * 4 / 3 + 1024);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WritePropertyName("contents");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WritePropertyName("parts");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WritePropertyName("inlineData");
            w.WriteStartObject();
            w.WriteString("mimeType", "image/jpeg");
            w.WriteBase64String("data", jpegBytes);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("text", prompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WritePropertyName("generationConfig");
            w.WriteStartObject();
            w.WriteNumber("temperature", 0.35);
            w.WriteNumber("maxOutputTokens", maxOut);
            // MEDIA_RESOLUTION_MEDIUM (256 vision tokens vs 64 LOW) makes HUD numbers, kickoffs & replays legible!
            w.WriteString("mediaResolution", "MEDIA_RESOLUTION_MEDIUM");
            w.WritePropertyName("stopSequences");
            w.WriteStartArray();
            w.WriteStringValue("\n");
            w.WriteEndArray();
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
            {
                var safeErr = SecretRedactor.Redact(body.Length > 200 ? body[..200] : body, [key]);
                return new ProviderCallResult(null, (int)resp.StatusCode, safeErr, SharedHttpFactory.ExtractRetryAfterSeconds(resp));
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0)
            {
                var c0 = cands[0];
                if (c0.TryGetProperty("content", out var contentEl) && contentEl.TryGetProperty("parts", out var parts))
                {
                    var textOut = SharedHttpFactory.ExtractGeminiTextExcludingThoughts(parts);
                    var parsed = CompactWireProtocol.ParseCompactLine(textOut);
                    return parsed is not null
                        ? new ProviderCallResult(parsed)
                        : new ProviderCallResult(null, 422, "Salida compacta no válida");
                }
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
        return SendGeminiPromptAsync(jpegBytes, prompt, maxTokens: 180, ct);
    }

    public Task<ProviderCallResult> AskRawTextAsync(
        string prompt,
        UserConfig config,
        int maxTokens = 320,
        CancellationToken ct = default) =>
        SendGeminiPromptAsync(null, prompt, maxTokens, ct);

    private async Task<ProviderCallResult> SendGeminiPromptAsync(
        byte[]? jpegBytes,
        string prompt,
        int maxTokens,
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
            w.WriteNumber("temperature", 0.45);
            w.WriteNumber("maxOutputTokens", maxTokens);
            if (jpegBytes is { Length: > 0 })
                w.WriteString("mediaResolution", "MEDIA_RESOLUTION_MEDIUM");
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
            {
                var safeErr = SecretRedactor.Redact(body.Length > 200 ? body[..200] : body, [key]);
                return new ProviderCallResult(null, (int)resp.StatusCode, safeErr, SharedHttpFactory.ExtractRetryAfterSeconds(resp));
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0 &&
                cands[0].TryGetProperty("content", out var contentEl) && contentEl.TryGetProperty("parts", out var parts))
            {
                var reply = SharedHttpFactory.ExtractGeminiTextExcludingThoughts(parts).Trim();
                if (!string.IsNullOrWhiteSpace(reply))
                    return new ProviderCallResult(new CoachResponse(true, UrgencyLevel.Medium, reply, reply, "[CHAT]"));
            }
            return new ProviderCallResult(null, 422, "Respuesta de chat vacía.");
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

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

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

    public async Task<ProviderCallResult> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        string spatialTelemetry = "",
        HudSensorSnapshot? sensorSnapshot = null,
        CancellationToken ct = default)
    {
        var key = Account.ApiKey.Trim();
        if (string.IsNullOrEmpty(key))
            return new ProviderCallResult(null, 401, "Falta API key de Anthropic Claude.");

        var baseUrl = Account.ResolvedBaseUrl();
        var (okUrl, errUrl) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopbackHttp: false);
        if (!okUrl)
            return new ProviderCallResult(null, 400, errUrl);

        var prompt = CompactWireProtocol.BuildRealtimePrompt(profile, config, recentHistory, forceAdvice, visualTimeline: spatialTelemetry);
        var b64 = Convert.ToBase64String(jpegBytes);
        var payload = new
        {
            model = Account.Model,
            max_tokens = 115,
            temperature = 0.35,
            stop_sequences = new[] { "\n" },
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = b64 } },
                        new { type = "text", text = prompt }
                    }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var safeErr = SecretRedactor.Redact(body.Length > 200 ? body[..200] : body, [key]);
                return new ProviderCallResult(null, (int)resp.StatusCode, safeErr, SharedHttpFactory.ExtractRetryAfterSeconds(resp));
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("content", out var blocks))
            {
                var sb = new StringBuilder();
                foreach (var b in blocks.EnumerateArray())
                {
                    if (b.TryGetProperty("text", out var t))
                        sb.Append(t.GetString());
                }
                var parsed = CompactWireProtocol.ParseCompactLine(sb.ToString());
                return parsed is not null
                    ? new ProviderCallResult(parsed)
                    : new ProviderCallResult(null, 422, "Salida Claude no válida");
            }
            return new ProviderCallResult(null, 422, "Respuesta vacía de Claude");
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
        return SendClaudePromptAsync(jpegBytes, prompt, maxTokens: 180, ct);
    }

    public Task<ProviderCallResult> AskRawTextAsync(
        string prompt,
        UserConfig config,
        int maxTokens = 320,
        CancellationToken ct = default) =>
        SendClaudePromptAsync(null, prompt, maxTokens, ct);

    private async Task<ProviderCallResult> SendClaudePromptAsync(
        byte[]? jpegBytes,
        string prompt,
        int maxTokens,
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

        var payload = new
        {
            model = Account.Model,
            max_tokens = maxTokens,
            temperature = 0.45,
            messages = new[] { new { role = "user", content = partsList.ToArray() } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var safeErr = SecretRedactor.Redact(body.Length > 200 ? body[..200] : body, [key]);
                return new ProviderCallResult(null, (int)resp.StatusCode, safeErr, SharedHttpFactory.ExtractRetryAfterSeconds(resp));
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("content", out var blocks))
            {
                var sb = new StringBuilder();
                foreach (var b in blocks.EnumerateArray())
                {
                    if (b.TryGetProperty("text", out var t))
                        sb.Append(t.GetString());
                }
                var reply = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(reply))
                    return new ProviderCallResult(new CoachResponse(true, UrgencyLevel.Medium, reply, reply, "[CHAT]"));
            }
            return new ProviderCallResult(null, 422, "Respuesta vacía de Claude.");
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
                : (false, $"HTTP {(int)resp.StatusCode} en Anthropic API.");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {SecretRedactor.Redact(ex.Message, [key])}");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class OpenAICompatibleProvider : IAIProvider
{
    public ProviderAccount Account { get; }
    private readonly HttpClient _http;

    public OpenAICompatibleProvider(ProviderAccount account, HttpClient? httpClient = null)
    {
        Account = account;
        bool allowLoopback = account.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        _http = httpClient ?? SharedHttpFactory.GetSharedClient(allowLoopback);
    }

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        var baseUrl = Account.ResolvedBaseUrl();
        bool allowLoopback = Account.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        var (okUrl, _) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopback);
        if (!okUrl) return;

        var modelsUrl = Account.Provider.Equals("pollinations", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Account.ApiKey)
            ? "https://text.pollinations.ai/models"
            : $"{baseUrl}/models";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));
        using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        var key = Account.ApiKey.Trim();
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            await resp.Content.CopyToAsync(Stream.Null, timeoutCts.Token);
        }
        catch
        {
            // Best-effort warmup
        }
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

    public async Task<ProviderCallResult> AnalyzeFrameAsync(
        byte[] jpegBytes,
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        string spatialTelemetry = "",
        HudSensorSnapshot? sensorSnapshot = null,
        CancellationToken ct = default)
    {
        var baseUrl = Account.ResolvedBaseUrl();
        bool allowLoopback = Account.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        var (okUrl, errUrl) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopback);
        if (!okUrl)
            return new ProviderCallResult(null, 400, errUrl);

        var endpoint = ResolveChatCompletionsEndpoint(baseUrl);
        string targetModel = string.IsNullOrWhiteSpace(Account.Model) ? "openai-fast" : Account.Model;

        Dictionary<string, object> payloadObj;
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

            payloadObj = BuildChatCompletionPayload(
                targetModel,
                [new { role = "user", content = sensorPrompt }],
                isChat: false);
        }
        else
        {
            var prompt = CompactWireProtocol.BuildRealtimePrompt(
                profile, config, recentHistory, forceAdvice,
                visualTimeline: spatialTelemetry);
            var b64 = Convert.ToBase64String(jpegBytes);
            payloadObj = BuildChatCompletionPayload(
                targetModel,
                [
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new
                            {
                                type = "image_url",
                                image_url = new { url = $"data:image/jpeg;base64,{b64}", detail = "auto" }
                            }
                        }
                    }
                ],
                isChat: false);
        }

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
            {
                var safeErr = SecretRedactor.Redact(body.Length > 200 ? body[..200] : body, [key]);
                return new ProviderCallResult(null, (int)resp.StatusCode, safeErr, SharedHttpFactory.ExtractRetryAfterSeconds(resp));
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var content = SharedHttpFactory.ExtractOpenAiMessageText(choices[0]);
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
            profile, config, userQuestion, recentHistory, visualTimeline,
            Account.SupportsVision ? null : sensorSnapshot,
            recentChatHistory);
        return SendOpenAiPromptAsync(jpegBytes, prompt, maxTokens: 180, ct);
    }

    public Task<ProviderCallResult> AskRawTextAsync(
        string prompt,
        UserConfig config,
        int maxTokens = 320,
        CancellationToken ct = default) =>
        SendOpenAiPromptAsync(null, prompt, maxTokens, ct);

    private async Task<ProviderCallResult> SendOpenAiPromptAsync(
        byte[]? jpegBytes,
        string prompt,
        int maxTokens,
        CancellationToken ct)
    {
        var baseUrl = Account.ResolvedBaseUrl();
        bool allowLoopback = Account.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        var (okUrl, errUrl) = BaseUrlSecurityValidator.Validate(baseUrl, allowLoopback);
        if (!okUrl)
            return new ProviderCallResult(null, 400, errUrl);

        var endpoint = ResolveChatCompletionsEndpoint(baseUrl);
        string targetModel = string.IsNullOrWhiteSpace(Account.Model) ? "openai-fast" : Account.Model;

        object messageContent = Account.SupportsVision && jpegBytes is { Length: > 0 }
            ? new object[]
            {
                new { type = "text", text = prompt },
                new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{Convert.ToBase64String(jpegBytes)}", detail = "auto" } }
            }
            : prompt;

        var payloadObj = BuildChatCompletionPayload(
            targetModel,
            [new { role = "user", content = messageContent }],
            isChat: true,
            maxTokensOverride: maxTokens);

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
            {
                var safeErr = SecretRedactor.Redact(body.Length > 200 ? body[..200] : body, [key]);
                return new ProviderCallResult(null, (int)resp.StatusCode, safeErr, SharedHttpFactory.ExtractRetryAfterSeconds(resp));
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var reply = SharedHttpFactory.ExtractOpenAiMessageText(choices[0])?.Trim();
                if (!string.IsNullOrWhiteSpace(reply))
                    return new ProviderCallResult(new CoachResponse(true, UrgencyLevel.Medium, reply, reply, "[CHAT]"));
            }
            return new ProviderCallResult(null, 422, "Sin respuesta de chat.");
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

        var modelsUrl = Account.Provider.Equals("pollinations", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Account.ApiKey)
            ? "https://text.pollinations.ai/models"
            : $"{baseUrl}/models";

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
        acc.Provider.ToLowerInvariant() switch
        {
            "gemini" => new GeminiFastProvider(acc),
            "claude" => new AnthropicClaudeProvider(acc),
            _ => new OpenAICompatibleProvider(acc)
        };

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
        CancellationToken ct = default)
    {
        if (_providers.Count == 0 || string.IsNullOrWhiteSpace(sessionDigest))
            return (null, 0, "none");

        var prompt = CoachingPersonaBuilder.BuildPostMatchReviewPrompt(gameDisplayName, sessionDigest, config);
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
