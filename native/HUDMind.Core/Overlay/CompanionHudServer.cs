using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;

namespace HUDMind.Core.Overlay;

/// <summary>
/// Thread-safe overlay multiplexer forwarding HUD updates simultaneously to multiple sinks
/// (e.g., local 0%-GPU WPF click-through overlay + Web Companion SSE HUD for Exclusive Fullscreen).
/// </summary>
public sealed class CompositeOverlayService : IOverlayOutputService
{
    private readonly IReadOnlyList<IOverlayOutputService> _targets;

    public CompositeOverlayService(params IOverlayOutputService[] targets)
    {
        _targets = targets.Where(t => t is not null).ToArray();
    }

    public void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds)
    {
        foreach (var target in _targets)
            target.ShowCallout(text, urgency, title, durationSeconds);
    }

    public void ShowMessage(OverlayCalloutMessage message)
    {
        foreach (var target in _targets)
            target.ShowMessage(message);
    }

    public void UpdateVoiceActivity(bool userMicActive, double userLevel01, bool coachVoiceActive, double coachLevel01)
    {
        foreach (var target in _targets)
            target.UpdateVoiceActivity(userMicActive, userLevel01, coachVoiceActive, coachLevel01);
    }

    public void ClearHistory()
    {
        foreach (var target in _targets)
            target.ClearHistory();
    }
}

/// <summary>
/// Zero-NuGet, 0%-GPU embedded HTTP + Server-Sent Events (SSE) Companion HUD for Exclusive Fullscreen play
/// via 2nd monitor, OBS Browser Source, or local-WiFi mobile browser.
/// Security: binds strictly to 127.0.0.1 unless LAN opt-in is enabled, enforces constant-time per-session
/// token auth for any non-loopback client, rejects oversized/unknown requests, and renders DOM via textContent only.
/// </summary>
public sealed class CompanionHudServer : IOverlayOutputService, IAsyncDisposable
{
    private const int MaxClients = 8;
    private const int MaxHeaderBytes = 4096;

    private readonly object _stateLock = new();
    private readonly OverlayFeedBuffer _feed = new();
    private readonly List<SseClient> _clients = [];
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _keepAliveTask;

    private string _sessionToken = GenerateToken();
    private int _boundPort;
    private bool _allowLan;
    private bool _userMicActive;
    private bool _coachVoiceActive;
    private volatile bool _disposed;

    public bool IsRunning => _listener is not null && _cts is { IsCancellationRequested: false };
    public int BoundPort => _boundPort;
    public bool AllowLan => _allowLan;
    public string SessionToken => _sessionToken;
    public string LocalUrl => _boundPort > 0 ? $"http://127.0.0.1:{_boundPort}/" : string.Empty;
    public string ObsLocalUrl => _boundPort > 0 ? $"http://127.0.0.1:{_boundPort}/?mode=obs" : string.Empty;

    public int ConnectedClientCount
    {
        get { lock (_stateLock) return _clients.Count; }
    }

    public string LanUrl
    {
        get
        {
            if (_boundPort <= 0 || !_allowLan)
                return string.Empty;
            var ip = ResolvePrimaryPrivateIpv4();
            return string.IsNullOrEmpty(ip)
                ? string.Empty
                : $"http://{ip}:{_boundPort}/?k={_sessionToken}";
        }
    }

    public void ConfigureMaxMessages(int maxMessages)
    {
        lock (_stateLock)
            _feed.MaxMessages = Math.Clamp(maxMessages, 1, 4);
    }

    public async Task<(bool Started, string ActiveUrl, string? Warning)> EnsureStartedAsync(
        int preferredPort = 17890,
        bool allowLan = false,
        int maxMessages = 3,
        CancellationToken ct = default)
    {
        if (_disposed)
            return (false, string.Empty, "disposed");

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return (false, string.Empty, "disposed");

            ConfigureMaxMessages(maxMessages);

            if (IsRunning && _allowLan == allowLan &&
                (preferredPort <= 0 || Math.Abs(_boundPort - Math.Clamp(preferredPort, 1024, 65535)) <= 5))
            {
                return (true, _allowLan && !string.IsNullOrEmpty(LanUrl) ? LanUrl : LocalUrl, null);
            }

            await StopCoreLockedAsync().ConfigureAwait(false);

            _allowLan = allowLan;
            _sessionToken = GenerateToken();

            var bindIp = allowLan ? IPAddress.Any : IPAddress.Loopback;
            int basePort = Math.Clamp(preferredPort <= 0 ? 17890 : preferredPort, 1024, 65535);
            TcpListener? bound = TryBindPort(bindIp, basePort);
            if (bound is null)
                return (false, string.Empty, $"No se pudo abrir puerto local para Companion Web HUD ({basePort}).");

            _listener = bound;
            _boundPort = ((IPEndPoint)bound.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            var runToken = _cts.Token;
            _acceptTask = Task.Run(() => AcceptLoopAsync(bound, runToken), runToken);
            _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(runToken), runToken);

            var displayUrl = _allowLan && !string.IsNullOrEmpty(LanUrl) ? LanUrl : LocalUrl;
            return (true, displayUrl, null);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds) =>
        ShowMessage(new OverlayCalloutMessage(text, urgency, title, durationSeconds, OverlayEntryKind.CoachFinal));

    public void ShowMessage(OverlayCalloutMessage message)
    {
        if (_disposed) return;
        string? json;
        lock (_stateLock)
        {
            _feed.Upsert(message, DateTimeOffset.UtcNow);
            json = IsRunning ? BuildStateJsonLocked() : null;
        }
        if (json is not null)
            BroadcastSsePayload($"event: state\ndata: {json}\n\n");
    }

    public void UpdateVoiceActivity(bool userMicActive, double userLevel01, bool coachVoiceActive, double coachLevel01)
    {
        if (_disposed) return;
        string? json = null;
        lock (_stateLock)
        {
            if (_userMicActive == userMicActive && _coachVoiceActive == coachVoiceActive)
                return;
            _userMicActive = userMicActive;
            _coachVoiceActive = coachVoiceActive;
            if (IsRunning)
                json = BuildStateJsonLocked();
        }
        if (json is not null)
            BroadcastSsePayload($"event: state\ndata: {json}\n\n");
    }

    public void ClearHistory()
    {
        if (_disposed) return;
        string? json;
        lock (_stateLock)
        {
            _feed.Clear();
            json = IsRunning ? BuildStateJsonLocked() : null;
        }
        if (json is not null)
            BroadcastSsePayload($"event: state\ndata: {json}\n\n");
    }

    private async Task StopCoreLockedAsync()
    {
        var cts = _cts;
        _cts = null;
        var listener = _listener;
        _listener = null;
        _boundPort = 0;

        if (cts is not null)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); } catch { }
        }
        try { listener?.Stop(); } catch { }

        List<SseClient> toClose;
        lock (_stateLock)
        {
            toClose = [.. _clients];
            _clients.Clear();
        }
        foreach (var c in toClose)
            c.Dispose();

        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
            _acceptTask = null;
        }
        if (_keepAliveTask is not null)
        {
            try { await _keepAliveTask.ConfigureAwait(false); } catch { }
            _keepAliveTask = null;
        }
        cts?.Dispose();
    }

    private static TcpListener? TryBindPort(IPAddress ip, int basePort)
    {
        for (int offset = 0; offset <= 5; offset++)
        {
            int candidate = basePort + offset;
            if (candidate > 65535) break;
            try
            {
                var listener = new TcpListener(ip, candidate);
                listener.Start(16);
                return listener;
            }
            catch (SocketException) { }
        }

        try
        {
            var fallback = new TcpListener(ip, 0);
            fallback.Start(16);
            return fallback;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            _ = Task.Run(() => HandleClientAsync(tcp, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken serverCt)
    {
        bool handedOffToSse = false;
        try
        {
            tcp.NoDelay = true;
            var remoteIp = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
            bool isLoopback = IPAddress.IsLoopback(remoteIp);
            if (!isLoopback && !_allowLan)
            {
                tcp.Close();
                return;
            }

            var ns = tcp.GetStream();
            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            headerCts.CancelAfter(TimeSpan.FromSeconds(2));

            string? requestLine = await ReadBoundedRequestHeadersAsync(ns, headerCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(requestLine))
            {
                await WriteSimpleResponseAsync(ns, 400, "Bad Request", "text/plain", "Bad Request").ConfigureAwait(false);
                return;
            }

            string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteSimpleResponseAsync(ns, 400, "Bad Request", "text/plain", "Bad Request").ConfigureAwait(false);
                return;
            }

            if (!string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSimpleResponseAsync(ns, 405, "Method Not Allowed", "text/plain", "GET only").ConfigureAwait(false);
                return;
            }

            var (path, queryToken) = SplitPathAndToken(parts[1]);
            if (!isLoopback && !IsValidToken(queryToken, _sessionToken))
            {
                await WriteSimpleResponseAsync(ns, 403, "Forbidden", "text/plain", "Invalid session token").ConfigureAwait(false);
                return;
            }

            if (path == "/health")
            {
                await WriteSimpleResponseAsync(ns, 200, "OK", "application/json; charset=utf-8", "{\"ok\":true}").ConfigureAwait(false);
                return;
            }

            if (path == "/")
            {
                await WriteHtmlPageAsync(ns).ConfigureAwait(false);
                return;
            }

            if (path == "/events")
            {
                await AttachSseClientAsync(tcp, ns, serverCt).ConfigureAwait(false);
                handedOffToSse = true;
                return;
            }

            await WriteSimpleResponseAsync(ns, 404, "Not Found", "text/plain", "Not Found").ConfigureAwait(false);
        }
        catch { /* Clean disconnect/timeout on malformed or aborted socket */ }
        finally
        {
            if (!handedOffToSse)
                try { tcp.Dispose(); } catch { }
        }
    }

    private async Task AttachSseClientAsync(TcpClient tcp, NetworkStream ns, CancellationToken serverCt)
    {
        var headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            "Cache-Control: no-store, no-cache\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: keep-alive\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(headers), serverCt).ConfigureAwait(false);

        string initialState;
        lock (_stateLock)
        {
            _feed.PruneExpired(DateTimeOffset.UtcNow);
            initialState = BuildStateJsonLocked();
        }

        var client = new SseClient(tcp, ns, () => RemoveClientByRef(tcp));
        lock (_stateLock)
        {
            if (_disposed || _cts is { IsCancellationRequested: true })
            {
                client.Dispose();
                return;
            }
            while (_clients.Count >= MaxClients)
            {
                var evicted = _clients[0];
                _clients.RemoveAt(0);
                evicted.Dispose();
            }
            _clients.Add(client);
        }

        client.Enqueue($"event: state\ndata: {initialState}\n\n");
    }

    private void RemoveClientByRef(TcpClient tcp)
    {
        lock (_stateLock)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_clients[i].Tcp, tcp))
                {
                    var dead = _clients[i];
                    _clients.RemoveAt(i);
                    dead.Dispose();
                }
            }
        }
    }

    private void BroadcastSsePayload(string sseFrame)
    {
        SseClient[] snapshot;
        lock (_stateLock)
        {
            if (_clients.Count == 0)
                return;
            snapshot = [.. _clients];
        }
        foreach (var client in snapshot)
            client.Enqueue(sseFrame);
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                string? expiredUpdate = null;
                lock (_stateLock)
                {
                    if (_feed.PruneExpired(DateTimeOffset.UtcNow))
                        expiredUpdate = BuildStateJsonLocked();
                }
                BroadcastSsePayload(expiredUpdate is not null
                    ? $"event: state\ndata: {expiredUpdate}\n\n"
                    : ": keepalive\n\n");
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private string BuildStateJsonLocked()
    {
        var entries = _feed.Snapshot();
        var dto = new CompanionStatePayload(
            Entries: entries.Select(e => new CompanionEntryDto(
                e.Id,
                e.Kind.ToString(),
                e.Speaker,
                e.Text,
                e.Urgency.ToString(),
                e.GameTitle)).ToArray(),
            UserMicActive: _userMicActive,
            CoachVoiceActive: _coachVoiceActive,
            SentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return JsonSerializer.Serialize(dto);
    }

    private static async Task<string?> ReadBoundedRequestHeadersAsync(NetworkStream ns, CancellationToken ct)
    {
        byte[] buf = new byte[MaxHeaderBytes];
        int total = 0;
        while (total < buf.Length)
        {
            int n = await ns.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
            if (n <= 0)
                break;
            total += n;
            int headerEnd = FindHeaderEnd(buf, total);
            if (headerEnd >= 0)
            {
                string block = Encoding.ASCII.GetString(buf, 0, headerEnd);
                int cr = block.IndexOf('\r');
                int lf = block.IndexOf('\n');
                int eol = cr >= 0 && lf >= 0 ? Math.Min(cr, lf) : Math.Max(cr, lf);
                return eol > 0 ? block[..eol] : block;
            }
        }
        return null;
    }

    private static int FindHeaderEnd(byte[] buf, int len)
    {
        for (int i = 0; i <= len - 4; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static (string Path, string Token) SplitPathAndToken(string rawTarget)
    {
        int qIdx = rawTarget.IndexOf('?');
        string path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
        if (qIdx < 0 || qIdx >= rawTarget.Length - 1)
            return (path, string.Empty);

        string query = rawTarget[(qIdx + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (pair[..eq] == "k")
                return (path, Uri.UnescapeDataString(pair[(eq + 1)..]));
        }
        return (path, string.Empty);
    }

    private static bool IsValidToken(string provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
            return false;
        byte[] a = Encoding.UTF8.GetBytes(provided);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static Task WriteSimpleResponseAsync(NetworkStream ns, int status, string reason, string contentType, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        byte[] hdr = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        var combined = new byte[hdr.Length + payload.Length];
        Buffer.BlockCopy(hdr, 0, combined, 0, hdr.Length);
        Buffer.BlockCopy(payload, 0, combined, hdr.Length, payload.Length);
        return ns.WriteAsync(combined, CancellationToken.None).AsTask();
    }

    private static Task WriteHtmlPageAsync(NetworkStream ns)
    {
        byte[] html = Encoding.UTF8.GetBytes(CompanionHtmlPage);
        byte[] hdr = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {html.Length}\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "X-Frame-Options: DENY\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        var combined = new byte[hdr.Length + html.Length];
        Buffer.BlockCopy(hdr, 0, combined, 0, hdr.Length);
        Buffer.BlockCopy(html, 0, combined, hdr.Length, html.Length);
        return ns.WriteAsync(combined, CancellationToken.None).AsTask();
    }

    private static string ResolvePrimaryPrivateIpv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                    {
                        var bytes = ua.Address.GetAddressBytes();
                        bool isPrivate = bytes[0] == 10 ||
                                         (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                                         (bytes[0] == 192 && bytes[1] == 168);
                        if (isPrivate)
                            return ua.Address.ToString();
                    }
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    private sealed record CompanionEntryDto(long Id, string Kind, string Speaker, string Text, string Urgency, string GameTitle);
    private sealed record CompanionStatePayload(CompanionEntryDto[] Entries, bool UserMicActive, bool CoachVoiceActive, long SentAtUnixMs);

    private sealed class SseClient : IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly Action _onFault;
        private readonly Queue<byte[]> _queue = new();
        private readonly object _sync = new();
        private bool _draining;
        private bool _disposed;

        public TcpClient Tcp { get; }

        public SseClient(TcpClient tcp, NetworkStream stream, Action onFault)
        {
            Tcp = tcp;
            _stream = stream;
            _onFault = onFault;
        }

        public void Enqueue(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            lock (_sync)
            {
                if (_disposed) return;
                if (_queue.Count >= 16)
                    _queue.Dequeue();
                _queue.Enqueue(bytes);
                if (!_draining)
                {
                    _draining = true;
                    _ = Task.Run(DrainAsync);
                }
            }
        }

        private async Task DrainAsync()
        {
            while (true)
            {
                byte[]? next;
                lock (_sync)
                {
                    if (_disposed || _queue.Count == 0)
                    {
                        _draining = false;
                        return;
                    }
                    next = _queue.Dequeue();
                }

                try
                {
                    using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _stream.WriteAsync(next, writeCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    _onFault();
                    return;
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _queue.Clear();
            }
            try { _stream.Dispose(); } catch { }
            try { Tcp.Dispose(); } catch { }
        }
    }

    private const string CompanionHtmlPage = """
<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<title>HUDMind Companion</title>
<style>
  :root { color-scheme: dark; }
  body { margin:0; background:#070d15; color:#f1f5f9; font-family:system-ui,-apple-system,Segoe UI,sans-serif; padding:12px; }
  body.obs-mode { background:transparent; padding:8px; }
  body.obs-mode .bar, body.obs-mode .empty { display:none; }
  body.obs-mode .pill { background:rgba(7,13,21,.88); box-shadow:0 4px 14px rgba(0,0,0,.55); }
  body.obs-mode .txt { text-shadow:0 1px 2px #000; }
  .bar { display:flex; justify-content:space-between; align-items:center; font-size:11px; opacity:.78; margin-bottom:10px; }
  .badge { padding:2px 8px; border-radius:999px; background:#0f172a; border:1px solid #22d3ee44; color:#22d3ee; font-weight:700; }
  .pill { border:1px solid #22d3ee55; border-left:4px solid #22d3ee; background:rgba(10,19,30,.94); border-radius:10px; padding:10px 12px; margin-bottom:8px; }
  .pill.High { border-color:#f43f5e88; border-left-color:#fb7185; }
  .pill.Low { border-color:#64748b66; border-left-color:#38bdf8; }
  .meta { display:flex; justify-content:space-between; font-size:11px; color:#94a3b8; margin-bottom:4px; font-weight:600; }
  .txt { font-size:17px; font-weight:700; line-height:1.3; word-break:break-word; }
  .empty { font-size:13px; color:#64748b; padding:16px; border:1px dashed #1e293b; border-radius:10px; text-align:center; }
</style>
</head>
<body>
  <div class="bar">
    <span class="badge">HUDMIND COMPANION · LIVE</span>
    <span id="status">Conectando...</span>
  </div>
  <div id="hud"><div class="empty">Esperando callouts en vivo...</div></div>
  <script>
    const params = new URLSearchParams(location.search);
    if (params.get('mode') === 'obs') { document.body.classList.add('obs-mode'); }
    const hud = document.getElementById('hud');
    const st = document.getElementById('status');
    const es = new EventSource('/events' + location.search);
    es.onopen = () => { st.textContent = 'En línea · 0% GPU'; };
    es.onerror = () => { st.textContent = 'Reconectando...'; };
    es.addEventListener('state', ev => {
      const p = JSON.parse(ev.data);
      const voice = p.UserMicActive ? 'Mic ON' : (p.CoachVoiceActive ? 'Voz Coach' : 'En línea');
      st.textContent = voice;
      hud.textContent = '';
      if (!p.Entries || p.Entries.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = 'Silencio táctico — observando partida';
        hud.appendChild(empty);
        return;
      }
      for (const e of p.Entries) {
        const card = document.createElement('div');
        card.className = 'pill ' + (e.Urgency || '');
        const meta = document.createElement('div');
        meta.className = 'meta';
        const left = document.createElement('span');
        left.textContent = (e.GameTitle || 'HUDMind') + ' · ' + (e.Speaker || 'Coach');
        const right = document.createElement('span');
        right.textContent = (e.Urgency || 'Normal').toUpperCase();
        meta.appendChild(left);
        meta.appendChild(right);
        const txt = document.createElement('div');
        txt.className = 'txt';
        txt.textContent = e.Text || '';
        card.appendChild(meta);
        card.appendChild(txt);
        hud.appendChild(card);
      }
    });
  </script>
</body>
</html>
""";
}
