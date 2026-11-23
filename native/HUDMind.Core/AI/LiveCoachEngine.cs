using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;

namespace HUDMind.Core.AI;

/// <summary>
/// Provider-agnostic Real-Time Conversational Coach Engine.
/// Coordinates sharp screen capture, ordered 16kHz live microphone with Acoustic Echo Suppression &amp; barge-in,
/// 24kHz zero-stutter PCM playback, local WinRT OCR silence gate, multi-game profile hot-reload, and live overlay.
/// Works identically with any <see cref="ILiveRealtimeBackend"/> implementation.
/// </summary>
public sealed class RealtimeLiveCoachOrchestrator : ICoachEngine
{
    private readonly ILiveRealtimeBackend _backend;
    private readonly UserConfig _config;
    private readonly List<GameProfile> _profiles;
    private readonly IForegroundWindowDetector _windowDetector;
    private readonly IScreenCaptureService _capture;
    private readonly IStreamingPcmPlayer _pcmPlayer;
    private readonly IStreamingMicCapture _mic;
    private readonly IOverlayOutputService _overlay;
    private readonly ITextOnlySensorService? _localPhaseGate;
    private readonly Func<bool>? _isPttKeyPressed;
    private readonly bool _testMode;
    private readonly string? _forcedGameId;
    private readonly Action<string>? _onLog;
    private readonly Action<CoachRuntimeStats>? _onStats;
    private readonly Action<bool>? _onPauseChanged;
    private readonly Action<MatchSessionSnapshot>? _onMatchCompleted;
    private readonly Func<GameProfile, AssembledCoachContext>? _resolveContextForProfile;

    private readonly InterventionMemory _memory = new(maxItems: 12, ttlSeconds: 75.0);
    private readonly TacticalSignalTracker _tacticalTracker = new();
    private readonly BlackScreenDiagnostic _blackScreenDiagnostic = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _hashLock = new();
    private readonly object _audioStateLock = new();
    private readonly object _micGateLock = new();
    private readonly ConcurrentDictionary<int, Task> _inFlightTasks = new();
    private int _taskSeq;

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _reconnectCts;
    private Task? _sessionTask;
    private Channel<byte[]>? _micSendChannel;
    private Task? _micSendTask;

    private volatile GameProfile? _activeProfile;
    private long _profileEpoch;
    private volatile bool _userPaused;
    private volatile bool _micEnabled;
    private volatile bool _modelSpeaking;
    private volatile bool _gameFocused;
    private volatile bool _bargeInMuted;
    private volatile bool _suppressCurrentTurnAudio;
    private volatile bool _disposed;

    private ulong? _lastSentHash;
    private long _framesSent;
    private long _interventions;
    private double _lastCaptureMs;
    private double _lastTtfaMs;
    private long _lastTurnRequestTick;
    private int _waitingFirstAudioInt;
    private long _lastOverlayPartialTick;
    private long _lastAutoNudgeUtcTicks;
    private long _lastMicVoiceUtcTicks;
    private long _lastPttHeldUtcTicks;

    public bool IsRunning => Volatile.Read(ref _runCts) is { IsCancellationRequested: false };
    public bool IsPaused => _userPaused;
    public bool SupportsLiveMic => true;
    public string EngineBadge => ".NET 9 · 0% GPU · LIVE WS";
    public string ActiveVoiceLabel => _backend.ResolveVoiceName(_config.VoiceGender);
    public string ActiveProviderName => _backend.ProviderName;
    public string ActiveVoiceDisplay => ActiveVoiceLabel;
    public string ActiveGameId => _activeProfile?.Manifest.GameId ?? string.Empty;
    public string ActiveChannelStatus => _config.IsTextOnlyMode
        ? $"💬 {_backend.ProviderName} · Solo texto"
        : _config.IsCoachVoiceOnlyMode
            ? $"🔊 {_backend.ProviderName} · Solo voz ({ActiveVoiceLabel})"
            : $"🎙 {_backend.ProviderName} · Charla ({ActiveVoiceLabel})";

    public void RefreshPersona(string? reason = null) => RefreshLivePersona(reason);

    public Task<bool> SendUserTextAsync(string userText, CancellationToken ct = default) =>
        SendUserTextAsync(userText, attachCurrentScreen: true, ct);

    public void UpdateAudioLive(UserConfig config)
    {
        _config.CopyRuntimePreferencesFrom(config);
        SetVolume(_config.TtsVolume);
        SetAudioDevicesLive(_config.AudioInputDeviceName, _config.AudioOutputDeviceName);
    }

    public void ApplyLiveSettings(UserConfig config)
    {
        UpdateAudioLive(config);
        RefreshLivePersona($"{_config.PlayerSkillTier} · {_config.CoachStyle}");
    }

    public RealtimeLiveCoachOrchestrator(
        ILiveRealtimeBackend backend,
        UserConfig config,
        List<GameProfile> profiles,
        IForegroundWindowDetector windowDetector,
        IScreenCaptureService capture,
        IStreamingPcmPlayer pcmPlayer,
        IStreamingMicCapture mic,
        IOverlayOutputService overlay,
        bool testMode = false,
        string? forcedGameId = null,
        Action<string>? onLog = null,
        Action<CoachRuntimeStats>? onStats = null,
        Action<bool>? onPauseChanged = null,
        ITextOnlySensorService? localPhaseGate = null,
        Func<bool>? isPttKeyPressed = null,
        Action<MatchSessionSnapshot>? onMatchCompleted = null,
        Func<GameProfile, AssembledCoachContext>? resolveContextForProfile = null)
    {
        _backend = backend;
        _config = config;
        _profiles = profiles;
        _windowDetector = windowDetector;
        _capture = capture;
        _pcmPlayer = pcmPlayer;
        _mic = mic;
        _overlay = overlay;
        _localPhaseGate = localPhaseGate;
        _isPttKeyPressed = isPttKeyPressed;
        _testMode = testMode;
        _forcedGameId = forcedGameId;
        _onLog = onLog;
        _onStats = onStats;
        _onPauseChanged = onPauseChanged;
        _onMatchCompleted = onMatchCompleted;
        _resolveContextForProfile = resolveContextForProfile;
        _micEnabled = config.IsMicActive;
    }

    private GameProfile? MatchForegroundProfile(string? fg) =>
        GameProfile.MatchForegroundProfile(_profiles, fg, _forcedGameId);

    public (string GameName, string Digest, int Count) GetMatchSessionDigest(bool isEn = false) =>
        _memory.BuildMatchReviewDigest(isEn: isEn);

    public MatchSessionSnapshot? CaptureMatchSnapshot(bool isEn = false) =>
        _memory.BuildSessionSnapshot(isEn);

    private void FlushMatchSnapshot(bool resetMatchLog)
    {
        bool isEn = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
        var snap = _memory.BuildSessionSnapshot(isEn);
        if (snap is not null && snap.InterventionsCount > 0)
        {
            try { _onMatchCompleted?.Invoke(snap); } catch { }
        }
        if (resetMatchLog)
            _memory.Clear(clearMatchLog: true);
    }

    public void SetAudioDevicesLive(string? inputDeviceName, string? outputDeviceName)
    {
        _config.AudioInputDeviceName = inputDeviceName ?? string.Empty;
        _config.AudioOutputDeviceName = outputDeviceName ?? string.Empty;
        if (_disposed) return;
        _pcmPlayer.SetDevice(_config.AudioOutputDeviceName);
        lock (_micGateLock)
        {
            if (_disposed) return;
            _mic.SetDevice(_config.AudioInputDeviceName);
        }
    }

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed || IsRunning) return;

            _pcmPlayer.SetDevice(_config.AudioOutputDeviceName);
            _pcmPlayer.SetVolume(_config.TtsVolume);
            _mic.SetDevice(_config.AudioInputDeviceName);
            _userPaused = false;
            _config.RuntimeTiltRisk = false;
            _micEnabled = _config.IsMicActive;
            _bargeInMuted = false;
            _suppressCurrentTurnAudio = false;
            _memory.Clear(clearMatchLog: true);
            _tacticalTracker.Reset();
            _blackScreenDiagnostic.Reset();
            Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, 0);
            Interlocked.Exchange(ref _lastPttHeldUtcTicks, 0);
            lock (_hashLock) _lastSentHash = null;

            if (_testMode)
            {
                _activeProfile = _profiles.FirstOrDefault(p => string.Equals(p.Manifest.GameId, _forcedGameId, StringComparison.OrdinalIgnoreCase))
                    ?? _profiles.FirstOrDefault();
                _gameFocused = true;
            }
            else
            {
                _activeProfile = MatchForegroundProfile(_windowDetector.GetForegroundExecutable());
                _gameFocused = _activeProfile is not null;
            }

            if (_activeProfile is not null && _resolveContextForProfile is not null)
                CoachContextAssembler.ApplyToConfig(_config, _resolveContextForProfile(_activeProfile));

            var cts = new CancellationTokenSource();
            Volatile.Write(ref _runCts, cts);
            _sessionTask = Task.Run(() => LifecycleLoopAsync(cts.Token), cts.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private CancellationToken CurrentRunToken()
    {
        var cts = Volatile.Read(ref _runCts);
        if (cts is null) return new CancellationToken(canceled: true);
        try { return cts.Token; }
        catch (ObjectDisposedException) { return new CancellationToken(canceled: true); }
    }

    private void TrackFireAndForget(Func<CancellationToken, Task> work)
    {
        var ct = CurrentRunToken();
        if (ct.IsCancellationRequested) return;
        int id = Interlocked.Increment(ref _taskSeq);
        var task = Task.Run(async () =>
        {
            try { await work(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { _onLog?.Invoke($"Error tarea Live: {SecretRedactor.Redact(ex.Message)}"); }
            finally { _inFlightTasks.TryRemove(id, out _); }
        });
        _inFlightTasks[id] = task;
    }

    public void SetVolume(double volume) => _pcmPlayer.SetVolume(volume);

    public void RefreshLivePersona(string? reason = null)
    {
        var rec = Volatile.Read(ref _reconnectCts);
        if (rec is { IsCancellationRequested: false })
        {
            _onLog?.Invoke($"Actualizando sesión Live ({reason ?? _config.PlayerSkillTier})...");
            _ = _backend.ResetSessionStateAsync();
            try { rec.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void SetVoiceGenderLive(string gender)
    {
        _config.VoiceGender = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
        RefreshLivePersona($"voz {_backend.ResolveVoiceName(_config.VoiceGender)}");
    }

    public void SetMicEnabled(bool enabled)
    {
        _micEnabled = enabled;
        if (!enabled)
        {
            StopMicAndDrainChannel();
            _onLog?.Invoke("🎤 Micrófono silenciado.");
        }
        else if (_backend.IsConnected && !_userPaused)
        {
            StartMicIfEnabled();
        }
    }

    public void SetCommunicationMode(string mode)
    {
        bool wasTextOnly = _config.IsTextOnlyMode;
        _config.SetCommunicationMode(mode);
        _micEnabled = _config.IsMicActive;
        bool nowTextOnly = _config.IsTextOnlyMode;

        lock (_audioStateLock)
        {
            if (nowTextOnly)
            {
                _pcmPlayer.InterruptAndClear();
                _modelSpeaking = false;
                _suppressCurrentTurnAudio = false;
            }
            else if (wasTextOnly && _modelSpeaking)
            {
                _suppressCurrentTurnAudio = true;
            }
        }

        if (nowTextOnly)
        {
            StopMicAndDrainChannel();
            _onLog?.Invoke("💬 Modo SOLO TEXTO activo (micrófono y voz silenciados; usá el chat escrito y overlay).");
        }
        else if (_config.IsCoachVoiceOnlyMode)
        {
            StopMicAndDrainChannel();
            _onLog?.Invoke("🔊 Modo SOLO VOZ DEL COACH activo (micrófono apagado; solo te habla el coach).");
        }
        else
        {
            _onLog?.Invoke("🎙 Modo CHARLA activo (micrófono en vivo + voz del coach).");
            if (_backend.IsConnected && !_userPaused)
                StartMicIfEnabled();
        }
        EmitStats();

        // When crossing text_only <-> voice on backends that negotiate modalities at session setup (e.g. OpenAI), reload cleanly
        if (wasTextOnly != nowTextOnly && _backend.IsConnected)
            RefreshLivePersona($"canal {_config.CommunicationMode}");
    }

    public async Task<bool> SendUserTextAsync(string userText, bool attachCurrentScreen = true, CancellationToken callerCt = default)
    {
        var msg = (userText ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(msg) || _disposed)
            return false;

        if (!_backend.IsConnected)
        {
            _onLog?.Invoke("Esperando conexión Live WebSocket para enviar tu mensaje...");
            return false;
        }

        var runCt = CurrentRunToken();
        if (runCt.IsCancellationRequested || callerCt.IsCancellationRequested)
            return false;

        CancellationTokenSource? linkedCts = null;
        try { linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt, runCt); }
        catch (ObjectDisposedException) { return false; }

        int id = Interlocked.Increment(ref _taskSeq);
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workTask = Task.Run(async () =>
        {
            using (linkedCts)
            {
                var ct = linkedCts.Token;
                try
                {
                    _onLog?.Invoke($"💬 Vos: \"{msg}\"");
                    Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                    Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

                    byte[]? fresh = attachCurrentScreen
                        ? CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _framesSent))
                        : null;

                    await _turnGate.WaitAsync(ct);
                    try
                    {
                        if (_disposed || !_backend.IsConnected || ct.IsCancellationRequested)
                        {
                            readyTcs.TrySetResult(false);
                            return;
                        }
                        if (fresh is not null)
                            await _backend.SendImageAndPromptTurnAsync(fresh, msg, ct);
                        else
                            await _backend.SendTextTurnAsync(msg, ct);
                        readyTcs.TrySetResult(true);
                    }
                    finally
                    {
                        try { _turnGate.Release(); } catch (ObjectDisposedException) { }
                    }
                }
                catch (OperationCanceledException) { readyTcs.TrySetResult(false); }
                catch (ObjectDisposedException) { readyTcs.TrySetResult(false); }
                catch (Exception ex)
                {
                    _onLog?.Invoke($"Error chat Live: {SecretRedactor.Redact(ex.Message)}");
                    readyTcs.TrySetResult(false);
                }
                finally
                {
                    _inFlightTasks.TryRemove(id, out _);
                }
            }
        });
        _inFlightTasks[id] = workTask;
        return await readyTcs.Task;
    }

    public void TogglePause()
    {
        _userPaused = !_userPaused;
        _onPauseChanged?.Invoke(_userPaused);
        var title = _activeProfile?.Manifest.DisplayName ?? "HUDMind Live";

        if (_userPaused)
        {
            lock (_audioStateLock)
            {
                _pcmPlayer.InterruptAndClear();
                _modelSpeaking = false;
                _bargeInMuted = false;
            }
            StopMicAndDrainChannel();
            _onLog?.Invoke($"Coach Live PAUSADO ({_config.HotkeyTogglePause} para reanudar).");
            if (_config.EnableOverlay)
                _overlay.ShowCallout($"Coach Live PAUSADO — {_config.HotkeyTogglePause}", UrgencyLevel.Low, title, 2.5);
        }
        else
        {
            if (_micEnabled && _backend.IsConnected) StartMicIfEnabled();
            WakeNow();
            _onLog?.Invoke("Coach Live REANUDADO.");
            if (_config.EnableOverlay)
            {
                _overlay.ShowCallout(
                    _config.IsTextOnlyMode ? "Coach Live ACTIVO — solo texto" : _micEnabled ? "Coach Live ACTIVO — charla en vivo" : "Coach Live ACTIVO — solo voz del coach",
                    UrgencyLevel.Low, title, 2.5);
            }
        }
        EmitStats();
    }

    public void TriggerOnDemandAnalysis()
    {
        if (!_backend.IsConnected)
        {
            _onLog?.Invoke("Esperando conexión Live WebSocket...");
            return;
        }
        if (!_testMode && _activeProfile is null)
        {
            _onLog?.Invoke($"{_config.HotkeyAnalyzeNow} ignorado: abrí o enfocá primero un juego.");
            return;
        }
        if (_userPaused)
        {
            _userPaused = false;
            _onPauseChanged?.Invoke(false);
            if (_micEnabled) StartMicIfEnabled();
        }

        TrackFireAndForget(async ct =>
        {
            var fresh = CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _framesSent));
            if (fresh is null)
                return;

            await _turnGate.WaitAsync(ct);
            try
            {
                if (!_backend.IsConnected) return;
                Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

                bool isEn = CoachingPersonaBuilder.IsEnglish(_config);
                string prompt = isEn
                    ? "Look closely at this exact game frame. Tell me in 1 crisp clear sentence what is happening and the best next play right now."
                    : "Mirá esta imagen actual exacta del juego. Decime en 1 oración corta y clara qué está pasando y qué me conviene hacer ahora.";

                await _backend.SendImageAndPromptTurnAsync(fresh, prompt, ct);
            }
            finally
            {
                _turnGate.Release();
            }
        });
    }

    private void WakeNow()
    {
        try
        {
            if (!_disposed && _wakeSignal.CurrentCount == 0)
                _wakeSignal.Release();
        }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task LifecycleLoopAsync(CancellationToken ct)
    {
        int rapidFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            using var localReconnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Volatile.Write(ref _reconnectCts, localReconnectCts);
            Task? videoTask = null;
            bool hadValidSession = false;
            long sessionEpoch = Volatile.Read(ref _profileEpoch);
            var sessionProfile = _activeProfile;

            try
            {
                await _backend.RunSessionLoopAsync(
                    _config,
                    sessionProfile,
                    onPcm24kAudio: pcm => HandleIncomingAudioChunk(sessionEpoch, pcm),
                    onPartialTranscript: line => HandlePartialTranscript(sessionEpoch, sessionProfile, line),
                    onTurnComplete: spoken => HandleTurnComplete(sessionEpoch, sessionProfile, spoken),
                    onInterrupted: HandleInterrupted,
                    onLog: msg => _onLog?.Invoke(SecretRedactor.Redact(msg)),
                    onConnected: () =>
                    {
                        hadValidSession = true;
                        rapidFailures = 0;
                        var voiceName = _backend.ResolveVoiceName(_config.VoiceGender);
                        string modeDesc = _config.IsTextOnlyMode
                            ? $"💬 SOLO TEXTO (silencioso — escribí abajo o tocá {_config.HotkeyAnalyzeNow})"
                            : _config.IsCoachVoiceOnlyMode
                                ? $"🔊 SOLO VOZ COACH ({voiceName} · mic apagado)"
                                : $"🎙 CHARLA ({voiceName} · hablale por mic o escribí)";

                        _onLog?.Invoke($"LIVE LISTO [{_backend.ProviderName} · {_backend.ActiveModelLabel}] · {modeDesc}");
                        if (_config.EnableOverlay && !_userPaused)
                        {
                            _overlay.ShowCallout(
                                _config.IsTextOnlyMode
                                    ? "Coach Live en modo SOLO TEXTO (silencioso)"
                                    : _micEnabled
                                        ? $"Coach Live ({voiceName}) listo — hablale directo"
                                        : $"Coach Live ({voiceName}) — solo voz del coach",
                                UrgencyLevel.Low,
                                _activeProfile?.Manifest.DisplayName ?? "HUDMind Live",
                                3.0);
                        }
                        if (_micEnabled && !_userPaused)
                            StartMicIfEnabled();
                        EmitStats();
                        videoTask = Task.Run(() => VideoAndAutoCoachLoopAsync(localReconnectCts.Token), localReconnectCts.Token);
                    },
                    sessionCt: localReconnectCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (localReconnectCts.IsCancellationRequested)
            {
                // Intentional hot-reconnect (voice/rank/game profile switched)
                hadValidSession = true;
                rapidFailures = 0;
            }
            catch (Exception ex)
            {
                rapidFailures++;
                var redacted = SecretRedactor.Redact(ex.Message);
                _onLog?.Invoke($"Sesión Live interrumpida: {redacted}");
                if (redacted.Contains("401") || redacted.Contains("403") || redacted.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
                {
                    _onLog?.Invoke("⛔ Revisá tu API Key en 'Cuentas IA' (error de autenticación).");
                    break;
                }
            }
            finally
            {
                Volatile.Write(ref _reconnectCts, null);
                try { localReconnectCts.Cancel(); } catch { }
                WakeNow();
                lock (_audioStateLock)
                {
                    _modelSpeaking = false;
                    _bargeInMuted = false;
                    _suppressCurrentTurnAudio = false;
                    _pcmPlayer.InterruptAndClear();
                }
                Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
                StopMicAndDrainChannel();
                if (videoTask is not null)
                {
                    try { await videoTask; } catch { /* clean exit */ }
                }
            }

            if (!ct.IsCancellationRequested)
            {
                int backoffMs = hadValidSession
                    ? 220
                    : Math.Min(8000, 900 * (1 << Math.Min(rapidFailures, 3)));
                try { await Task.Delay(backoffMs, ct); } catch { break; }
            }
        }
    }

    private void HandleIncomingAudioChunk(long sessionEpoch, byte[] pcm24k)
    {
        if (pcm24k.Length == 0 || Volatile.Read(ref _profileEpoch) != sessionEpoch) return;
        lock (_audioStateLock)
        {
            if (!_config.IsCoachVoiceActive || _userPaused || _bargeInMuted || _suppressCurrentTurnAudio)
                return;

            if (Interlocked.Exchange(ref _waitingFirstAudioInt, 0) == 1)
            {
                long reqTick = Volatile.Read(ref _lastTurnRequestTick);
                if (reqTick > 0)
                    Volatile.Write(ref _lastTtfaMs, Stopwatch.GetElapsedTime(reqTick).TotalMilliseconds);
            }
            _modelSpeaking = true;
            _pcmPlayer.EnqueuePcm24k(pcm24k);
        }
    }

    private void HandlePartialTranscript(long sessionEpoch, GameProfile? sessionProfile, string currentLine)
    {
        if (string.IsNullOrWhiteSpace(currentLine) || Volatile.Read(ref _profileEpoch) != sessionEpoch) return;

        if (_config.IsTextOnlyMode && Interlocked.Exchange(ref _waitingFirstAudioInt, 0) == 1)
        {
            long reqTick = Volatile.Read(ref _lastTurnRequestTick);
            if (reqTick > 0)
                Volatile.Write(ref _lastTtfaMs, Stopwatch.GetElapsedTime(reqTick).TotalMilliseconds);
        }

        // Coalesce partial overlay updates (~90ms) to keep 0% GPU CPU rasterization ultra-light!
        if (_config.EnableOverlay && !_userPaused && currentLine.Length >= 5)
        {
            long prevTick = Interlocked.Read(ref _lastOverlayPartialTick);
            if (prevTick == 0 || Stopwatch.GetElapsedTime(prevTick).TotalMilliseconds >= 90.0)
            {
                Interlocked.Exchange(ref _lastOverlayPartialTick, Stopwatch.GetTimestamp());
                var title = (sessionProfile ?? _activeProfile)?.Manifest.DisplayName ?? "HUDMind Live";
                _overlay.ShowCallout(
                    currentLine,
                    UrgencyLevel.Medium,
                    title,
                    _config.OverlayDurationSeconds);
            }
        }
    }

    private void HandleTurnComplete(long sessionEpoch, GameProfile? sessionProfile, string spoken)
    {
        bool staleEpoch = Volatile.Read(ref _profileEpoch) != sessionEpoch;
        bool allowFlush;
        lock (_audioStateLock)
        {
            allowFlush = !staleEpoch && _config.IsCoachVoiceActive && !_userPaused && !_bargeInMuted && !_suppressCurrentTurnAudio;
            if (allowFlush)
                _pcmPlayer.FlushBuffered();
            else
                _pcmPlayer.InterruptAndClear();
            _modelSpeaking = false;
            _bargeInMuted = false;
            _suppressCurrentTurnAudio = false;
        }
        Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
        Interlocked.Exchange(ref _lastOverlayPartialTick, 0);

        if (!staleEpoch && !string.IsNullOrWhiteSpace(spoken))
        {
            var prof = sessionProfile ?? _activeProfile;
            _memory.RecordMatchEvent(
                prof?.Manifest.GameId ?? string.Empty,
                prof?.Manifest.DisplayName ?? "HUDMind Live",
                "LIVE",
                string.Empty,
                spoken);

            if (_config.EnableOverlay && !_userPaused)
            {
                _overlay.ShowCallout(
                    spoken,
                    UrgencyLevel.Medium,
                    prof?.Manifest.DisplayName ?? "HUDMind Live",
                    _config.OverlayDurationSeconds);
            }
            Interlocked.Increment(ref _interventions);
            double ttfa = Volatile.Read(ref _lastTtfaMs);
            string icon = _config.IsTextOnlyMode ? "💬" : (_config.IsCoachVoiceOnlyMode ? "🔊" : "🎙");
            _onLog?.Invoke($"{icon} Coach ({ttfa:F0}ms): \"{spoken}\"");
            EmitStats();
        }
    }

    private void HandleInterrupted()
    {
        lock (_audioStateLock)
        {
            _pcmPlayer.InterruptAndClear();
            _modelSpeaking = false;
            _bargeInMuted = false;
            _suppressCurrentTurnAudio = false;
        }
        Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
    }

    private void StartMicIfEnabled()
    {
        lock (_micGateLock)
        {
            if (_disposed || !_micEnabled || _userPaused || !_backend.IsConnected) return;

            StopMicAndDrainChannelLocked();

            var micChan = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _micSendChannel = micChan;
            var runCt = CurrentRunToken();
            _micSendTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in micChan.Reader.ReadAllAsync(runCt))
                    {
                        if (_backend.IsConnected && !_userPaused && _micEnabled)
                            await _backend.SendMicAudio16kAsync(chunk, runCt);
                    }
                }
                catch (OperationCanceledException) when (runCt.IsCancellationRequested) { }
            }, runCt);

            bool ok = _mic.TryStart(
                pcmChunk =>
                {
                    if (_disposed || !_backend.IsConnected || _userPaused || !_micEnabled) return;
                    if (!_testMode && !_gameFocused) return;

                    long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                    bool pttMode = _config.IsPushToTalkMode;
                    bool pttHeld = false;
                    bool pttInReleaseTail = false;
                    if (pttMode)
                    {
                        pttHeld = _isPttKeyPressed?.Invoke() ?? true;
                        if (pttHeld)
                        {
                            Interlocked.Exchange(ref _lastPttHeldUtcTicks, nowTicks);
                        }
                        else
                        {
                            // Keep sending ~420ms after PTT release (> 250ms Gemini / 260ms OpenAI server VAD silence window)
                            // so server VAD detects end-of-utterance cleanly without waiting for more audio!
                            long prevPtt = Interlocked.Read(ref _lastPttHeldUtcTicks);
                            if (prevPtt == 0 || (nowTicks - prevPtt) > TimeSpan.FromMilliseconds(420.0).Ticks)
                                return;
                            pttInReleaseTail = true;
                        }
                    }

                    double rms = AudioPcmUtilities.ComputeRms16(pcmChunk);
                    bool coachAudible = _modelSpeaking || _pcmPlayer.IsPlayingAudio;

                    // Acoustic Echo Suppression: ignore speaker bleed while coach speaks, trigger clean barge-in above threshold
                    if (coachAudible)
                    {
                        if (pttInReleaseTail)
                            return; // Do not trigger false echo barge-in on post-release silence tail

                        double bargeThreshold = (pttMode && pttHeld) ? 650.0 : 1800.0;
                        if (rms < bargeThreshold)
                            return;

                        lock (_audioStateLock)
                        {
                            // Mute residual dying chunks ONLY when server stream is still active (_modelSpeaking == true)!
                            // If server already emitted turnComplete (_modelSpeaking == false), do NOT mute future replies.
                            if (_modelSpeaking)
                                _bargeInMuted = true;
                            _modelSpeaking = false;
                            _pcmPlayer.InterruptAndClear();
                        }
                    }

                    if (pttMode)
                    {
                        if (pttHeld)
                        {
                            Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                            Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                        }
                    }
                    else
                    {
                        if (rms >= 320.0)
                        {
                            Interlocked.Exchange(ref _lastMicVoiceUtcTicks, nowTicks);
                            Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                            Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                        }
                        else
                        {
                            long lastMic = Interlocked.Read(ref _lastMicVoiceUtcTicks);
                            if (lastMic == 0 || (nowTicks - lastMic) > TimeSpan.FromMilliseconds(320.0).Ticks)
                                return;
                        }
                    }

                    micChan.Writer.TryWrite(pcmChunk);
                },
                err => _onLog?.Invoke(err));

            if (ok)
                _onLog?.Invoke(_config.IsPushToTalkMode
                    ? $"🎤 Micrófono 16kHz en Push-to-Talk (mantené '{_config.PushToTalkKey}' para hablar)."
                    : "🎤 Micrófono 16kHz con supresión de eco activo — hablá cuando quieras.");
            else
                StopMicAndDrainChannelLocked();
        }
    }

    private void StopMicAndDrainChannel()
    {
        lock (_micGateLock)
            StopMicAndDrainChannelLocked();
    }

    private void StopMicAndDrainChannelLocked()
    {
        _mic.Stop();
        var ch = Interlocked.Exchange(ref _micSendChannel, null);
        ch?.Writer.TryComplete();
    }

    private (int W, int H, int Quality) ResolveTargetResolution() =>
        (_config.TokenEfficiencyMode ?? "balanced").ToLowerInvariant() switch
        {
            "eco" => (800, 450, 74),
            "high" => (1280, 720, 82),
            _ => (960, 540, 78)
        };

    private byte[]? CaptureSharpFrame(bool force, long frameId)
    {
        ulong? prevHash;
        lock (_hashLock) prevHash = _lastSentHash;

        var (w, h, q) = ResolveTargetResolution();
        int phash = _activeProfile?.Manifest.PhashThreshold ?? 6;
        var region = _testMode ? null : _windowDetector.GetForegroundClientRect();

        var (frame, skipped) = _capture.CaptureIfChanged(
            _config.CaptureMonitor,
            region,
            targetWidth: w,
            targetHeight: h,
            jpegQuality: q,
            frameId: frameId,
            lastAcceptedHash: prevHash,
            phashThreshold: phash,
            isForced: force);

        _blackScreenDiagnostic.Observe(_capture, _activeProfile, _testMode, _onLog, _overlay, _config.EnableOverlay);

        if (skipped || frame is null)
            return null;

        lock (_hashLock)
        {
            _lastSentHash = frame.PerceptualHash;
            Volatile.Write(ref _lastCaptureMs, frame.CaptureMs + frame.EncodeMs);
        }
        return frame.JpegBytes;
    }

    private async Task VideoAndAutoCoachLoopAsync(CancellationToken ct)
    {
        long frameCounter = 0;
        while (!ct.IsCancellationRequested && _backend.IsConnected)
        {
            try
            {
                if (!_userPaused)
                {
                    if (!_testMode)
                    {
                        var fg = _windowDetector.GetForegroundExecutable();
                        var matched = MatchForegroundProfile(fg);

                        if (matched is not null)
                        {
                            _gameFocused = true;
                            if (!ReferenceEquals(matched, _activeProfile))
                            {
                                var prev = _activeProfile;
                                if (prev is not null)
                                    FlushMatchSnapshot(resetMatchLog: true);

                                Interlocked.Increment(ref _profileEpoch);
                                _config.RuntimeTiltRisk = false;
                                Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, 0);
                                lock (_hashLock) _lastSentHash = null;
                                _localPhaseGate?.Reset();
                                _tacticalTracker.Reset(matched.Manifest.GameId);
                                _blackScreenDiagnostic.Reset();
                                if (_resolveContextForProfile is not null)
                                    CoachContextAssembler.ApplyToConfig(_config, _resolveContextForProfile(matched));
                                _activeProfile = matched;
                                _onLog?.Invoke($"Juego detectado automáticamente: {matched.Manifest.DisplayName} ({fg})");
                                if (_config.EnableOverlay)
                                    _overlay.ShowCallout("Coach Live ACTIVO — observando", UrgencyLevel.Low, matched.Manifest.DisplayName, 3.0);
                                EmitStats();

                                if (prev is not null || !string.Equals(matched.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase))
                                {
                                    RefreshLivePersona(matched.Manifest.DisplayName);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            _gameFocused = false;
                            await Task.Delay(700, ct);
                            continue;
                        }
                    }

                    var jpeg = CaptureSharpFrame(force: false, frameId: ++frameCounter);
                    if (jpeg is not null)
                    {
                        Interlocked.Increment(ref _framesSent);

                        bool isEnEval = CoachingPersonaBuilder.IsEnglish(_config);
                        string rawTelemetry = _localPhaseGate is not null && _activeProfile is not null
                            ? await _localPhaseGate.ReadTelemetryAsync(_activeProfile, ct)
                            : string.Empty;
                        var tactical = TacticalSignalEvaluator.Evaluate(rawTelemetry, _activeProfile, _tacticalTracker, isEnEval);
                        _config.RuntimeTiltRisk = tactical.IsTiltRisk;
                        if (tactical.MatchEnded)
                            FlushMatchSnapshot(resetMatchLog: true);

                        double autoSec = _config.LiveAutoCoachSeconds;
                        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                        long lastMicTicks = Interlocked.Read(ref _lastMicVoiceUtcTicks);
                        long lastNudgeTicks = Interlocked.Read(ref _lastAutoNudgeUtcTicks);
                        bool userRecentlySpoke = lastMicTicks > 0 && (nowTicks - lastMicTicks) < TimeSpan.FromSeconds(3.0).Ticks;
                        bool canVoiceTurn = autoSec > 0 && !_modelSpeaking && !_pcmPlayer.IsPlayingAudio && !userRecentlySpoke;
                        double scaledAutoSec = autoSec * tactical.CooldownMultiplier;
                        bool regularDue = canVoiceTurn &&
                            (lastNudgeTicks == 0 || (nowTicks - lastNudgeTicks) >= TimeSpan.FromSeconds(scaledAutoSec).Ticks);
                        bool eventDue = canVoiceTurn && tactical.TriggerImmediate &&
                            (lastNudgeTicks == 0 || (nowTicks - lastNudgeTicks) >= TimeSpan.FromSeconds(Math.Max(3.5, autoSec * 0.45)).Ticks);
                        bool shouldNudge = (regularDue || eventDue) && !tactical.ShouldSilence;

                        bool sentAsTurn = false;
                        if (shouldNudge)
                        {
                            Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, nowTicks);

                            bool skipProactive = false;
                            if (_localPhaseGate is not null && _activeProfile is not null)
                            {
                                var snap = await _localPhaseGate.ExtractAsync(jpeg, _activeProfile, ct);
                                if (snap.Phase is "MENU" or "REPLAY" or "LOADING" or "DESKTOP")
                                {
                                    skipProactive = true;
                                    _onLog?.Invoke($"Silencio local [{snap.Phase}] (sin molestar en menú/replay).");
                                }
                            }

                            if (!skipProactive)
                            {
                                sentAsTurn = true;
                                await _turnGate.WaitAsync(ct);
                                try
                                {
                                    Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                                    Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                                    string factNote = string.IsNullOrWhiteSpace(tactical.DeterministicFact)
                                        ? string.Empty
                                        : (isEnEval
                                            ? $" Verified sensor fact: {tactical.DeterministicFact}."
                                            : $" Dato exacto del juego: {tactical.DeterministicFact}.");
                                    string prompt = isEnEval
                                        ? $"Look at this exact live game frame.{factNote} Give 1 crisp, helpful tactical tip in 8-14 words based strictly on what you see."
                                        : $"Mirá esta captura exacta de la partida actual.{factNote} Da 1 solo consejo táctico claro, natural y útil en español (8 a 14 palabras) basado únicamente en lo que ves en pantalla.";
                                    await _backend.SendImageAndPromptTurnAsync(jpeg, prompt, ct);
                                }
                                finally
                                {
                                    _turnGate.Release();
                                }
                            }
                        }

                        if (!sentAsTurn)
                            await _backend.SendVideoFrameAsync(jpeg, ct);

                        EmitStats();
                    }
                }

                double fps = _activeProfile?.Manifest.CaptureFps ?? 1.0;
                int periodMs = Math.Clamp((int)Math.Round(1000.0 / Math.Clamp(fps, 0.5, 2.0)), 500, 1500);
                try { await _wakeSignal.WaitAsync(TimeSpan.FromMilliseconds(periodMs), ct); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Error video Live: {SecretRedactor.Redact(ex.Message)}");
                await Task.Delay(500, ct);
            }
        }
    }

    private void EmitStats()
    {
        string modeTag = _config.IsTextOnlyMode
            ? "Solo texto"
            : _config.IsCoachVoiceOnlyMode
                ? $"Solo voz ({_backend.ResolveVoiceName(_config.VoiceGender)})"
                : $"Charla ({_backend.ResolveVoiceName(_config.VoiceGender)})";

        _onStats?.Invoke(new CoachRuntimeStats(
            Interlocked.Read(ref _framesSent),
            0,
            Interlocked.Read(ref _interventions),
            Interlocked.Read(ref _interventions),
            Volatile.Read(ref _lastCaptureMs),
            Volatile.Read(ref _lastTtfaMs),
            0.0,
            $"LIVE · {_backend.ActiveModelLabel} · {modeTag}",
            _activeProfile?.Manifest.DisplayName ?? (_testMode ? "Universal" : "(esperando juego)"),
            _userPaused
        ));
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var cts = Interlocked.Exchange(ref _runCts, null);
            if (cts is null) return;

            await cts.CancelAsync();
            WakeNow();
            StopMicAndDrainChannel();
            _pcmPlayer.InterruptAndClear();

            var tasksToAwait = new List<Task>();
            if (_sessionTask is not null) tasksToAwait.Add(_sessionTask);
            if (_micSendTask is not null) tasksToAwait.Add(_micSendTask);
            tasksToAwait.AddRange(_inFlightTasks.Values);
            if (tasksToAwait.Count > 0)
            {
                try { await Task.WhenAll(tasksToAwait); } catch { /* clean stop */ }
            }

            FlushMatchSnapshot(resetMatchLog: false);
            _sessionTask = null;
            _micSendTask = null;
            cts.Dispose();
            _onLog?.Invoke("Sesión Live detenida.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        await _backend.DisposeAsync();
        _mic.Dispose();
        _pcmPlayer.Dispose();
        _capture.Dispose();
        _wakeSignal.Dispose();
        _turnGate.Dispose();
        _lifecycleGate.Dispose();
    }
}
