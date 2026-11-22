using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
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

/// <summary>
/// Shared SSRF-protected HTTP/2 connection pool and provider JSON/error helpers.
/// </summary>
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

    internal static ProviderCallResult RedactedHttpFailure(HttpResponseMessage resp, string body, string key)
    {
        var snippet = body.Length > 200 ? body[..200] : body;
        var safeErr = SecretRedactor.Redact(snippet, string.IsNullOrEmpty(key) ? null : [key]);
        return new ProviderCallResult(null, (int)resp.StatusCode, safeErr, ExtractRetryAfterSeconds(resp));
    }
}
