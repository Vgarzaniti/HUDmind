using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Windows;

namespace HUDMind.App;

/// <summary>
/// Application-layer coordinator that owns the active polymorphic <see cref="ICoachEngine"/>,
/// Valve GSI loopback listener, Win32 global hotkeys, and full-match post-game review snapshots.
/// </summary>
public sealed class CoachEngineCoordinator : IAsyncDisposable
{
    private readonly Win32GlobalHotkeyManager _hotkeys = new();
    private readonly LoopbackGsiService _gsiService = new();
    private readonly WasapiAudioSceneMonitor _audioMonitor = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _hotkeySync = new();

    private sealed record LastMatchRecord(string MatchId, string GameId, string GameName, string Digest, int Count);

    private volatile ICoachEngine? _activeEngine;
    private volatile LastMatchRecord _lastMatch = new(string.Empty, string.Empty, string.Empty, string.Empty, 0);
    private int _disposed;

    public ICoachEngine? ActiveEngine => _activeEngine;
    public bool IsRunning => _activeEngine is { IsRunning: true };
    public bool IsPaused => _activeEngine is { IsPaused: true };
    public bool SupportsLiveMic => _activeEngine is { SupportsLiveMic: true };
    public bool HasMatchHistory(bool isEn = false) =>
        !string.IsNullOrWhiteSpace(_lastMatch.Digest) || (_activeEngine?.GetMatchSessionDigest(isEn).Count > 0);

    public async Task<(bool Started, bool FallbackToClassic, string Badge, string Status)> StartAsync(
        UserConfig config,
        IReadOnlyList<ProviderAccount> accounts,
        List<GameProfile> profiles,
        IVoiceOutputService sharedVoice,
        OverlayWindow overlay,
        bool testMode,
        string? forcedGameId,
        bool wantLive,
        Action<string> onLog,
        Action<CoachRuntimeStats> onStats,
        Action<bool> onPauseChanged,
        Action onMicHotkey,
        Action<MatchSessionSnapshot>? onMatchCompleted = null,
        Func<GameProfile, AssembledCoachContext>? resolveContextForProfile = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return (false, false, string.Empty, string.Empty);
        try
        {
            await _lifecycleLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return (false, false, string.Empty, string.Empty);
        }
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || IsRunning)
                return (false, false, string.Empty, string.Empty);

            _lastMatch = new LastMatchRecord(string.Empty, string.Empty, string.Empty, string.Empty, 0);

            Action<MatchSessionSnapshot>? wrappedMatchCompleted = snap =>
            {
                if (snap.InterventionsCount > 0 && !string.IsNullOrWhiteSpace(snap.Digest))
                {
                    _lastMatch = new LastMatchRecord(
                        snap.MatchId, snap.GameId, snap.DisplayName, snap.Digest, snap.InterventionsCount);
                }
                onMatchCompleted?.Invoke(snap);
            };

            if (_gsiService.TryStart())
                onLog($"📡 Receptor Valve GSI oficial activo (127.0.0.1:{LoopbackGsiService.DefaultPort}) para CS2 / Dota 2.");

            var liveBackend = wantLive ? LiveBackendFactory.TryCreateFromAccounts(accounts) : null;
            bool fallbackToClassic = wantLive && liveBackend is null;

            ICoachEngine engine;
            if (liveBackend is not null)
            {
                try
                {
                    var sensor = new TextOnlySensorService(_gsiService);
                    if (!sensor.HasOcrEngine)
                        onLog("ℹ️ Windows OCR sin packs de idioma instalados (filtro local de menús por OCR desactivado).");

                    int prevPttHeld = 0;
                    engine = new RealtimeLiveCoachOrchestrator(
                        liveBackend,
                        config,
                        profiles,
                        new Win32WindowDetector(),
                        new FastScreenCapture(),
                        new Win32StreamingPcmPlayer(),
                        new Win32StreamingMicCapture(),
                        overlay,
                        testMode: testMode,
                        forcedGameId: forcedGameId,
                        onLog: onLog,
                        onStats: onStats,
                        onPauseChanged: onPauseChanged,
                        localPhaseGate: sensor,
                        isPttKeyPressed: () =>
                        {
                            bool held = Win32KeyStateProbe.IsPushToTalkKeyDown(config.PushToTalkKey);
                            int prev = Interlocked.Exchange(ref prevPttHeld, held ? 1 : 0);
                            if (held && prev == 0 && config.IsPushToTalkMode && config.EnableAudioCues && !config.IsTextOnlyMode)
                                Win32AudioCue.Play(AudioCueKind.SoftOn);
                            return held;
                        },
                        onMatchCompleted: wrappedMatchCompleted,
                        resolveContextForProfile: resolveContextForProfile,
                        storyboardComposer: new Win32TemporalStoryboardComposer(),
                        audioMonitor: _audioMonitor
                    );
                }
                catch
                {
                    try { await liveBackend.DisposeAsync(); } catch { }
                    throw;
                }
            }
            else
            {
                if (fallbackToClassic)
                    onLog("ℹ️ Tu cuenta prioritaria usa HTTP REST Multi-IA (sin WebSocket Live) — cambiando UI a Turnos Multi-IA.");

                var textOnlySensor = new TextOnlySensorService(_gsiService);
                if (accounts.Any(a => a.IsReady() && !a.SupportsVision) && !textOnlySensor.HasOcrEngine)
                    onLog("⚠ Tu cuenta prioritaria es ciego-texto pero falta el pack OCR de Windows (solo usarán logs si existen).");

                var router = new ProviderFailoverRouter(accounts, onLog: onLog, textOnlySensor: textOnlySensor);
                engine = new CoachOrchestrator(
                    config,
                    profiles,
                    router,
                    new Win32WindowDetector(),
                    new FastScreenCapture(),
                    sharedVoice,
                    overlay,
                    testMode: testMode,
                    forcedGameId: forcedGameId,
                    onLog: onLog,
                    onStats: onStats,
                    onPauseChanged: onPauseChanged,
                    disposeVoice: false,
                    telemetrySensor: textOnlySensor,
                    onMatchCompleted: wrappedMatchCompleted,
                    resolveContextForProfile: resolveContextForProfile,
                    storyboardComposer: new Win32TemporalStoryboardComposer(),
                    audioMonitor: _audioMonitor
                );
            }

            _activeEngine = engine;
            try
            {
                await engine.StartAsync();
            }
            catch
            {
                _activeEngine = null;
                try { await engine.DisposeAsync(); } catch { }
                throw;
            }

            RegisterHotkeys(config, onMicHotkey, onLog);
            return (true, fallbackToClassic, engine.EngineBadge, engine.ActiveChannelStatus);
        }
        finally
        {
            try { _lifecycleLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public void TogglePause() => _activeEngine?.TogglePause();
    public void TriggerOnDemandAnalysis() => _activeEngine?.TriggerOnDemandAnalysis();
    public void SetCommunicationMode(string mode) => _activeEngine?.SetCommunicationMode(mode);
    public Task<bool> SendUserTextAsync(string text, CancellationToken ct = default) =>
        _activeEngine?.SendUserTextAsync(text, ct) ?? Task.FromResult(false);
    public void RefreshPersona(string? reason = null) => _activeEngine?.RefreshPersona(reason);
    public void ApplyLiveSettings(UserConfig config) => _activeEngine?.ApplyLiveSettings(config);

    public void RegisterHotkeys(UserConfig config, Action onMicHotkey, Action<string> onLog)
    {
        bool pOk = true, aOk = true, mOk = true;
        bool registered = false;
        lock (_hotkeySync)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _hotkeys.Stop();
            var engine = _activeEngine;
            if (!config.EnableHotkeys || engine is not { IsRunning: true })
                return;

            (pOk, aOk, mOk) = _hotkeys.Start(
                config.HotkeyTogglePause,
                config.HotkeyAnalyzeNow,
                config.HotkeyToggleMic,
                () => _activeEngine?.TogglePause(),
                () => _activeEngine?.TriggerOnDemandAnalysis(),
                onToggleMic: onMicHotkey,
                onWarning: onLog
            );
            registered = true;
        }
        if (registered && (!pOk || !aOk || !mOk))
            onLog($"ℹ️ Atajos activos — Pausa:{(pOk ? "OK" : "OCUPADA")} | Analizar:{(aOk ? "OK" : "OCUPADA")} | Mic:{(mOk ? "OK" : "OCUPADA")}");
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            await StopCoreAsync();
        }
        catch (ObjectDisposedException) { }
    }

    private async Task StopCoreAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            lock (_hotkeySync)
                _hotkeys.Stop();
            SnapshotLastMatchSession();
            var engine = _activeEngine;
            if (engine is not null)
            {
                try { await engine.DisposeAsync(); }
                finally { _activeEngine = null; }
            }
        }
        finally
        {
            try { _lifecycleLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public (string GameName, string Digest, int Count) SnapshotLastMatchSession(bool isEn = false)
    {
        var engine = _activeEngine;
        if (engine is not null)
        {
            var snap = engine.CaptureMatchSnapshot(isEn);
            if (snap is not null && snap.InterventionsCount > 0 && !string.IsNullOrWhiteSpace(snap.Digest))
            {
                _lastMatch = new LastMatchRecord(
                    snap.MatchId, snap.GameId, snap.DisplayName, snap.Digest, snap.InterventionsCount);
            }
            else
            {
                var (g, d, count) = engine.GetMatchSessionDigest(isEn);
                if (count > 0 && !string.IsNullOrWhiteSpace(d))
                {
                    var prev = _lastMatch;
                    _lastMatch = new LastMatchRecord(prev.MatchId, prev.GameId, g, d, count);
                }
            }
        }
        var latest = _lastMatch;
        return (latest.GameName, latest.Digest, latest.Count);
    }

    public (string MatchId, string GameId, string GameName, int Count) GetLastMatchMeta()
    {
        var latest = _lastMatch;
        return (latest.MatchId, latest.GameId, latest.GameName, latest.Count);
    }

    public (int InstalledCount, string Message) InstallValveGsiConfigs()
    {
        var res = LoopbackGsiService.InstallValveGsiConfigs();
        if (res.InstalledCount > 0)
            _gsiService.TryStart();
        return res;
    }

    public async Task<(string? Review, double LatencyMs, string ProviderModel)> GeneratePostMatchReviewAsync(
        IReadOnlyList<ProviderAccount> accounts,
        UserConfig config,
        Action<string> onLog,
        CancellationToken ct = default,
        GameProfile? profile = null)
    {
        bool isEn = CoachingPersonaBuilder.IsEnglish(config, preferTextLang: true);
        var (gameName, digest, _) = SnapshotLastMatchSession(isEn);
        if (string.IsNullOrWhiteSpace(digest))
            return (null, 0, "empty");

        await using var router = new ProviderFailoverRouter(accounts, onLog: onLog);
        return await router.GenerateMatchReviewAsync(gameName, digest, config, ct, profile);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopCoreAsync();
        _hotkeys.Dispose();
        _gsiService.Dispose();
        _audioMonitor.Dispose();
        _lifecycleLock.Dispose();
    }
}
