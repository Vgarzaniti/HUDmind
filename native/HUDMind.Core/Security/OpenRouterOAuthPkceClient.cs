using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HUDMind.Core.AI;
using HUDMind.Core.Config;

namespace HUDMind.Core.Security;

/// <summary>
/// Zero-NuGet RFC 8252 OAuth 2.0 + PKCE (S256) loopback flow for 1-click user login &amp; key
/// provisioning via OpenRouter with zero server deployment or developer client-secret exposure.
/// </summary>
public static class OpenRouterOAuthPkceClient
{
    public const string AuthEndpoint = "https://openrouter.ai/auth";
    public const string ExchangeEndpoint = "https://openrouter.ai/api/v1/auth/keys";
    private const int MaxHeaderBytes = 4096;

    public static (string Verifier, string Challenge) GeneratePkcePair()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        string verifier = Base64UrlEncode(random);
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        string challenge = Base64UrlEncode(hash);
        return (verifier, challenge);
    }

    public static Uri BuildAuthorizeUri(string callbackUrl, string codeChallenge)
    {
        var (ok, err) = BaseUrlSecurityValidator.Validate(AuthEndpoint);
        if (!ok)
            throw new InvalidOperationException(err);

        string url = $"{AuthEndpoint}?callback_url={Uri.EscapeDataString(callbackUrl)}&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256";
        return new Uri(url, UriKind.Absolute);
    }

    public static async Task<(bool Ok, string ApiKey, string ErrorMessage)> AuthenticateAsync(
        Action<Uri> openBrowser,
        HttpMessageHandler? httpHandler = null,
        string? exchangeEndpointOverride = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        string exchangeUrl = string.IsNullOrWhiteSpace(exchangeEndpointOverride)
            ? ExchangeEndpoint
            : exchangeEndpointOverride.Trim();

        bool isLocalOverride = !string.IsNullOrWhiteSpace(exchangeEndpointOverride);
        var (urlOk, urlErr) = BaseUrlSecurityValidator.Validate(exchangeUrl, allowLoopbackHttp: isLocalOverride);
        if (!urlOk)
            return (false, string.Empty, urlErr);

        var (verifier, challenge) = GeneratePkcePair();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string callbackUrl = $"http://127.0.0.1:{port}/callback";
        Uri authUri = BuildAuthorizeUri(callbackUrl, challenge);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout ?? TimeSpan.FromMinutes(3));

        openBrowser(authUri);

        string? code = null;
        try
        {
            while (!linkedCts.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(linkedCts.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                string? reqLine = await ReadRequestLineAsync(stream, linkedCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(reqLine))
                    continue;

                var parts = reqLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHtmlAsync(stream, 405, "Method Not Allowed", "Método no permitido.").ConfigureAwait(false);
                    continue;
                }

                string rawTarget = parts[1];
                int qIdx = rawTarget.IndexOf('?');
                string path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
                if (!string.Equals(path, "/callback", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHtmlAsync(stream, 404, "Not Found", "Ruta inválida.").ConfigureAwait(false);
                    continue;
                }

                var queryParams = ParseQuery(qIdx >= 0 ? rawTarget[(qIdx + 1)..] : string.Empty);
                if (queryParams.TryGetValue("error", out var oauthErr) && !string.IsNullOrWhiteSpace(oauthErr))
                {
                    await WriteHtmlAsync(stream, 400, "OK", "Autorización cancelada o rechazada. Podés cerrar esta pestaña.").ConfigureAwait(false);
                    return (false, string.Empty, "Autorización cancelada por el usuario.");
                }

                if (queryParams.TryGetValue("code", out var authCode) && !string.IsNullOrWhiteSpace(authCode))
                {
                    code = authCode.Trim();
                    await WriteHtmlAsync(stream, 200, "OK", "¡Conectado a HUDMind! Ya podés cerrar esta pestaña y volver al juego.").ConfigureAwait(false);
                    break;
                }

                await WriteHtmlAsync(stream, 400, "Bad Request", "Falta el código de autorización.").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty, ct.IsCancellationRequested
                ? "Conexión cancelada."
                : "Se agotó el tiempo esperando el login en el navegador.");
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        if (string.IsNullOrWhiteSpace(code))
            return (false, string.Empty, "No se recibió el código de autorización.");

        return await ExchangeCodeForApiKeyAsync(exchangeUrl, code, verifier, httpHandler, linkedCts.Token).ConfigureAwait(false);
    }

    public static async Task<(bool Ok, string ApiKey, string ErrorMessage)> ExchangeCodeForApiKeyAsync(
        string exchangeUrl,
        string code,
        string codeVerifier,
        HttpMessageHandler? httpHandler = null,
        CancellationToken ct = default)
    {
        try
        {
            using var client = httpHandler is null
                ? SharedHttpFactory.CreateSafeClient(allowLoopback: false, timeout: TimeSpan.FromSeconds(15))
                : new HttpClient(httpHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };

            var payload = JsonSerializer.Serialize(new
            {
                code,
                code_verifier = codeVerifier,
                code_challenge_method = "S256"
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(exchangeUrl, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, string.Empty, $"Error {(int)resp.StatusCode} al intercambiar el código OAuth.");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("key", out var keyProp) &&
                keyProp.ValueKind == JsonValueKind.String &&
                keyProp.GetString() is string key &&
                !string.IsNullOrWhiteSpace(key))
            {
                return (true, key.Trim(), string.Empty);
            }

            return (false, string.Empty, "La respuesta OAuth no incluyó una API Key válida.");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"Fallo de red en intercambio PKCE: {SecretRedactor.Redact(ex.Message)}");
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string k = Uri.UnescapeDataString(pair[..eq]);
            string v = Uri.UnescapeDataString(pair[(eq + 1)..]);
            dict[k] = v;
        }
        return dict;
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream ns, CancellationToken ct)
    {
        byte[] buf = new byte[MaxHeaderBytes];
        int total = 0;
        while (total < buf.Length)
        {
            int n = await ns.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
            string text = Encoding.ASCII.GetString(buf, 0, total);
            int eol = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (eol > 0)
                return text[..eol];
        }
        return null;
    }

    private static Task WriteHtmlAsync(NetworkStream ns, int status, string reason, string message)
    {
        string safeMsg = WebUtility.HtmlEncode(message);
        string html =
            "<!doctype html><html lang='es'><head><meta charset='utf-8'><title>HUDMind Auth</title>" +
            "<style>body{margin:0;background:#070c13;color:#f1f5f9;font-family:system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;}" +
            ".card{background:#0b131e;border:1px solid #22d3ee55;border-radius:12px;padding:24px 28px;max-width:420px;text-align:center;}" +
            "h2{margin:0 0 8px;color:#22d3ee;}p{margin:0;color:#cbd5e1;line-height:1.5;}</style></head>" +
            $"<body><div class='card'><h2>HUDMind Native</h2><p>{safeMsg}</p></div></body></html>";

        byte[] payload = Encoding.UTF8.GetBytes(html);
        byte[] hdr = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "X-Frame-Options: DENY\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");

        var combined = new byte[hdr.Length + payload.Length];
        Buffer.BlockCopy(hdr, 0, combined, 0, hdr.Length);
        Buffer.BlockCopy(payload, 0, combined, hdr.Length, payload.Length);
        return ns.WriteAsync(combined, CancellationToken.None).AsTask();
    }
}
