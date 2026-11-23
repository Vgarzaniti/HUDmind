using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HUDMind.Core.AI;
using Microsoft.Win32;

namespace HUDMind.Windows;

/// <summary>
/// Unprivileged, 100% Anti-Cheat safe Valve Game State Integration (GSI) loopback HTTP receiver on 127.0.0.1.
/// Supports Counter-Strike 2 (AppID 730) and Dota 2 (AppID 570) official JSON POST telemetry in ~0ms.
/// Uses raw TcpListener (no Windows HttpListener/urlacl admin permission needed).
/// </summary>
public sealed partial class LoopbackGsiService : IDisposable
{
    public const int DefaultPort = 47315;
    private const int MaxHeaderBytes = 8192;
    private const int MaxBodyBytes = 65536;
    private static readonly TimeSpan StaleAge = TimeSpan.FromSeconds(15);

    private readonly int _port;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _connSlots = new(4, 4);
    private readonly ConcurrentDictionary<long, Task> _activeClients = new();
    private long _clientSeq;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    private string _latestCs2Summary = string.Empty;
    private DateTimeOffset _latestCs2Utc = DateTimeOffset.MinValue;
    private DateTimeOffset _cs2BombPlantedUtc = DateTimeOffset.MinValue;
    private string _latestDotaSummary = string.Empty;
    private DateTimeOffset _latestDotaUtc = DateTimeOffset.MinValue;

    public int Port => _port;
    public bool IsListening { get; private set; }

    public LoopbackGsiService(int port = DefaultPort)
    {
        _port = Math.Clamp(port, 1024, 65535);
    }

    public bool TryStart()
    {
        lock (_stateLock)
        {
            if (_disposed || IsListening)
                return IsListening;

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, _port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                listener.Start(8);

                var cts = new CancellationTokenSource();
                _listener = listener;
                _cts = cts;
                IsListening = true;
                _acceptTask = Task.Run(() => AcceptLoopAsync(listener, cts.Token), cts.Token);
                return true;
            }
            catch
            {
                IsListening = false;
                return false;
            }
        }
    }

    public string GetRecentSummary(string gameKind)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_stateLock)
        {
            if (string.Equals(gameKind, "cs2", StringComparison.OrdinalIgnoreCase))
                return (now - _latestCs2Utc) <= StaleAge ? _latestCs2Summary : string.Empty;
            if (string.Equals(gameKind, "dota2", StringComparison.OrdinalIgnoreCase))
                return (now - _latestDotaUtc) <= StaleAge ? _latestDotaSummary : string.Empty;
            return string.Empty;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        TcpListener? listener;
        Task? acceptTask;

        lock (_stateLock)
        {
            IsListening = false;
            cts = _cts;
            listener = _listener;
            acceptTask = _acceptTask;
            _cts = null;
            _listener = null;
            _acceptTask = null;
        }

        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { acceptTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        try
        {
            var runningClients = _activeClients.Values.ToArray();
            if (runningClients.Length > 0)
                Task.WhenAll(runningClients).Wait(TimeSpan.FromMilliseconds(700));
        }
        catch { }
        cts?.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(100, ct).ConfigureAwait(false);
                continue;
            }

            if (!await _connSlots.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                client.Dispose();
                continue;
            }

            long cid = Interlocked.Increment(ref _clientSeq);
            var cTask = HandleClientAsync(cid, client, ct);
            _activeClients[cid] = cTask;
        }
    }

    private async Task HandleClientAsync(long clientId, TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 1000;
                client.SendTimeout = 1000;
                await using var stream = client.GetStream();

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(1200);

                var (status, jsonBody) = await ReadHttpJsonBodyAsync(stream, readCts.Token).ConfigureAwait(false);
                if (status == 200 && !string.IsNullOrWhiteSpace(jsonBody))
                {
                    var (kind, summary, bombPlanted) = ParseGsiPayloadWithMeta(jsonBody);
                    if (summary == "__CLEAR__")
                    {
                        lock (_stateLock)
                        {
                            if (kind == "cs2")
                            {
                                _latestCs2Summary = string.Empty;
                                _latestCs2Utc = DateTimeOffset.MinValue;
                                _cs2BombPlantedUtc = DateTimeOffset.MinValue;
                            }
                            else if (kind == "dota2")
                            {
                                _latestDotaSummary = string.Empty;
                                _latestDotaUtc = DateTimeOffset.MinValue;
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(summary))
                    {
                        var now = DateTimeOffset.UtcNow;
                        lock (_stateLock)
                        {
                            if (kind == "cs2")
                            {
                                if (bombPlanted)
                                {
                                    if (_cs2BombPlantedUtc == DateTimeOffset.MinValue)
                                        _cs2BombPlantedUtc = now;
                                    int elapsed = (int)Math.Floor((now - _cs2BombPlantedUtc).TotalSeconds);
                                    int rem = Math.Clamp(40 - elapsed, 0, 40);
                                    summary = summary.Replace("C4=planted", $"C4=planted(~{rem}s)", StringComparison.Ordinal);
                                }
                                else
                                {
                                    _cs2BombPlantedUtc = DateTimeOffset.MinValue;
                                }
                                _latestCs2Summary = summary;
                                _latestCs2Utc = now;
                            }
                            else if (kind == "dota2")
                            {
                                _latestDotaSummary = summary;
                                _latestDotaUtc = now;
                            }
                        }
                    }
                }

                string statusLine = status switch
                {
                    200 => "HTTP/1.1 200 OK\r\n",
                    413 => "HTTP/1.1 413 Payload Too Large\r\n",
                    _ => "HTTP/1.1 400 Bad Request\r\n"
                };
                var respBytes = Encoding.ASCII.GetBytes(statusLine + "Content-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(respBytes, readCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Resilient per-connection isolation
        }
        finally
        {
            _activeClients.TryRemove(clientId, out _);
            try { _connSlots.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private static async Task<(int StatusCode, string Body)> ReadHttpJsonBodyAsync(NetworkStream stream, CancellationToken ct)
    {
        var raw = new MemoryStream(2048);
        var buf = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int headerEnd = -1;
            while (raw.Length < MaxHeaderBytes)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, MaxHeaderBytes - (int)raw.Length)), ct).ConfigureAwait(false);
                if (n <= 0) break;
                raw.Write(buf, 0, n);

                var span = raw.GetBuffer().AsSpan(0, (int)raw.Length);
                int idx = span.IndexOf("\r\n\r\n"u8);
                if (idx >= 0)
                {
                    headerEnd = idx + 4;
                    break;
                }
            }

            if (headerEnd < 0)
                return (400, string.Empty);

            var headerText = Encoding.ASCII.GetString(raw.GetBuffer(), 0, headerEnd);
            if (!headerText.StartsWith("POST ", StringComparison.OrdinalIgnoreCase))
                return (400, string.Empty);

            int contentLength = -1;
            foreach (var line in headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    return (400, string.Empty);
                }
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line["Content-Length:".Length..].Trim(), out int parsedLen))
                {
                    contentLength = parsedLen;
                }
            }

            if (contentLength <= 0)
                return (400, string.Empty);
            if (contentLength > MaxBodyBytes)
                return (413, string.Empty);

            int alreadyBody = (int)raw.Length - headerEnd;
            if (alreadyBody >= contentLength)
                return (200, Encoding.UTF8.GetString(raw.GetBuffer(), headerEnd, contentLength));

            var bodyBuf = new byte[contentLength];
            if (alreadyBody > 0)
                Buffer.BlockCopy(raw.GetBuffer(), headerEnd, bodyBuf, 0, alreadyBody);

            int offset = alreadyBody;
            while (offset < contentLength)
            {
                int n = await stream.ReadAsync(bodyBuf.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
                if (n <= 0) break;
                offset += n;
            }

            return offset == contentLength
                ? (200, Encoding.UTF8.GetString(bodyBuf, 0, contentLength))
                : (400, string.Empty);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static (string Kind, string Summary) ParseGsiPayload(string json)
    {
        var (kind, summary, _) = ParseGsiPayloadWithMeta(json);
        return (kind, summary);
    }

    private static (string Kind, string Summary, bool BombPlanted) ParseGsiPayloadWithMeta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("provider", out var prov) ||
                !prov.TryGetProperty("appid", out var appIdEl))
            {
                return (string.Empty, string.Empty, false);
            }

            int appId = appIdEl.ValueKind == JsonValueKind.Number ? appIdEl.GetInt32() : 0;
            if (appId == 730)
                return ParseCs2Gsi(root);
            if (appId == 570)
                return ("dota2", ParseDota2Gsi(root), false);
        }
        catch
        {
            // Ignore malformed telemetry JSON
        }
        return (string.Empty, string.Empty, false);
    }

    private static (string Kind, string Summary, bool BombPlanted) ParseCs2Gsi(JsonElement root)
    {
        // Clear stale match telemetry immediately when player exits to CS2 main menu / lobby!
        if (root.TryGetProperty("player", out var playerCheck) &&
            playerCheck.TryGetProperty("activity", out var actEl) &&
            string.Equals(actEl.GetString(), "menu", StringComparison.OrdinalIgnoreCase))
        {
            return ("cs2", "__CLEAR__", false);
        }
        if (!root.TryGetProperty("map", out _))
            return ("cs2", "__CLEAR__", false);

        var sb = new StringBuilder(192);
        sb.Append("CS2_GSI:");
        bool bombPlanted = false;

        if (root.TryGetProperty("map", out var map))
        {
            if (map.TryGetProperty("name", out var mn) && mn.GetString() is string mapName && !string.IsNullOrWhiteSpace(mapName))
                sb.Append(' ').Append(CoachingPersonaBuilder.SanitizeField(mapName, 24));
            if (map.TryGetProperty("phase", out var mp) && mp.GetString() is string phase && !string.IsNullOrWhiteSpace(phase))
                sb.Append(" phase=").Append(CoachingPersonaBuilder.SanitizeField(phase, 16));
            if (map.TryGetProperty("team_ct", out var ctEl) && ctEl.TryGetProperty("score", out var ctSc) &&
                map.TryGetProperty("team_t", out var tEl) && tEl.TryGetProperty("score", out var tSc))
            {
                sb.Append($" score={ctSc.GetInt32()}:{tSc.GetInt32()}");
            }
        }

        if (root.TryGetProperty("round", out var round))
        {
            if (round.TryGetProperty("phase", out var rp) && rp.GetString() is string rPhase && !string.IsNullOrWhiteSpace(rPhase))
                sb.Append(" round=").Append(CoachingPersonaBuilder.SanitizeField(rPhase, 16));
            if (round.TryGetProperty("bomb", out var bomb) && bomb.GetString() is string bombState && !string.IsNullOrWhiteSpace(bombState))
            {
                bombPlanted = string.Equals(bombState, "planted", StringComparison.OrdinalIgnoreCase);
                sb.Append(" C4=").Append(CoachingPersonaBuilder.SanitizeField(bombState, 12));
            }
        }

        if (root.TryGetProperty("player", out var player))
        {
            if (player.TryGetProperty("team", out var pt) && pt.GetString() is string team && !string.IsNullOrWhiteSpace(team))
                sb.Append(" side=").Append(CoachingPersonaBuilder.SanitizeField(team, 8));

            if (player.TryGetProperty("state", out var st))
            {
                int hp = st.TryGetProperty("health", out var h) ? h.GetInt32() : 0;
                int armor = st.TryGetProperty("armor", out var a) ? a.GetInt32() : 0;
                int money = st.TryGetProperty("money", out var m) ? m.GetInt32() : 0;
                sb.Append($" HP={hp} Armor={armor}");
                if (st.TryGetProperty("helmet", out var hm) && hm.ValueKind == JsonValueKind.True)
                    sb.Append(" Helmet=1");
                if (st.TryGetProperty("defusekit", out var dk) && dk.ValueKind == JsonValueKind.True)
                    sb.Append(" Kit=1");
                sb.Append($" ${money}");
            }

            if (player.TryGetProperty("weapons", out var weapons) && weapons.ValueKind == JsonValueKind.Object)
            {
                foreach (var w in weapons.EnumerateObject())
                {
                    if (w.Value.TryGetProperty("state", out var ws) &&
                        string.Equals(ws.GetString(), "active", StringComparison.OrdinalIgnoreCase) &&
                        w.Value.TryGetProperty("name", out var wn))
                    {
                        var wName = CoachingPersonaBuilder.SanitizeField(wn.GetString(), 24);
                        int ammo = w.Value.TryGetProperty("ammo_clip", out var ac) ? ac.GetInt32() : -1;
                        sb.Append(" wp=").Append(wName);
                        if (ammo >= 0) sb.Append($"({ammo})");
                        break;
                    }
                }
            }
        }

        return ("cs2", sb.Length > 10 ? sb.ToString() : string.Empty, bombPlanted);
    }

    private static string ParseDota2Gsi(JsonElement root)
    {
        var sb = new StringBuilder(160);
        sb.Append("DOTA2_GSI:");

        if (root.TryGetProperty("map", out var map))
        {
            if (map.TryGetProperty("clock_time", out var ct) && ct.ValueKind == JsonValueKind.Number)
            {
                int sec = ct.GetInt32();
                int abs = Math.Abs(sec);
                sb.Append($" {(sec < 0 ? "-" : "")}{abs / 60}:{abs % 60:D2}");
            }
            if (map.TryGetProperty("game_state", out var gs) && gs.GetString() is string state && !string.IsNullOrWhiteSpace(state))
            {
                var shortState = state.Replace("DOTA_GAMERULES_STATE_", string.Empty, StringComparison.Ordinal);
                sb.Append(' ').Append(CoachingPersonaBuilder.SanitizeField(shortState, 32));
            }
        }

        if (root.TryGetProperty("hero", out var hero))
        {
            if (hero.TryGetProperty("name", out var hn) && hn.GetString() is string hName && !string.IsNullOrWhiteSpace(hName))
            {
                var cleanHero = hName.Replace("npc_dota_hero_", string.Empty, StringComparison.Ordinal);
                sb.Append(' ').Append(CoachingPersonaBuilder.SanitizeField(cleanHero, 24));
            }
            if (hero.TryGetProperty("level", out var lv))
                sb.Append($" Lv{lv.GetInt32()}");
            if (hero.TryGetProperty("health_percent", out var hp) && hero.TryGetProperty("mana_percent", out var mp))
                sb.Append($" HP={hp.GetInt32()}% MP={mp.GetInt32()}%");
            if (hero.TryGetProperty("alive", out var al) && al.ValueKind == JsonValueKind.False)
                sb.Append(" DEAD");
        }

        if (root.TryGetProperty("player", out var player))
        {
            if (player.TryGetProperty("gold", out var g))
                sb.Append($" Gold={g.GetInt32()}");
            if (player.TryGetProperty("kills", out var k) && player.TryGetProperty("deaths", out var d) && player.TryGetProperty("assists", out var a))
                sb.Append($" KDA={k.GetInt32()}/{d.GetInt32()}/{a.GetInt32()}");
        }

        return sb.Length > 12 ? sb.ToString() : string.Empty;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VdfPathRegex();

    /// <summary>
    /// Installs standard official Valve GSI config file (`gamestate_integration_hudmind.cfg`)
    /// into detected Steam CS2 and/or Dota 2 cfg folders when explicitly requested by the user.
    /// </summary>
    public static (int InstalledCount, string Message) InstallValveGsiConfigs(int port = DefaultPort)
    {
        var steamRoots = DiscoverSteamLibraryRoots();
        var targets = new List<(string Label, string CfgDir)>
        {
            ("CS2", Path.Combine("steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "cfg")),
            ("Dota 2", Path.Combine("steamapps", "common", "dota 2 beta", "game", "dota", "cfg"))
        };

        int installed = 0;
        var names = new List<string>();
        string? writeErr = null;
        string cfgBody = BuildValveGsiCfg(port);

        foreach (var root in steamRoots)
        {
            foreach (var (label, relCfg) in targets)
            {
                try
                {
                    var fullCfgDir = Path.GetFullPath(Path.Combine(root, relCfg));
                    if (!Directory.Exists(fullCfgDir) || HasReparsePointInChain(root, fullCfgDir))
                        continue;

                    var dest = Path.GetFullPath(Path.Combine(fullCfgDir, "gamestate_integration_hudmind.cfg"));
                    if (!dest.StartsWith(fullCfgDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (File.Exists(dest) && (File.GetAttributes(dest) & FileAttributes.ReparsePoint) != 0)
                        continue;

                    File.WriteAllText(dest, cfgBody, new UTF8Encoding(false));
                    installed++;
                    if (!names.Contains(label)) names.Add(label);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    writeErr = $"{label}: {ex.Message}";
                }
            }
        }

        if (installed > 0)
            return (installed, $"GSI oficial instalado para {string.Join(" + ", names)} en http://127.0.0.1:{port} (reiniciá el juego si ya estaba abierto).");
        if (!string.IsNullOrEmpty(writeErr))
            return (0, $"Permiso denegado al escribir config GSI en Steam ({writeErr}). Ejecutá como Administrador o creá el .cfg manualmente.");
        return (0, "No se encontraron carpetas instaladas de CS2 ni Dota 2 en Steam.");
    }

    public static string BuildValveGsiCfg(int port = DefaultPort) => $$"""
"HUDMind Real-Time Coach GSI v1.0"
{
    "uri" "http://127.0.0.1:{{port}}/"
    "timeout" "5.0"
    "buffer" "0.1"
    "throttle" "0.2"
    "heartbeat" "5.0"
    "data"
    {
        "provider" "1"
        "map" "1"
        "round" "1"
        "player_id" "1"
        "player_state" "1"
        "player_weapons" "1"
        "hero" "1"
        "abilities" "0"
        "items" "0"
    }
}
""";

    private static bool HasReparsePointInChain(string rootPath, string leafDir)
    {
        try
        {
            var rootFull = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
            var cur = Path.GetFullPath(leafDir).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(cur, rootFull, StringComparison.OrdinalIgnoreCase) &&
                !cur.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;

            while (!string.IsNullOrEmpty(cur))
            {
                if (Directory.Exists(cur) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
                    return true;
                if (string.Equals(cur, rootFull, StringComparison.OrdinalIgnoreCase))
                    break;
                cur = Path.GetDirectoryName(cur)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    internal static bool IsSafeLocalDirectoryPath(string? rawPath, out string fullLocalPath)
    {
        fullLocalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
            return false;
        var trimmed = rawPath.Trim();
        // Reject UNC / network shares before calling Directory.Exists to prevent NTLM credential leaks!
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(trimmed))
        {
            return false;
        }
        try
        {
            var full = Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar);
            if (Uri.TryCreate(full, UriKind.Absolute, out var uri) && uri.IsUnc)
                return false;
            if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return false;
            fullLocalPath = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> DiscoverSteamLibraryRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedPaths = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        };

        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string hkcuSteam)
                seedPaths.Add(hkcuSteam.Replace('/', Path.DirectorySeparatorChar));
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string hklmSteam)
                seedPaths.Add(hklmSteam);
        }
        catch { }

        foreach (var seed in seedPaths)
        {
            if (!IsSafeLocalDirectoryPath(seed, out var safeSteamRoot))
                continue;
            roots.Add(safeSteamRoot);

            var vdfPath = Path.Combine(safeSteamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath) || (File.GetAttributes(vdfPath) & FileAttributes.ReparsePoint) != 0)
                continue;
            try
            {
                var txt = File.ReadAllText(vdfPath, Encoding.UTF8);
                foreach (Match m in VdfPathRegex().Matches(txt))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (IsSafeLocalDirectoryPath(p, out var safeLib))
                        roots.Add(safeLib);
                }
            }
            catch { }
        }
        return [.. roots];
    }

    public void Dispose()
    {
        lock (_stateLock) _disposed = true;
        Stop();
        _connSlots.Dispose();
    }
}
