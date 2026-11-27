using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Overlay;

namespace HUDMind.Windows.Overlay;

/// <summary>
/// Safe local configuration &amp; allowlist setup helper for external loopback RCON overlay bridges
/// installed under %APPDATA% (validates ReparsePoint/symlink safety before reading or writing).
/// </summary>
public static class InGameOverlayBridgeSetup
{
    public static string? ResolveCompatibleBridgeDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            return null;

        // Probe known compatible local overlay bridge paths without loading/injecting external binaries
        string[] knownSubPaths = [Path.Combine("bakkesmod", "bakkesmod")];
        foreach (var sub in knownSubPaths)
        {
            var candidate = Path.Combine(appData, sub);
            if (LoopbackGsiService.IsSafeLocalDirectoryPath(candidate, out var safe) && Directory.Exists(safe))
                return safe;
        }
        return null;
    }

    public static LoopbackRconBridgeSettings? TryLoadBridgeSettings(string? overrideBridgeDir = null)
    {
        var dir = string.IsNullOrWhiteSpace(overrideBridgeDir)
            ? ResolveCompatibleBridgeDirectory()
            : (LoopbackGsiService.IsSafeLocalDirectoryPath(overrideBridgeDir, out var safeDir) ? safeDir : null);
        if (dir is null)
            return null;

        var cfgDir = Path.Combine(dir, "cfg");
        if (!LoopbackGsiService.IsSafeLocalDirectoryPath(cfgDir, out var safeCfgDir))
            return null;

        var cfgPath = Path.Combine(safeCfgDir, "config.cfg");
        if (!File.Exists(cfgPath))
            return null;

        var info = new FileInfo(cfgPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return null;

        var content = File.ReadAllText(cfgPath, Encoding.UTF8);
        return InGameOverlayBridgeProtocol.ParseRconBridgeConfig(content);
    }

    public static async Task<(bool Configured, bool LiveVerified, string Message)> ConfigureAndVerifyAsync(CancellationToken ct = default)
    {
        var dir = ResolveCompatibleBridgeDirectory();
        if (dir is null)
            return (false, false, "No se detectó ningún puente HUD compatible instalado en %APPDATA%.");

        return await ConfigureDirectoryAsync(dir, attemptLiveVerify: true, ct).ConfigureAwait(false);
    }

    public static async Task<(bool Configured, bool LiveVerified, string Message)> ConfigureDirectoryAsync(
        string bridgeDir,
        bool attemptLiveVerify = false,
        CancellationToken ct = default)
    {
        if (!LoopbackGsiService.IsSafeLocalDirectoryPath(bridgeDir, out var safeRoot) || !Directory.Exists(safeRoot))
            return (false, false, "Ruta del puente HUD inválida o rechazada por seguridad.");

        if (!EnsureSafeSubdirectory(safeRoot, "data", out var safeDataDir))
            return (false, false, "Ruta del puente HUD rechazada por seguridad (enlace simbólico).");

        var allowPath = Path.Combine(safeDataDir, "rcon_commands.cfg");
        if (File.Exists(allowPath) && (new FileInfo(allowPath).Attributes & FileAttributes.ReparsePoint) != 0)
            return (false, false, "El archivo de permisos locales es un enlace simbólico (bloqueado por seguridad).");

        string existingAllow = File.Exists(allowPath)
            ? await File.ReadAllTextAsync(allowPath, Encoding.UTF8, ct).ConfigureAwait(false)
            : string.Empty;
        var (updatedAllow, addedAllow) = InGameOverlayBridgeProtocol.EnsureCommandAllowlisted(existingAllow, "toast");
        if (addedAllow || !File.Exists(allowPath))
            await File.WriteAllTextAsync(allowPath, updatedAllow, new UTF8Encoding(false), ct).ConfigureAwait(false);

        if (!EnsureSafeSubdirectory(safeRoot, "cfg", out var safeCfgDir))
            return (false, false, "Carpeta cfg del puente HUD rechazada por seguridad (enlace simbólico).");

        var cfgPath = Path.Combine(safeCfgDir, "config.cfg");
        if (File.Exists(cfgPath) && (new FileInfo(cfgPath).Attributes & FileAttributes.ReparsePoint) != 0)
            return (false, false, "El archivo config.cfg local es un enlace simbólico (bloqueado por seguridad).");

        string existingCfg = File.Exists(cfgPath)
            ? await File.ReadAllTextAsync(cfgPath, Encoding.UTF8, ct).ConfigureAwait(false)
            : string.Empty;
        string fallbackSecret = GenerateRandomToken();
        var (updatedCfg, cfgChanged, _) = InGameOverlayBridgeProtocol.EnsureRconConfigEnabled(existingCfg, fallbackSecret);
        if (cfgChanged)
            await File.WriteAllTextAsync(cfgPath, updatedCfg, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var settings = TryLoadBridgeSettings(safeRoot);
        if (settings is null || !settings.Enabled)
        {
            return (true, false,
                "Permiso 'toast' y rcon_enabled aplicados en configuración local. Abrí o reiniciá el juego para activarlo.");
        }

        bool liveOk = attemptLiveVerify &&
            await LoopbackRconToastBridge.TrySendSetupVerificationAsync(settings, ct).ConfigureAwait(false);

        if (liveOk)
            return (true, true, $"Puente HUD local listo (127.0.0.1:{settings.Port}) — aviso en pantalla completa verificado.");

        string restartNote = cfgChanged ? " Si el juego ya estaba abierto, reinicialo una vez para aplicar rcon_enabled." : string.Empty;
        return (true, false,
            $"Puente HUD configurado en puerto local {settings.Port} (config.cfg + rcon_commands.cfg).{restartNote}");
    }

    private static bool EnsureSafeSubdirectory(string safeRoot, string subName, out string safeSubDir)
    {
        safeSubDir = string.Empty;
        var candidate = Path.Combine(safeRoot, subName);
        if (File.Exists(candidate))
            return false;
        if (!Directory.Exists(candidate))
            Directory.CreateDirectory(candidate);
        return LoopbackGsiService.IsSafeLocalDirectoryPath(candidate, out safeSubDir);
    }

    private static string GenerateRandomToken()
    {
        Span<byte> raw = stackalloc byte[8];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToHexString(raw).ToLowerInvariant();
    }
}

/// <summary>
/// Strictly loopback (ws://127.0.0.1) passive RCON WebSocket overlay adapter that forwards finalized
/// coaching callouts to a compatible local in-game toast bridge so callouts stay visible even in Exclusive Fullscreen
/// with zero code injection or memory reads by HUDMind.
/// </summary>
public sealed class LoopbackRconToastBridge : IOverlayOutputService, IAsyncDisposable
{
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isSupportedGameActive;
    private readonly Func<LoopbackRconBridgeSettings?> _settingsResolver;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;

    private long _lastToastSentTick;
    private long _backoffUntilTick;
    private string _lastSentCmd = string.Empty;
    private volatile bool _disposed;

    public LoopbackRconToastBridge(
        Func<bool> isEnabled,
        Func<bool> isSupportedGameActive,
        Func<LoopbackRconBridgeSettings?>? settingsResolver = null)
    {
        _isEnabled = isEnabled;
        _isSupportedGameActive = isSupportedGameActive;
        _settingsResolver = settingsResolver ?? (() => InGameOverlayBridgeSetup.TryLoadBridgeSettings());
        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token), _cts.Token);
    }

    public void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds) =>
        ShowMessage(new OverlayCalloutMessage(text, urgency, title, durationSeconds, OverlayEntryKind.CoachFinal));

    public void ShowMessage(OverlayCalloutMessage message)
    {
        if (_disposed || message.Kind != OverlayEntryKind.CoachFinal || !_isEnabled())
            return;

        if (!_isSupportedGameActive() &&
            !message.Title.Contains("Rocket", StringComparison.OrdinalIgnoreCase))
            return;

        long nowTick = System.Diagnostics.Stopwatch.GetTimestamp();
        long backoff = Interlocked.Read(ref _backoffUntilTick);
        if (backoff > 0 && nowTick < backoff)
            return;

        long lastSent = Interlocked.Read(ref _lastToastSentTick);
        if (lastSent > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastSent).TotalMilliseconds < 1800.0)
            return;

        string cmd = InGameOverlayBridgeProtocol.BuildToastCommand("HUDMind", message.Text);
        if (string.IsNullOrEmpty(cmd))
            return;

        if (string.Equals(cmd, _lastSentCmd, StringComparison.Ordinal) &&
            lastSent > 0 &&
            System.Diagnostics.Stopwatch.GetElapsedTime(lastSent).TotalSeconds < 6.0)
        {
            return;
        }

        _queue.Writer.TryWrite(cmd);
    }

    internal static async Task<bool> TrySendSetupVerificationAsync(LoopbackRconBridgeSettings settings, CancellationToken ct)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.SecretToken))
            return false;

        try
        {
            using var ws = await ConnectAndAuthAsync(settings, ct).ConfigureAwait(false);
            if (ws is null)
                return false;

            await SendTextAsync(ws, "rcon_refresh_allowed", ct).ConfigureAwait(false);
            await SendTextAsync(ws, InGameOverlayBridgeProtocol.BuildToastCommand("HUDMind", "Puente Pantalla Completa conectado"), ct).ConfigureAwait(false);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        ClientWebSocket? ws = null;
        try
        {
            await foreach (var cmd in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!_isEnabled())
                    continue;

                long nowTick = System.Diagnostics.Stopwatch.GetTimestamp();
                long backoff = Interlocked.Read(ref _backoffUntilTick);
                if (backoff > 0 && nowTick < backoff)
                    continue;

                var settings = _settingsResolver();
                if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.SecretToken))
                {
                    Interlocked.Exchange(ref _backoffUntilTick,
                        nowTick + System.Diagnostics.Stopwatch.Frequency * 15);
                    continue;
                }

                try
                {
                    if (ws is null || ws.State != WebSocketState.Open)
                    {
                        ws?.Dispose();
                        ws = await ConnectAndAuthAsync(settings, ct).ConfigureAwait(false);
                    }

                    if (ws is null || ws.State != WebSocketState.Open)
                    {
                        Interlocked.Exchange(ref _backoffUntilTick,
                            System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 12);
                        continue;
                    }

                    await SendTextAsync(ws, cmd, ct).ConfigureAwait(false);
                    Interlocked.Exchange(ref _lastToastSentTick, System.Diagnostics.Stopwatch.GetTimestamp());
                    _lastSentCmd = cmd;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch
                {
                    ws?.Dispose();
                    ws = null;
                    Interlocked.Exchange(ref _backoffUntilTick,
                        System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 12);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            ws?.Dispose();
        }
    }

    private static async Task<ClientWebSocket?> ConnectAndAuthAsync(LoopbackRconBridgeSettings settings, CancellationToken ct)
    {
        int port = Math.Clamp(settings.Port, 1024, 65535);
        var uri = new Uri($"ws://127.0.0.1:{port}/", UriKind.Absolute);
        var ws = new ClientWebSocket();
        try
        {
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connCts.CancelAfter(TimeSpan.FromMilliseconds(1800));
            await ws.ConnectAsync(uri, connCts.Token).ConfigureAwait(false);

            await SendTextAsync(ws, $"rcon_password {settings.SecretToken}", connCts.Token).ConfigureAwait(false);

            var buf = new byte[512];
            var res = await ws.ReceiveAsync(buf, connCts.Token).ConfigureAwait(false);
            string reply = Encoding.UTF8.GetString(buf, 0, res.Count).Trim();
            if (!reply.Contains("authyes", StringComparison.OrdinalIgnoreCase))
            {
                ws.Dispose();
                return null;
            }
            return ws;
        }
        catch
        {
            ws.Dispose();
            return null;
        }
    }

    private static Task SendTextAsync(ClientWebSocket ws, string command, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Text, true, ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        try { await _cts.CancelAsync().ConfigureAwait(false); } catch { }
        try { await _workerTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
