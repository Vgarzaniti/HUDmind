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
    private readonly IForegroundWindowDetector _windowDetector;
    private readonly IScreenCaptureService _capture;
    private readonly IStreamingPcmPlayer _pcmPlayer;
    private readonly IStreamingMicCapture _mic;
    private readonly IOverlayOutputService _overlay;
    private readonly ITextOnlySensorService? _localPhaseGate;
    private readonly Func<bool>? _isPttKeyPressed;
    private readonly bool _testMode;
    private readonly Action<string>? _onLog;
    private readonly Action<CoachRuntimeStats>? _onStats;
    private readonly Action<bool>? _onPauseChanged;
    private readonly ITemporalStoryboardComposer? _storyboardComposer;
    private readonly ICoachSafetyPipeline _safety;
    private readonly IAudioSceneMonitor _audioMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly MatchSessionCoordinator _session;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _hashLock = new();
    private readonly object _audioStateLock = new();
    private readonly object _micGateLock = new();
    private readonly object _followUpLock = new();
    private readonly object _modeLock = new();
    private readonly ConcurrentDictionary<int, Task> _inFlightTasks = new();
    private int _taskSeq;
    private long _turnSeq;
    private long _proactiveTurnSeq;
    private long _proactiveRetryAfterUtcTicks;
    private long _captureSeq;
    private string? _pendingLiveFollowUpTopic;
    private DateTimeOffset _pendingLiveFollowUpExpiresUtc;
    private bool _pendingLiveFollowUpUrgent;

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _reconnectCts;
    private Task? _sessionTask;
    private Channel<byte[]>? _micSendChannel;
    private Task? _micSendTask;

    private volatile bool _userPaused;
    private volatile bool _micEnabled;
    private volatile bool _modelSpeaking;
    private volatile bool _gameFocused;
    private volatile bool _bargeInMuted;
    private volatile bool _suppressCurrentTurnAudio;
    private volatile bool _activeSessionTextOnly;
    private string _activeInputDeviceName = string.Empty;
    private string _activeOutputDeviceName = string.Empty;
    private volatile bool _disposed;
    private int _disposeOnce;

    private ulong? _lastSentHash;
    private long _framesSent;
    private long _interventions;
    private double _lastCaptureMs;
    private double _lastTtfaMs;
    private long _lastTurnRequestTick;
    private int _waitingFirstAudioInt;
    private int _bargeInConsecutiveFrames;
    private long _lastOverlayPartialTick;
    private long _lastOverlayAudioTick;
    private int _lastPttActiveState;
    private long _lastAutoNudgeUtcTicks;
    private long _lastSpeechCompletedUtcTicks;
    private long _lastMicVoiceUtcTicks;
    private long _lastPttHeldUtcTicks;
    private long _lastWebSocketFrameTick;
    private long _lastMicVisualBurstTick;
    private int _proactiveTurnInFlight;
    private int _sessionCompletedTurns;
    private int _suppressNextConnectedOverlay;

    public bool IsRunning => Volatile.Read(ref _runCts) is { IsCancellationRequested: false };
    public bool IsPaused => _userPaused;
    public bool SupportsLiveMic => true;
    public string EngineBadge => ".NET 9 · 0% GPU · LIVE WS";
    public string ActiveVoiceLabel => _backend.ResolveVoiceName(_config.VoiceGender);
    public string ActiveProviderName => _backend.ProviderName;
    public string ActiveVoiceDisplay => ActiveVoiceLabel;
    public string ActiveGameId => _session.ActiveProfile?.Manifest.GameId ?? string.Empty;
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
        if (_disposed) return;
        CancellationTokenSource? recToCancel = null;
        string? pendingMicLog = null;
        string modeName;
        lock (_modeLock)
        {
            if (_disposed) return;
            bool wasTextOnly = _activeSessionTextOnly;
            bool wasMicEnabled = _micEnabled;
            string prevInputDevice = _activeInputDeviceName;
            string prevOutputDevice = _activeOutputDeviceName;
            _config.CopyRuntimePreferencesFrom(config);
            SetVolume(_config.TtsVolume);
            modeName = _config.CommunicationMode;
            _micEnabled = _config.IsMicActive;
            bool inputDeviceChanged = !string.Equals(
                prevInputDevice,
                (_config.AudioInputDeviceName ?? string.Empty).Trim(),
                StringComparison.Ordinal);
            if (!_micEnabled || (inputDeviceChanged && _mic.IsCapturing))
                StopMicAndDrainChannel();
            ApplyAudioDeviceNamesLocked(_config.AudioInputDeviceName, _config.AudioOutputDeviceName);
            bool outputDeviceChanged = !string.Equals(prevOutputDevice, _activeOutputDeviceName, StringComparison.Ordinal);
            bool nowTextOnly = _config.IsTextOnlyMode;
            _activeSessionTextOnly = nowTextOnly;
            var activeRec = Volatile.Read(ref _reconnectCts);
            bool willReconnect = wasTextOnly != nowTextOnly && IsRunning && activeRec is { IsCancellationRequested: false };
            if (willReconnect)
                recToCancel = activeRec;
            pendingMicLog = ApplyVoiceAndMicStateLocked(
                wasTextOnly,
                nowTextOnly,
                _config.IsCoachVoiceOnlyMode,
                preOpenOutput: wasTextOnly || outputDeviceChanged,
                startMicIfConnected: !willReconnect && (!wasMicEnabled || wasTextOnly || inputDeviceChanged));
        }
        if (!string.IsNullOrEmpty(pendingMicLog))
            _onLog?.Invoke(pendingMicLog);
        CancelReconnectSession(recToCancel, $"canal {modeName}");
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
        Func<GameProfile, AssembledCoachContext>? resolveContextForProfile = null,
        ITemporalStoryboardComposer? storyboardComposer = null,
        ICoachSafetyPipeline? safetyPipeline = null,
        IAudioSceneMonitor? audioMonitor = null,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _config = config;
        _windowDetector = windowDetector;
        _capture = capture;
        _pcmPlayer = pcmPlayer;
        _mic = mic;
        _overlay = overlay;
        _localPhaseGate = localPhaseGate;
        _isPttKeyPressed = isPttKeyPressed;
        _testMode = testMode;
        _onLog = onLog;
        _onStats = onStats;
        _onPauseChanged = onPauseChanged;
        _storyboardComposer = storyboardComposer;
        _safety = safetyPipeline ?? CoachContentGuard.DefaultPipeline;
        _audioMonitor = audioMonitor ?? NullAudioSceneMonitor.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _session = new MatchSessionCoordinator(
            profiles,
            windowDetector,
            capture,
            overlay,
            testMode,
            forcedGameId,
            onLog,
            onMatchCompleted,
            resolveContextForProfile);
        _micEnabled = config.IsMicActive;
        _activeInputDeviceName = (config.AudioInputDeviceName ?? string.Empty).Trim();
        _activeOutputDeviceName = (config.AudioOutputDeviceName ?? string.Empty).Trim();
    }

    public (string GameName, string Digest, int Count) GetMatchSessionDigest(bool isEn = false) =>
        _session.GetMatchSessionDigest(isEn);

    public MatchSessionSnapshot? CaptureMatchSnapshot(bool isEn = false) =>
        _session.CaptureMatchSnapshot(isEn);

    public void SetAudioDevicesLive(string? inputDeviceName, string? outputDeviceName)
    {
        if (_disposed) return;
        string? pendingMicLog = null;
        lock (_modeLock)
        {
            if (_disposed) return;
            var cleanIn = (inputDeviceName ?? string.Empty).Trim();
            string prevInputDevice = _activeInputDeviceName;
            string prevOutputDevice = _activeOutputDeviceName;
            bool inputChanged = !string.Equals(prevInputDevice, cleanIn, StringComparison.Ordinal);
            if (inputChanged && _mic.IsCapturing)
                StopMicAndDrainChannel();
            ApplyAudioDeviceNamesLocked(cleanIn, outputDeviceName);
            bool outputChanged = !string.Equals(
                prevOutputDevice,
                _activeOutputDeviceName,
                StringComparison.Ordinal);
            if (outputChanged && IsRunning && _config.IsCoachVoiceActive)
                _pcmPlayer.PreOpenDevice();
            if (inputChanged && _micEnabled && !_userPaused && _backend.IsConnected &&
                Volatile.Read(ref _reconnectCts) is { IsCancellationRequested: false })
            {
                pendingMicLog = StartMicIfEnabled();
            }
        }
        if (!string.IsNullOrEmpty(pendingMicLog))
            _onLog?.Invoke(pendingMicLog);
    }

    private void ApplyAudioDeviceNamesLocked(string? inputDeviceName, string? outputDeviceName)
    {
        var cleanIn = (inputDeviceName ?? string.Empty).Trim();
        var cleanOut = (outputDeviceName ?? string.Empty).Trim();
        _config.AudioInputDeviceName = cleanIn;
        _config.AudioOutputDeviceName = cleanOut;
        _activeInputDeviceName = cleanIn;
        _activeOutputDeviceName = cleanOut;
        if (_disposed) return;
        _pcmPlayer.SetDevice(cleanOut);
        lock (_micGateLock)
        {
            if (_disposed) return;
            _mic.SetDevice(cleanIn);
        }
    }

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed || IsRunning) return;

            var cleanIn = (_config.AudioInputDeviceName ?? string.Empty).Trim();
            var cleanOut = (_config.AudioOutputDeviceName ?? string.Empty).Trim();
            _pcmPlayer.SetDevice(cleanOut);
            _pcmPlayer.SetVolume(_config.TtsVolume);
            if (_config.IsCoachVoiceActive)
                _pcmPlayer.PreOpenDevice();
            _mic.SetDevice(cleanIn);
            lock (_modeLock)
            {
                _userPaused = false;
                _micEnabled = _config.IsMicActive;
                _activeSessionTextOnly = _config.IsTextOnlyMode;
                _activeInputDeviceName = cleanIn;
                _activeOutputDeviceName = cleanOut;
            }
            _config.RuntimeTiltRisk = false;
            _bargeInMuted = false;
            _suppressCurrentTurnAudio = false;
            Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, 0);
            Interlocked.Exchange(ref _lastSpeechCompletedUtcTicks, 0);
            Interlocked.Exchange(ref _lastPttHeldUtcTicks, 0);
            Interlocked.Exchange(ref _lastWebSocketFrameTick, 0);
            Interlocked.Exchange(ref _lastMicVisualBurstTick, 0);
            Interlocked.Exchange(ref _lastTurnRequestTick, 0);
            Interlocked.Exchange(ref _lastMicVoiceUtcTicks, 0);
            Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
            Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
            Interlocked.Exchange(ref _turnSeq, 0);
            Interlocked.Exchange(ref _proactiveTurnSeq, 0);
            Interlocked.Exchange(ref _proactiveRetryAfterUtcTicks, 0);
            Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
            Interlocked.Exchange(ref _sessionCompletedTurns, 0);
            Interlocked.Exchange(ref _suppressNextConnectedOverlay, 0);
            lock (_followUpLock) _pendingLiveFollowUpTopic = null;
            lock (_hashLock) _lastSentHash = null;

            var initialProfile = _session.InitializeOnStart(_config, predetectInNonTestMode: true);
            _gameFocused = _testMode || initialProfile is not null;

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
        var runCts = Volatile.Read(ref _runCts);
        if (runCts is null || _disposed) return;
        CancellationToken ct;
        try { ct = runCts.Token; }
        catch (ObjectDisposedException) { return; }
        if (ct.IsCancellationRequested || _disposed) return;
        int id = Interlocked.Increment(ref _taskSeq);
        var doneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Pre-register BEFORE Task.Run so fast completion or concurrent StopAsync never leaks/races _inFlightTasks
        _inFlightTasks[id] = doneTcs.Task;
        if (Volatile.Read(ref _runCts) != runCts || ct.IsCancellationRequested || _disposed)
        {
            _inFlightTasks.TryRemove(id, out _);
            doneTcs.TrySetResult();
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await work(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_disposed || ct.IsCancellationRequested) { }
            catch (Exception ex) { _onLog?.Invoke($"Error tarea Live: {SecretRedactor.Redact(ex.Message)}"); }
            finally
            {
                _inFlightTasks.TryRemove(id, out _);
                doneTcs.TrySetResult();
            }
        });
    }

    public void SetVolume(double volume) => _pcmPlayer.SetVolume(volume);

    public void RefreshLivePersona(string? reason = null)
    {
        CancellationTokenSource? rec;
        lock (_modeLock)
            rec = Volatile.Read(ref _reconnectCts);
        CancelReconnectSession(rec, reason ?? _config.PlayerSkillTier);
    }

    private void CancelReconnectSession(CancellationTokenSource? rec, string reason)
    {
        if (rec is { IsCancellationRequested: false })
        {
            _onLog?.Invoke($"Actualizando sesión Live ({reason})...");
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
        bool logMuted = false;
        string? pendingMicLog = null;
        lock (_modeLock)
        {
            if (_disposed) return;
            _micEnabled = enabled;
            if (!enabled)
            {
                StopMicAndDrainChannel();
                logMuted = true;
            }
            else if (_backend.IsConnected && !_userPaused)
            {
                pendingMicLog = StartMicIfEnabled();
            }
        }
        if (!string.IsNullOrEmpty(pendingMicLog))
            _onLog?.Invoke(pendingMicLog);
        if (logMuted)
            _onLog?.Invoke("🎤 Micrófono silenciado.");
    }

    public void SetCommunicationMode(string mode)
    {
        if (_disposed) return;
        bool nowTextOnly, coachVoiceOnly;
        string logMode;
        string? pendingMicLog = null;
        CancellationTokenSource? recToCancel = null;
        lock (_modeLock)
        {
            if (_disposed) return;
            bool wasTextOnly = _activeSessionTextOnly;
            _config.SetCommunicationMode(mode);
            _micEnabled = _config.IsMicActive;
            nowTextOnly = _config.IsTextOnlyMode;
            coachVoiceOnly = _config.IsCoachVoiceOnlyMode;
            logMode = _config.CommunicationMode;
            _activeSessionTextOnly = nowTextOnly;
            var activeRec = Volatile.Read(ref _reconnectCts);
            bool willReconnect = wasTextOnly != nowTextOnly && IsRunning && activeRec is { IsCancellationRequested: false };
            if (willReconnect)
                recToCancel = activeRec;
            pendingMicLog = ApplyVoiceAndMicStateLocked(
                wasTextOnly,
                nowTextOnly,
                coachVoiceOnly,
                preOpenOutput: wasTextOnly,
                startMicIfConnected: !willReconnect);
        }

        if (!string.IsNullOrEmpty(pendingMicLog))
            _onLog?.Invoke(pendingMicLog);

        if (nowTextOnly)
            _onLog?.Invoke("💬 Modo SOLO TEXTO activo (micrófono y voz silenciados; usá el chat escrito y overlay).");
        else if (coachVoiceOnly)
            _onLog?.Invoke("🔊 Modo SOLO VOZ DEL COACH activo (micrófono apagado; solo te habla el coach).");
        else
            _onLog?.Invoke("🎙 Modo CHARLA activo (micrófono en vivo + voz del coach).");

        EmitStats();

        // When crossing text_only <-> voice on backends that negotiate modalities at session setup (e.g. OpenAI), reload cleanly even during handshake
        CancelReconnectSession(recToCancel, $"canal {logMode}");
    }

    private string? ApplyVoiceAndMicStateLocked(
        bool wasTextOnly,
        bool nowTextOnly,
        bool coachVoiceOnly,
        bool preOpenOutput,
        bool startMicIfConnected)
    {
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
            return null;
        }
        else if (coachVoiceOnly)
        {
            _pcmPlayer.SetDevice(_activeOutputDeviceName);
            _pcmPlayer.SetVolume(_config.TtsVolume);
            if (preOpenOutput && IsRunning) _pcmPlayer.PreOpenDevice();
            StopMicAndDrainChannel();
            return null;
        }
        else
        {
            _pcmPlayer.SetDevice(_activeOutputDeviceName);
            _pcmPlayer.SetVolume(_config.TtsVolume);
            if (preOpenOutput && IsRunning) _pcmPlayer.PreOpenDevice();
            if (startMicIfConnected && _backend.IsConnected && !_userPaused)
                return StartMicIfEnabled();
            return null;
        }
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

        bool isEnMsg = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
        var inputGuard = _safety.EvaluateUserInput(msg, isEnMsg);
        if (!inputGuard.IsAllowed)
        {
            _onLog?.Invoke($"💬 Vos: \"{msg}\"");
            _onLog?.Invoke($"🛡️ Coach [safety-guard]: \"{inputGuard.SafeRedirectReply}\"");
            if (_config.EnableOverlay && !_userPaused)
            {
                _overlay.ShowMessage(new OverlayCalloutMessage(
                    inputGuard.SafeRedirectReply,
                    UrgencyLevel.Low,
                    _session.ActiveProfile?.Manifest.DisplayName ?? "HUDMind Live",
                    Math.Min(_config.OverlayDurationSeconds, 3.5),
                    OverlayEntryKind.Status,
                    "HUD"));
            }
            return true;
        }

        var runCts = Volatile.Read(ref _runCts);
        if (runCts is null || _disposed || callerCt.IsCancellationRequested)
            return false;

        CancellationTokenSource? linkedCts = null;
        try { linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt, runCts.Token); }
        catch (ObjectDisposedException) { return false; }

        int id = Interlocked.Increment(ref _taskSeq);
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var doneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlightTasks[id] = doneTcs.Task;
        if (Volatile.Read(ref _runCts) != runCts || runCts.IsCancellationRequested || callerCt.IsCancellationRequested || _disposed)
        {
            _inFlightTasks.TryRemove(id, out _);
            doneTcs.TrySetResult();
            linkedCts.Dispose();
            return false;
        }
        _ = Task.Run(async () =>
        {
            using (linkedCts)
            {
                var ct = linkedCts.Token;
                try
                {
                    _onLog?.Invoke($"💬 Vos: \"{msg}\"");
                    if (_config.EnableOverlay && !_userPaused)
                    {
                        _overlay.ShowMessage(new OverlayCalloutMessage(
                            msg,
                            UrgencyLevel.Low,
                            _session.ActiveProfile?.Manifest.DisplayName ?? "HUDMind Live",
                            _config.OverlayDurationSeconds,
                            OverlayEntryKind.UserMessage,
                            "Vos"));
                    }
                    var freshFrame = attachCurrentScreen
                        ? CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _captureSeq))
                        : null;
                    var (_, _, cfgQuality) = ResolveTargetResolution();
                    bool isEn = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
                    var clip = freshFrame is not null
                        ? _session.FrameRingBuffer.BuildVisualClipPayload(
                            _storyboardComposer,
                            freshFrame,
                            maxFrames: 4,
                            maxAgeSeconds: 4.5,
                            jpegQuality: Math.Max(cfgQuality, 84),
                            isEn: isEn)
                        : null;

                    var guide = GameKnowledgeCompiler.CompileForQuestion(_session.ActiveProfile, msg, maxChars: 520);
                    var timeline = _session.Memory.FormatVisualTimeline(isEn: isEn);
                    var enrichedPrompt = BuildEnrichedLiveQuestionPrompt(
                        msg, guide, timeline, clip?.SequenceNote ?? string.Empty, clip?.PanelCount ?? 0, isEn);

                    await _turnGate.WaitAsync(ct);
                    try
                    {
                        if (_disposed || !_backend.IsConnected || ct.IsCancellationRequested)
                        {
                            readyTcs.TrySetResult(false);
                            return;
                        }
                        Interlocked.Increment(ref _turnSeq);
                        Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
                        Interlocked.Exchange(ref _proactiveTurnSeq, 0);
                        Interlocked.Exchange(ref _proactiveRetryAfterUtcTicks, 0);
                        lock (_followUpLock) _pendingLiveFollowUpTopic = null;
                        Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                        Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, _timeProvider.GetUtcNow().UtcTicks);

                        if (clip is { Frames.Count: > 0 })
                        {
                            if (_backend.SupportsMultiFrameTurn && clip.Frames.Count > 1)
                            {
                                Interlocked.Increment(ref _framesSent);
                                await _backend.SendFrameSequenceAndPromptTurnAsync(clip.Frames, enrichedPrompt, ct);
                            }
                            else if (clip.FallbackSingleJpeg is { Length: > 0 } singleBytes)
                            {
                                Interlocked.Increment(ref _framesSent);
                                await _backend.SendImageAndPromptTurnAsync(singleBytes, enrichedPrompt, ct);
                            }
                            else
                            {
                                await _backend.SendTextTurnAsync(enrichedPrompt, ct);
                            }
                        }
                        else
                        {
                            await _backend.SendTextTurnAsync(enrichedPrompt, ct);
                        }
                        readyTcs.TrySetResult(true);
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
                        throw;
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
                    doneTcs.TrySetResult();
                }
            }
        });
        return await readyTcs.Task;
    }

    public void TogglePause()
    {
        if (_disposed || !IsRunning)
            return;
        bool pausedNow;
        bool micNow;
        bool textOnlyNow;
        string? pendingMicLog = null;
        lock (_modeLock)
        {
            if (_disposed || !IsRunning)
                return;
            pausedNow = !_userPaused;
            _userPaused = pausedNow;
            micNow = _micEnabled;
            textOnlyNow = _config.IsTextOnlyMode;
            if (pausedNow)
            {
                lock (_audioStateLock)
                {
                    _pcmPlayer.InterruptAndClear();
                    _modelSpeaking = false;
                    _bargeInMuted = false;
                }
                StopMicAndDrainChannel();
            }
            else if (micNow && _backend.IsConnected)
            {
                pendingMicLog = StartMicIfEnabled();
            }
        }
        _onPauseChanged?.Invoke(pausedNow);
        if (!string.IsNullOrEmpty(pendingMicLog))
            _onLog?.Invoke(pendingMicLog);
        var title = _session.ActiveProfile?.Manifest.DisplayName ?? "HUDMind Live";

        if (pausedNow)
        {
            _onLog?.Invoke($"Coach Live PAUSADO ({_config.HotkeyTogglePause} para reanudar).");
            if (_config.EnableOverlay)
                _overlay.ShowMessage(new OverlayCalloutMessage($"Coach Live PAUSADO — {_config.HotkeyTogglePause}", UrgencyLevel.Low, title, 2.5, OverlayEntryKind.Status, "HUD"));
        }
        else
        {
            WakeNow();
            _onLog?.Invoke("Coach Live REANUDADO.");
            if (_config.EnableOverlay)
            {
                _overlay.ShowMessage(new OverlayCalloutMessage(
                    textOnlyNow ? "Coach Live ACTIVO — solo texto" : micNow ? "Coach Live ACTIVO — charla en vivo" : "Coach Live ACTIVO — solo voz del coach",
                    UrgencyLevel.Low, title, 2.5, OverlayEntryKind.Status, "HUD"));
            }
        }
        EmitStats();
    }

    public void TriggerOnDemandAnalysis()
    {
        if (_disposed || !IsRunning)
            return;
        if (!_backend.IsConnected)
        {
            _onLog?.Invoke("Esperando conexión Live WebSocket...");
            return;
        }
        if (!_testMode && _session.ActiveProfile is null)
        {
            _onLog?.Invoke($"{_config.HotkeyAnalyzeNow} ignorado: abrí o enfocá primero un juego.");
            return;
        }
        bool unpausedNow = false;
        string? pendingMicLog = null;
        lock (_modeLock)
        {
            if (_disposed || !IsRunning) return;
            if (_userPaused)
            {
                _userPaused = false;
                unpausedNow = true;
                if (_micEnabled && _backend.IsConnected)
                    pendingMicLog = StartMicIfEnabled();
            }
        }
        if (unpausedNow)
        {
            _onPauseChanged?.Invoke(false);
            if (!string.IsNullOrEmpty(pendingMicLog))
                _onLog?.Invoke(pendingMicLog);
            WakeNow();
            EmitStats();
        }

        TrackFireAndForget(async ct =>
        {
            long captureEpoch = _session.CurrentEpoch;
            var freshFrame = CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _captureSeq));
            if (freshFrame is null)
                return;

            var (_, _, cfgQuality) = ResolveTargetResolution();
            bool isEn = CoachingPersonaBuilder.IsEnglish(_config);
            var clip = _session.FrameRingBuffer.BuildVisualClipPayload(
                _storyboardComposer,
                freshFrame,
                maxFrames: 4,
                maxAgeSeconds: 4.5,
                jpegQuality: Math.Max(cfgQuality, 84),
                isEn: isEn);

            try { await _turnGate.WaitAsync(ct); }
            catch (ObjectDisposedException) { return; }
            try
            {
                if (_disposed || !_backend.IsConnected || _userPaused || _session.CurrentEpoch != captureEpoch) return;
                Interlocked.Increment(ref _turnSeq);
                Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
                Interlocked.Exchange(ref _proactiveTurnSeq, 0);
                Interlocked.Exchange(ref _proactiveRetryAfterUtcTicks, 0);
                lock (_followUpLock) _pendingLiveFollowUpTopic = null;
                Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, _timeProvider.GetUtcNow().UtcTicks);

                string seqPrefix = clip.PanelCount >= 2 && !string.IsNullOrWhiteSpace(clip.SequenceNote)
                    ? $"{clip.SequenceNote} "
                    : string.Empty;
                string prompt = isEn
                    ? $"{seqPrefix}Give 1 predictive, crystal-clear tactical callout (8-14 words) for the next 2 seconds based ONLY on clearly visible pixels."
                    : $"{seqPrefix}Dame 1 sola llamada táctica predictiva y clara (8 a 14 palabras) para los próximos 2 segundos basándote SOLO en lo que se ve nítido.";

                Interlocked.Increment(ref _framesSent);
                if (_backend.SupportsMultiFrameTurn && clip.Frames.Count > 1)
                    await _backend.SendFrameSequenceAndPromptTurnAsync(clip.Frames, prompt, ct);
                else
                    await _backend.SendImageAndPromptTurnAsync(clip.FallbackSingleJpeg ?? freshFrame.JpegBytes, prompt, ct);
            }
            catch
            {
                Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
                throw;
            }
            finally
            {
                try { _turnGate.Release(); } catch (ObjectDisposedException) { }
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
            lock (_modeLock)
            {
                _activeSessionTextOnly = _config.IsTextOnlyMode;
                Volatile.Write(ref _reconnectCts, localReconnectCts);
            }
            Task? videoTask = null;
            bool hadValidSession = false;
            long sessionEpoch = _session.CurrentEpoch;
            var sessionProfile = _session.ActiveProfile;

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
                        if (_disposed || localReconnectCts.IsCancellationRequested)
                            return;
                        hadValidSession = true;
                        rapidFailures = 0;
                        Interlocked.Exchange(ref _sessionCompletedTurns, 0);
                        bool quietRefresh = Interlocked.Exchange(ref _suppressNextConnectedOverlay, 0) == 1;
                        var voiceName = _backend.ResolveVoiceName(_config.VoiceGender);
                        string modeDesc = _config.IsTextOnlyMode
                            ? $"💬 SOLO TEXTO (silencioso — escribí abajo o tocá {_config.HotkeyAnalyzeNow})"
                            : _config.IsCoachVoiceOnlyMode
                                ? $"🔊 SOLO VOZ COACH ({voiceName} · mic apagado)"
                                : $"🎙 CHARLA ({voiceName} · hablale por mic o escribí)";

                        if (!quietRefresh)
                            _onLog?.Invoke($"LIVE LISTO [{_backend.ProviderName} · {_backend.ActiveModelLabel}] · {modeDesc}");
                        if (_config.EnableOverlay && !_userPaused && !quietRefresh)
                        {
                            _overlay.ShowMessage(new OverlayCalloutMessage(
                                _config.IsTextOnlyMode
                                    ? "Coach Live en modo SOLO TEXTO (silencioso)"
                                    : _micEnabled
                                        ? $"Coach Live ({voiceName}) listo — hablale directo"
                                        : $"Coach Live ({voiceName}) — solo voz del coach",
                                UrgencyLevel.Low,
                                _session.ActiveProfile?.Manifest.DisplayName ?? "HUDMind Live",
                                3.0,
                                OverlayEntryKind.Status,
                                "HUD"));
                        }
                        string? pendingMicLog = null;
                        lock (_modeLock)
                        {
                            if (!_disposed && _micEnabled && !_userPaused && !localReconnectCts.IsCancellationRequested)
                                pendingMicLog = StartMicIfEnabled();
                        }
                        if (!string.IsNullOrEmpty(pendingMicLog))
                            _onLog?.Invoke(pendingMicLog);
                        EmitStats();
                        if (!_disposed && !localReconnectCts.IsCancellationRequested)
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
                lock (_modeLock)
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
                Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
                Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
                Interlocked.Exchange(ref _proactiveTurnSeq, 0);
                Interlocked.Exchange(ref _proactiveRetryAfterUtcTicks, 0);
                lock (_followUpLock) _pendingLiveFollowUpTopic = null;
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
        if (pcm24k.Length == 0 || _session.CurrentEpoch != sessionEpoch) return;
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
        double coachRms = AudioPcmUtilities.ComputeRms16(pcm24k);
        NotifyOverlayVoiceActivity(
            userMicActive: Interlocked.CompareExchange(ref _lastPttActiveState, 0, 0) == 1,
            userLevel01: 0,
            coachVoiceActive: true,
            coachLevel01: Math.Clamp(coachRms / 4200.0, 0.14, 1.0));
    }

    private void NotifyOverlayVoiceActivity(
        bool userMicActive,
        double userLevel01,
        bool coachVoiceActive,
        double coachLevel01,
        bool force = false)
    {
        if (!_config.EnableOverlay)
            return;
        long prev = Interlocked.Read(ref _lastOverlayAudioTick);
        if (!force && prev != 0 && Stopwatch.GetElapsedTime(prev).TotalMilliseconds < 65.0)
            return;
        Interlocked.Exchange(ref _lastOverlayAudioTick, Stopwatch.GetTimestamp());
        _overlay.UpdateVoiceActivity(userMicActive, userLevel01, coachVoiceActive, coachLevel01);
    }

    private void HandlePartialTranscript(long sessionEpoch, GameProfile? sessionProfile, string currentLine)
    {
        if (string.IsNullOrWhiteSpace(currentLine) || _session.CurrentEpoch != sessionEpoch) return;

        bool isEnOut = CoachingPersonaBuilder.IsEnglish(_config);
        var outGuard = _safety.InspectAndCleanModelOutput(currentLine, isEnOut);
        if (!outGuard.IsSafe)
        {
            lock (_audioStateLock)
            {
                _suppressCurrentTurnAudio = true;
                _modelSpeaking = false;
                _pcmPlayer.InterruptAndClear();
            }
            if (_config.EnableOverlay && !_userPaused)
            {
                var title = (sessionProfile ?? _session.ActiveProfile)?.Manifest.DisplayName ?? "HUDMind Live";
                _overlay.ShowMessage(new OverlayCalloutMessage(outGuard.FallbackReplacement, UrgencyLevel.Low, title, 3.0, OverlayEntryKind.Status, "HUD"));
            }
            return;
        }

        if (_config.IsTextOnlyMode && Interlocked.Exchange(ref _waitingFirstAudioInt, 0) == 1)
        {
            long reqTick = Volatile.Read(ref _lastTurnRequestTick);
            if (reqTick > 0)
                Volatile.Write(ref _lastTtfaMs, Stopwatch.GetElapsedTime(reqTick).TotalMilliseconds);
        }

        // Coalesce partial overlay updates (~90ms) and abort silent-marker ("NONE") audio before 300ms prime flushes!
        bool isSilentMarker = CompactWireProtocol.IsSilentMarker(currentLine, includeAmbiguousNo: false);
        if (isSilentMarker)
        {
            lock (_audioStateLock)
            {
                _suppressCurrentTurnAudio = true;
                _modelSpeaking = false;
                _pcmPlayer.InterruptAndClear();
            }
            return;
        }
        if (_config.EnableOverlay && !_userPaused && currentLine.Length >= 5)
        {
            long prevTick = Interlocked.Read(ref _lastOverlayPartialTick);
            if (prevTick == 0 || Stopwatch.GetElapsedTime(prevTick).TotalMilliseconds >= 90.0)
            {
                Interlocked.Exchange(ref _lastOverlayPartialTick, Stopwatch.GetTimestamp());
                var title = (sessionProfile ?? _session.ActiveProfile)?.Manifest.DisplayName ?? "HUDMind Live";
                _overlay.ShowMessage(new OverlayCalloutMessage(
                    currentLine,
                    UrgencyLevel.Medium,
                    title,
                    _config.OverlayDurationSeconds,
                    OverlayEntryKind.CoachLive,
                    "Coach"));
            }
        }
    }

    private void HandleTurnComplete(long sessionEpoch, GameProfile? sessionProfile, string spoken)
    {
        bool staleEpoch = _session.CurrentEpoch != sessionEpoch;
        bool isEnOut = CoachingPersonaBuilder.IsEnglish(_config);
        bool effectiveSilent = string.IsNullOrWhiteSpace(spoken) ||
            CompactWireProtocol.IsSilentMarker(spoken, includeAmbiguousNo: true);
        var outGuard = _safety.InspectAndCleanModelOutput(spoken, isEnOut);

        bool allowFlush;
        lock (_audioStateLock)
        {
            allowFlush = !staleEpoch && !effectiveSilent && outGuard.IsSafe && _config.IsCoachVoiceActive && !_userPaused && !_bargeInMuted && !_suppressCurrentTurnAudio;
            if (allowFlush)
                _pcmPlayer.FlushBuffered();
            else
                _pcmPlayer.InterruptAndClear();
            _modelSpeaking = false;
            _bargeInMuted = false;
            _suppressCurrentTurnAudio = false;
        }
        long curTurnSeq = Interlocked.Read(ref _turnSeq);
        long proTurnSeq = Interlocked.Read(ref _proactiveTurnSeq);
        bool wasProactive = Interlocked.Exchange(ref _proactiveTurnInFlight, 0) == 1 && curTurnSeq == proTurnSeq;
        Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
        Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
        Interlocked.Exchange(ref _lastOverlayPartialTick, 0);
        NotifyOverlayVoiceActivity(
            userMicActive: Interlocked.CompareExchange(ref _lastPttActiveState, 0, 0) == 1,
            userLevel01: 0,
            coachVoiceActive: false,
            coachLevel01: 0,
            force: true);

        if (!staleEpoch && effectiveSilent)
        {
            // Proactive turn returned empty/NONE -> enforce dedicated <=2.5s retry delay (immune to spike/multiplier bypass)
            if (wasProactive && _config.LiveAutoCoachSeconds > 0)
            {
                double retryDelay = Math.Min(_config.LiveAutoCoachSeconds, 2.5);
                Interlocked.Exchange(
                    ref _proactiveRetryAfterUtcTicks,
                    _timeProvider.GetUtcNow().AddSeconds(retryDelay).Ticks);
            }
            EmitStats();
            return;
        }

        if (wasProactive)
            Interlocked.Exchange(ref _proactiveRetryAfterUtcTicks, 0);

        if (!staleEpoch && !effectiveSilent)
        {
            var prof = sessionProfile ?? _session.ActiveProfile;
            if (!outGuard.IsSafe)
            {
                if (_config.EnableOverlay && !_userPaused)
                {
                    _overlay.ShowMessage(new OverlayCalloutMessage(
                        outGuard.FallbackReplacement,
                        UrgencyLevel.Low,
                        prof?.Manifest.DisplayName ?? "HUDMind Live",
                        3.5,
                        OverlayEntryKind.Status,
                        "HUD"));
                }
                _onLog?.Invoke($"🛡️ Coach [safety-filter]: \"{outGuard.FallbackReplacement}\"");
                if (outGuard.Category == SafetyCategory.SevereSexualOrIllegal)
                    _ = _backend.ResetSessionStateAsync();
                EmitStats();
                return;
            }

            Interlocked.Increment(ref _sessionCompletedTurns);
            Interlocked.Exchange(ref _lastSpeechCompletedUtcTicks, _timeProvider.GetUtcNow().UtcTicks);
            if (!IsSafetyDeflectionBoilerplate(spoken))
            {
                _session.Memory.RecordObservation("LIVE", spoken);
                _session.Memory.RecordMatchEvent(
                    prof?.Manifest.GameId ?? string.Empty,
                    prof?.Manifest.DisplayName ?? "HUDMind Live",
                    "LIVE",
                    string.Empty,
                    spoken);
            }

            if (_config.EnableOverlay && !_userPaused)
            {
                _overlay.ShowMessage(new OverlayCalloutMessage(
                    spoken,
                    UrgencyLevel.Medium,
                    prof?.Manifest.DisplayName ?? "HUDMind Live",
                    _config.OverlayDurationSeconds,
                    OverlayEntryKind.CoachFinal,
                    "Coach"));
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
        Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
        Interlocked.Exchange(ref _proactiveTurnSeq, 0);
        Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
        Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
        NotifyOverlayVoiceActivity(
            userMicActive: Interlocked.CompareExchange(ref _lastPttActiveState, 0, 0) == 1,
            userLevel01: 0,
            coachVoiceActive: false,
            coachLevel01: 0,
            force: true);
    }

    private string? StartMicIfEnabled()
    {
        lock (_micGateLock)
        {
            if (_disposed || !_micEnabled || _userPaused || !_backend.IsConnected) return null;

            var runCts = Volatile.Read(ref _runCts);
            if (runCts is null || _disposed || !_micEnabled || _userPaused || !_backend.IsConnected)
                return null;
            CancellationToken runCt;
            try { runCt = runCts.Token; }
            catch (ObjectDisposedException) { return null; }
            if (runCt.IsCancellationRequested)
                return null;

            var recCts = Volatile.Read(ref _reconnectCts);
            bool hasActiveSession = false;
            if (recCts is not null)
            {
                try { hasActiveSession = !recCts.IsCancellationRequested; }
                catch (ObjectDisposedException) { hasActiveSession = false; }
            }
            if (!hasActiveSession)
                return null;

            if (_mic.IsCapturing && _micSendChannel is not null && _micSendTask is { IsCompleted: false })
                return null;

            Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
            StopMicAndDrainChannelLocked();

            var micChan = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _micSendChannel = micChan;
            int sendId = Interlocked.Increment(ref _taskSeq);
            var sendDoneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightTasks[sendId] = sendDoneTcs.Task;
            _micSendTask = sendDoneTcs.Task;
            if (Volatile.Read(ref _runCts) != runCts || runCt.IsCancellationRequested || _disposed)
            {
                _inFlightTasks.TryRemove(sendId, out _);
                sendDoneTcs.TrySetResult();
                StopMicAndDrainChannelLocked();
                return null;
            }
            int callerTid = Environment.CurrentManagedThreadId;
            int syncStartActive = 1;
            string? syncStartError = null;
            _ = Task.Run(async () =>
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
                catch (ObjectDisposedException) when (_disposed || runCt.IsCancellationRequested) { }
                catch (Exception ex) { _onLog?.Invoke($"Error mic Live: {SecretRedactor.Redact(ex.Message)}"); }
                finally
                {
                    _inFlightTasks.TryRemove(sendId, out _);
                    sendDoneTcs.TrySetResult();
                }
            });

            bool ok = _mic.TryStart(
                pcmChunk =>
                {
                    if (_disposed || !_backend.IsConnected || _userPaused || !_micEnabled) return;
                    if (!_testMode && !_gameFocused) return;

                    long nowTicks = _timeProvider.GetUtcNow().UtcTicks;
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
                            // Keep sending ~760ms clean digital silence after PTT release (> 520ms Gemini / 260ms OpenAI server VAD silence window)
                            // ensuring 7 x 100ms DMA blocks reach the server reliably even with USB/WinMM callback jitter.
                            long prevPtt = Interlocked.Read(ref _lastPttHeldUtcTicks);
                            if (prevPtt == 0 || (nowTicks - prevPtt) > TimeSpan.FromMilliseconds(760.0).Ticks)
                                return;
                            pttInReleaseTail = true;
                        }
                    }

                    double rms = AudioPcmUtilities.ComputeRms16(pcmChunk);
                    if (pttMode)
                    {
                        int prevPttUi = Interlocked.Exchange(ref _lastPttActiveState, pttHeld ? 1 : 0);
                        if (pttHeld)
                            NotifyOverlayVoiceActivity(true, Math.Clamp(rms / 3200.0, 0.14, 1.0), _modelSpeaking, _modelSpeaking ? 0.55 : 0.0, force: prevPttUi == 0);
                        else if (prevPttUi == 1)
                            NotifyOverlayVoiceActivity(false, 0, _modelSpeaking, _modelSpeaking ? 0.55 : 0.0, force: true);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _lastPttActiveState, 0);
                        if (rms >= 420.0)
                            NotifyOverlayVoiceActivity(true, Math.Clamp(rms / 3200.0, 0.12, 1.0), _modelSpeaking, _modelSpeaking ? 0.55 : 0.0);
                    }
                    bool coachAudible = _modelSpeaking || _pcmPlayer.IsPlayingAudio;

                    // Acoustic Echo Shield: active strictly while coach audio is playing or hanging over speakers.
                    // Never deafens normal open_mic user voice when speakers are silent.
                    if (coachAudible)
                    {
                        if (pttInReleaseTail)
                            return; // Do not trigger false echo barge-in on post-release silence tail

                        if (pttMode && pttHeld)
                        {
                            if (rms < 650.0)
                                return;
                        }
                        else
                        {
                            // Require >=2 consecutive frames (~200ms sustained speech >=1750 RMS) so 100ms loud clicks/bangs NEVER chop audio;
                            // allow single-frame override ONLY on genuine loud player shout (>=2850 RMS).
                            if (rms < 1750.0)
                            {
                                Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
                                return;
                            }
                            bool sustainedTwoFrames = Interlocked.Increment(ref _bargeInConsecutiveFrames) >= 2;
                            bool urgentShoutOverride = rms >= 2850.0;
                            if (!sustainedTwoFrames && !urgentShoutOverride)
                                return;
                        }

                        Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
                        Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
                        Interlocked.Exchange(ref _proactiveTurnSeq, 0);
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
                    else
                    {
                        Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
                    }

                    bool sendDigitalSilence = pttInReleaseTail;
                    if (pttMode)
                    {
                        if (pttHeld || pttInReleaseTail)
                            Interlocked.Exchange(ref _lastMicVoiceUtcTicks, nowTicks);

                        if (pttHeld)
                        {
                            Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                            Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                            TriggerVoiceMotionSyncIfDue(nowTicks);
                        }
                    }
                    else
                    {
                        if (rms >= 540.0)
                        {
                            Interlocked.Exchange(ref _lastMicVoiceUtcTicks, nowTicks);
                            Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                            Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                            TriggerVoiceMotionSyncIfDue(nowTicks);
                        }
                        else
                        {
                            long lastMic = Interlocked.Read(ref _lastMicVoiceUtcTicks);
                            if (lastMic == 0)
                                return;
                            double quietMs = TimeSpan.FromTicks(nowTicks - lastMic).TotalMilliseconds;
                            if (quietMs > 760.0)
                                return;
                            // Forward raw tail briefly for trailing consonants (0..140ms), then pure digital zeros (140..760ms)
                            // ensuring 6 x 100ms DMA zero blocks (> 520ms) close Gemini Live END_SENSITIVITY_LOW cleanly!
                            if (quietMs > 140.0)
                                sendDigitalSilence = true;
                        }
                    }

                    micChan.Writer.TryWrite(sendDigitalSilence ? new byte[pcmChunk.Length] : pcmChunk);
                },
                err =>
                {
                    if (Environment.CurrentManagedThreadId == callerTid &&
                        Interlocked.CompareExchange(ref syncStartActive, 0, 0) == 1)
                        Volatile.Write(ref syncStartError, err);
                    else
                        _onLog?.Invoke(err);
                });

            Interlocked.Exchange(ref syncStartActive, 0);
            var earlyError = Volatile.Read(ref syncStartError);
            if (ok)
                return earlyError is { Length: > 0 }
                    ? earlyError
                    : (_config.IsPushToTalkMode
                        ? $"🎤 Micrófono 16kHz en Push-to-Talk (mantené '{_config.PushToTalkKey}' para hablar)."
                        : "🎤 Micrófono 16kHz con supresión de eco activo — hablá cuando quieras.");

            StopMicAndDrainChannelLocked();
            return earlyError;
        }
    }

    private void StopMicAndDrainChannel()
    {
        lock (_micGateLock)
            StopMicAndDrainChannelLocked();
    }

    private void StopMicAndDrainChannelLocked()
    {
        if (Interlocked.Exchange(ref _lastPttActiveState, 0) == 1)
            NotifyOverlayVoiceActivity(false, 0, _modelSpeaking, _modelSpeaking ? 0.55 : 0.0, force: true);
        _mic.Stop();
        var ch = Interlocked.Exchange(ref _micSendChannel, null);
        ch?.Writer.TryComplete();
    }

    private void TriggerVoiceMotionSyncIfDue(long nowTicks)
    {
        if (_disposed || !_backend.IsConnected || _modelSpeaking)
            return;

        long prev = Interlocked.Read(ref _lastMicVisualBurstTick);
        if (prev > 0 && (nowTicks - prev) < TimeSpan.FromSeconds(1.8).Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastMicVisualBurstTick, nowTicks, prev) != prev)
            return;

        TrackFireAndForget(async ct =>
        {
            if (_disposed || !_backend.IsConnected || _modelSpeaking)
                return;
            _ = CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _captureSeq));

            var seq = _session.FrameRingBuffer.SnapshotRecent(DateTimeOffset.UtcNow, maxAgeSeconds: 4.5, maxCount: 4);
            foreach (var sample in seq)
            {
                if (ct.IsCancellationRequested || !_backend.IsConnected || _modelSpeaking)
                    break;
                Interlocked.Increment(ref _framesSent);
                await _backend.SendVideoFrameAsync(sample.JpegBytes, ct);
            }
        });
    }

    private static string BuildEnrichedLiveQuestionPrompt(
        string question,
        string topicGuide,
        string timeline,
        string sequenceNote,
        int panelCount,
        bool isEn)
    {
        var sb = new System.Text.StringBuilder(question.Length + topicGuide.Length + 280);
        if (panelCount >= 2)
        {
            var note = string.IsNullOrWhiteSpace(sequenceNote) ? string.Empty : $"{sequenceNote} ";
            sb.Append(isEn
                ? $"{note}[Video-like sequence attached ({panelCount} frames); trace trajectory across ALL frames before confirming rapid mechanics.] "
                : $"{note}[Secuencia tipo video adjunta ({panelCount} cuadros); seguí la trayectoria cuadro por cuadro antes de confirmar mecánicas rápidas.] ");
        }
        if (!string.IsNullOrWhiteSpace(topicGuide))
            sb.Append(isEn ? $"Relevant game rules: {topicGuide}. " : $"Mecánicas/reglas del juego: {topicGuide}. ");
        if (!string.IsNullOrWhiteSpace(timeline))
            sb.Append(isEn ? $"Match context: {timeline}. " : $"Contexto de partida: {timeline}. ");
        sb.Append(isEn
            ? $"Answer honestly based ONLY on what you clearly see (admit if obscured/unclear, never guess yes). Question: {question}"
            : $"Respondé con 100% honestidad SOLO por lo que ves claro (si un contacto o detalle no se distingue, admitilo sin inventar). Pregunta: {question}");
        return sb.ToString();
    }

    private (int W, int H, int Quality) ResolveTargetResolution() =>
        (_config.TokenEfficiencyMode ?? "balanced").ToLowerInvariant() switch
        {
            "eco" => (960, 540, Math.Max(_config.ImageCompressionQuality, 76)),
            "high" => (1440, 810, Math.Max(_config.ImageCompressionQuality, 84)),
            _ => (1280, 720, Math.Max(_config.ImageCompressionQuality, 80))
        };

    private CapturedFrameData? CaptureSharpFrame(bool force, long frameId)
    {
        ulong? prevHash;
        lock (_hashLock) prevHash = _lastSentHash;

        var (w, h, q) = ResolveTargetResolution();
        int phash = _session.ActiveProfile?.Manifest.PhashThreshold ?? 6;
        var region = _testMode ? null : _windowDetector.GetForegroundClientRect();
        bool macroDue = !force && _session.FrameRingBuffer.NeedsMacroKeyframe(DateTimeOffset.UtcNow);

        var (frame, skipped) = _capture.CaptureIfChanged(
            _config.CaptureMonitor,
            region,
            targetWidth: w,
            targetHeight: h,
            jpegQuality: q,
            frameId: frameId,
            lastAcceptedHash: prevHash,
            phashThreshold: (force || macroDue) ? 0 : Math.Min(phash, 5),
            isForced: force);

        _session.ObserveCaptureHealth(_config);

        if (skipped || frame is null)
            return null;

        lock (_hashLock)
        {
            _lastSentHash = frame.PerceptualHash;
            Volatile.Write(ref _lastCaptureMs, frame.CaptureMs + frame.EncodeMs);
        }
        _session.FrameRingBuffer.Record(frame, forceReplaceTailIfTooClose: force);
        return frame;
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
                        var switchOutcome = _session.SyncForegroundProfile(_config, "Coach Live ACTIVO — observando");
                        if (!switchOutcome.GameInForeground)
                        {
                            _gameFocused = false;
                            await Task.Delay(700, ct);
                            continue;
                        }

                        _gameFocused = true;
                        if (switchOutcome.Switched)
                        {
                            Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, 0);
                            Interlocked.Exchange(ref _lastSpeechCompletedUtcTicks, 0);
                            Interlocked.Exchange(ref _proactiveRetryAfterUtcTicks, 0);
                            Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
                            Interlocked.Exchange(ref _sessionCompletedTurns, 0);
                            Interlocked.Exchange(ref _suppressNextConnectedOverlay, 0);
                            Interlocked.Exchange(ref _proactiveTurnSeq, 0);
                            lock (_followUpLock) _pendingLiveFollowUpTopic = null;
                            lock (_hashLock) _lastSentHash = null;
                            _localPhaseGate?.Reset();
                            EmitStats();

                            var matched = switchOutcome.CurrentProfile!;
                            // Always realign LifecycleLoopAsync sessionEpoch & system instruction after any profile switch (including null -> universal)
                            RefreshLivePersona(matched.Manifest.DisplayName);
                            break;
                        }
                    }

                    // Sample ~10 FPS continuously into local OBS-style dual-tier RAM replay buffer (0 API cost)
                    var captured = CaptureSharpFrame(force: false, frameId: ++frameCounter);
                    bool blackScreenStall = _capture.ConsecutiveBlackFrames >= 2;
                    byte[]? evalJpeg = blackScreenStall
                        ? null
                        : (captured?.JpegBytes ?? _session.FrameRingBuffer.LatestSample?.JpegBytes);

                    if (evalJpeg is { Length: > 0 })
                    {
                        bool isEnEval = CoachingPersonaBuilder.IsEnglish(_config);
                        var activeProf = _session.ActiveProfile;
                        string rawTelemetry = _localPhaseGate is not null && activeProf is not null
                            ? await _localPhaseGate.ReadTelemetryAsync(activeProf, ct)
                            : string.Empty;
                        var tactical = _session.EvaluateTacticalSignal(rawTelemetry, activeProf, _config, isEnEval);
                        if (!_testMode && tactical.MatchEnded && !_modelSpeaking && !_pcmPlayer.IsPlayingAudio)
                        {
                            _onLog?.Invoke("Partida finalizada — limpiando contexto Live para la próxima.");
                            _ = _backend.ResetSessionStateAsync();
                            RefreshLivePersona("nueva partida");
                            break;
                        }

                        double autoSec = _config.LiveAutoCoachSeconds;
                        long nowTicks = _timeProvider.GetUtcNow().UtcTicks;
                        long lastMicTicks = Interlocked.Read(ref _lastMicVoiceUtcTicks);
                        long lastNudgeTicks = Interlocked.Read(ref _lastAutoNudgeUtcTicks);
                        long lastSpeechDoneTicks = Interlocked.Read(ref _lastSpeechCompletedUtcTicks);
                        long retryAfterTicks = Interlocked.Read(ref _proactiveRetryAfterUtcTicks);
                        bool inRetryBackoff = retryAfterTicks > 0 && nowTicks < retryAfterTicks;

                        bool pttHeldNow = _micEnabled && _config.IsPushToTalkMode && (_isPttKeyPressed?.Invoke() ?? false);
                        bool userRecentlySpoke = pttHeldNow || (lastMicTicks > 0 && (nowTicks - lastMicTicks) < TimeSpan.FromSeconds(3.0).Ticks);
                        double pendingShieldSec = Math.Min(2.4, Math.Max(0.25, autoSec * 0.6));
                        long reqTick = Interlocked.Read(ref _lastTurnRequestTick);
                        bool waitingPending = Volatile.Read(ref _waitingFirstAudioInt) == 1
                            && reqTick > 0
                            && Stopwatch.GetElapsedTime(reqTick).TotalSeconds < pendingShieldSec;
                        bool canVoiceTurn = autoSec > 0 && !_modelSpeaking && !_pcmPlayer.IsPlayingAudio && !waitingPending && !userRecentlySpoke && !inRetryBackoff;

                        if (!_testMode && Volatile.Read(ref _sessionCompletedTurns) >= 18 &&
                            !_modelSpeaking && !_pcmPlayer.IsPlayingAudio && !waitingPending && !userRecentlySpoke)
                        {
                            Interlocked.Exchange(ref _suppressNextConnectedOverlay, 1);
                            _ = _backend.ResetSessionStateAsync();
                            RefreshLivePersona("contexto fresco");
                            break;
                        }

                        double motionEnergy = _session.FrameRingBuffer.ComputeRecentMotionEnergy();
                        bool visualSpike = motionEnergy >= 0.58 && _session.FrameRingBuffer.MacroCount >= 2;

                        // Queue natural follow-up strictly for verified tactical alerts occurring mid-utterance (never generic camera pans)
                        if (tactical.TriggerImmediate && autoSec > 0 && !tactical.ShouldSilence &&
                            (_modelSpeaking || _pcmPlayer.IsPlayingAudio || waitingPending))
                        {
                            lock (_followUpLock)
                            {
                                _pendingLiveFollowUpTopic = !string.IsNullOrWhiteSpace(tactical.DeterministicFact)
                                    ? tactical.DeterministicFact
                                    : (isEnEval ? "tactical shift" : "cambio táctico");
                                _pendingLiveFollowUpUrgent = true;
                                _pendingLiveFollowUpExpiresUtc = _timeProvider.GetUtcNow().AddSeconds(2.0);
                            }
                        }

                        // Peek pending follow-up without dropping it until committed or expired
                        string? followUpTopic = null;
                        bool followUpUrgent = false;
                        if (canVoiceTurn)
                        {
                            lock (_followUpLock)
                            {
                                if (_pendingLiveFollowUpTopic is not null)
                                {
                                    if (_timeProvider.GetUtcNow() <= _pendingLiveFollowUpExpiresUtc)
                                    {
                                        followUpTopic = _pendingLiveFollowUpTopic;
                                        followUpUrgent = _pendingLiveFollowUpUrgent;
                                    }
                                    else
                                    {
                                        _pendingLiveFollowUpTopic = null;
                                    }
                                }
                            }
                        }

                        double scaledAutoSec = autoSec * tactical.CooldownMultiplier;
                        double minPostSpeechGapSec = followUpTopic is not null
                            ? 1.0
                            : Math.Min(3.5, Math.Max(1.8, scaledAutoSec * 0.45));
                        bool postSpeechReady = lastSpeechDoneTicks == 0 ||
                            (nowTicks - lastSpeechDoneTicks) >= TimeSpan.FromSeconds(minPostSpeechGapSec).Ticks;

                        bool regularDue = canVoiceTurn && postSpeechReady &&
                            (lastNudgeTicks == 0 || (nowTicks - lastNudgeTicks) >= TimeSpan.FromSeconds(scaledAutoSec).Ticks);
                        double eventFloorSec = visualSpike ? Math.Max(4.2, autoSec * 0.60) : Math.Max(3.5, autoSec * 0.50);
                        bool eventDue = canVoiceTurn && postSpeechReady && (tactical.TriggerImmediate || visualSpike || followUpTopic is not null) &&
                            (lastNudgeTicks == 0 || (nowTicks - lastNudgeTicks) >= TimeSpan.FromSeconds(followUpTopic is not null ? 1.2 : eventFloorSec).Ticks);
                        bool retryDue = canVoiceTurn && retryAfterTicks > 0 && nowTicks >= retryAfterTicks;
                        bool shouldNudge = (regularDue || eventDue || retryDue) && !tactical.ShouldSilence;

                        bool sentAsTurn = false;
                        if (shouldNudge)
                        {
                            Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, nowTicks);
                            Interlocked.Exchange(ref _proactiveRetryAfterUtcTicks, 0);

                            // Force fresh capture BEFORE OCR gate so we never classify stale pre-loading/pre-menu pixels
                            captured ??= CaptureSharpFrame(force: true, frameId: ++frameCounter);
                            if (_capture.ConsecutiveBlackFrames >= 2)
                            {
                                EmitStats();
                                continue;
                            }

                            byte[] currentJpeg = captured?.JpegBytes ?? evalJpeg;
                            bool skipProactive = false;
                            if (_localPhaseGate is not null && activeProf is not null)
                            {
                                var snap = await _localPhaseGate.ExtractAsync(currentJpeg, activeProf, ct);
                                if (snap.Phase is "MENU" or "REPLAY" or "LOADING" or "DESKTOP")
                                {
                                    skipProactive = true;
                                    lock (_followUpLock) _pendingLiveFollowUpTopic = null;
                                    _onLog?.Invoke($"Silencio local [{snap.Phase}] (sin molestar en menú/replay).");
                                }
                            }

                            if (!skipProactive)
                            {
                                bool isUrgentTurn = tactical.TriggerImmediate || visualSpike || followUpUrgent;
                                double audioBusyThreshold = isUrgentTurn ? 0.72 : 0.42;
                                if (_config.IsCoachVoiceActive && _audioMonitor.SampleOtherAudio().OtherSessionsPeak01 >= audioBusyThreshold)
                                {
                                    double maxWaitMs = isUrgentTurn ? 140.0 : 220.0;
                                    var gapSw = Stopwatch.StartNew();
                                    while (!ct.IsCancellationRequested && gapSw.Elapsed.TotalMilliseconds < maxWaitMs)
                                    {
                                        await Task.Delay(55, ct);
                                        if (_audioMonitor.SampleOtherAudio().OtherSessionsPeak01 < audioBusyThreshold)
                                            break;
                                    }
                                }

                                var (_, _, cfgQuality) = ResolveTargetResolution();
                                await _turnGate.WaitAsync(ct);
                                try
                                {
                                    long latestReqTick = Interlocked.Read(ref _lastTurnRequestTick);
                                    bool turnSupersededOrAwaitingAudio = latestReqTick != reqTick ||
                                        (Volatile.Read(ref _waitingFirstAudioInt) == 1 && latestReqTick > 0 &&
                                         Stopwatch.GetElapsedTime(latestReqTick).TotalSeconds < pendingShieldSec);
                                    bool micStartedDuringWait = (_micEnabled && _config.IsPushToTalkMode && (_isPttKeyPressed?.Invoke() ?? false))
                                        || Interlocked.Read(ref _lastMicVoiceUtcTicks) > lastMicTicks
                                        || turnSupersededOrAwaitingAudio;
                                    if (_disposed || _userPaused || !_backend.IsConnected || _modelSpeaking || _pcmPlayer.IsPlayingAudio ||
                                        micStartedDuringWait || Interlocked.Read(ref _lastAutoNudgeUtcTicks) != nowTicks)
                                    {
                                        // Superseded cleanly by pause, disconnect, speech, or user chat/F10
                                    }
                                    else
                                    {
                                        // Re-capture [NOW] right before sending if OCR/gap wait aged the frame (>140ms)
                                        if ((_timeProvider.GetUtcNow() - (captured?.CapturedAtUtc ?? DateTimeOffset.MinValue)).TotalMilliseconds >= 140.0)
                                        {
                                            var freshestNow = CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _captureSeq));
                                            if (freshestNow is not null)
                                            {
                                                captured = freshestNow;
                                                currentJpeg = freshestNow.JpegBytes;
                                            }
                                        }

                                        var clip = _session.FrameRingBuffer.BuildVisualClipPayload(
                                            _storyboardComposer,
                                            captured,
                                            maxFrames: 3,
                                            maxAgeSeconds: 2.6,
                                            jpegQuality: Math.Max(cfgQuality, 82),
                                            isEn: isEnEval);

                                        sentAsTurn = true;
                                        lock (_followUpLock)
                                        {
                                            if (_pendingLiveFollowUpTopic is not null && _timeProvider.GetUtcNow() <= _pendingLiveFollowUpExpiresUtc)
                                                followUpTopic = _pendingLiveFollowUpTopic;
                                            _pendingLiveFollowUpTopic = null;
                                        }
                                        long turnSeq = Interlocked.Increment(ref _turnSeq);
                                        Interlocked.Exchange(ref _proactiveTurnSeq, turnSeq);
                                        Interlocked.Exchange(ref _proactiveTurnInFlight, 1);
                                        Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                                        Interlocked.Exchange(ref _waitingFirstAudioInt, 1);

                                        string factNote = string.IsNullOrWhiteSpace(tactical.DeterministicFact)
                                            ? string.Empty
                                            : (isEnEval
                                                ? $" Verified fact: {tactical.DeterministicFact}."
                                                : $" Dato real: {tactical.DeterministicFact}.");
                                        string timelineNote = _session.Memory.FormatVisualTimeline(isEn: isEnEval);
                                        string ctxNote = string.IsNullOrWhiteSpace(timelineNote) ? string.Empty
                                            : (isEnEval ? $" Match context: {timelineNote}." : $" Contexto: {timelineNote}.");
                                        string motionNote = visualSpike
                                            ? (isEnEval ? " High-action play burst detected." : " Pico de acción detectado en pantalla.")
                                            : string.Empty;
                                        string followUpNote = followUpTopic is not null
                                            ? (isEnEval
                                                ? $" Continue naturally right after your last tip using a brief conversational connector ('and watch out...', 'and also...') focused on {followUpTopic}."
                                                : $" Continuá natural justo después de tu comentario anterior usando un conector corto ('y ojo que...', 'y aparte...') sobre {followUpTopic}.")
                                            : string.Empty;
                                        string seqPrefix = clip.PanelCount >= 2 && !string.IsNullOrWhiteSpace(clip.SequenceNote)
                                            ? $"{clip.SequenceNote} "
                                            : string.Empty;
                                        string prompt = isEnEval
                                            ? $"{seqPrefix}{factNote}{ctxNote}{motionNote}{followUpNote} Live OBS play active. Focus strictly on [NOW] and speak 1 ultra-short predictive adjustment (5-9 words) for the next 1-2s; never narrate old finished plays and never speak 'NONE' aloud."
                                            : $"{seqPrefix}{factNote}{ctxNote}{motionNote}{followUpNote} Jugada en vivo activa. Mirá [AHORA] y tirá 1 ajuste táctico bien corto (5 a 9 palabras) para los próximos 1-2s; no relates jugadas pasadas ni digas 'NONE'.";

                                        Interlocked.Increment(ref _framesSent);
                                        if (_backend.SupportsMultiFrameTurn && clip.Frames.Count > 1)
                                            await _backend.SendFrameSequenceAndPromptTurnAsync(clip.Frames, prompt, ct);
                                        else
                                            await _backend.SendImageAndPromptTurnAsync(clip.FallbackSingleJpeg ?? currentJpeg, prompt, ct);
                                    }
                                }
                                catch
                                {
                                    Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
                                    Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
                                    throw;
                                }
                                finally
                                {
                                    try { _turnGate.Release(); } catch (ObjectDisposedException) { }
                                }
                            }
                        }

                        // Throttle passive WebSocket JPEG streaming to ~1 FPS max while pausing during active speech or DMA playback
                        double fps = _session.ActiveProfile?.Manifest.CaptureFps ?? 1.5;
                        int wsPeriodMs = Math.Clamp((int)Math.Round(1000.0 / Math.Clamp(fps, 0.5, 1.5)), 850, 1800);
                        long prevWsTick = Interlocked.Read(ref _lastWebSocketFrameTick);
                        double sinceWsMs = prevWsTick == 0 ? double.MaxValue : Stopwatch.GetElapsedTime(prevWsTick).TotalMilliseconds;
                        bool wsDue = prevWsTick == 0 || sinceWsMs >= wsPeriodMs;
                        if (!sentAsTurn && !_modelSpeaking && !_pcmPlayer.IsPlayingAudio && wsDue && captured is not null)
                        {
                            Interlocked.Exchange(ref _lastWebSocketFrameTick, Stopwatch.GetTimestamp());
                            Interlocked.Increment(ref _framesSent);
                            await _backend.SendVideoFrameAsync(captured.JpegBytes, ct);
                        }

                        EmitStats();
                    }
                }

                // 100ms local OBS ring-buffer cadence (~10 FPS in RAM) keeps fluid clip memory ready for voice, F10 & chat
                try { await _wakeSignal.WaitAsync(TimeSpan.FromMilliseconds(100), ct); }
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
            _session.ActiveProfile?.Manifest.DisplayName ?? (_testMode ? "Universal" : "(esperando juego)"),
            _userPaused
        ));
    }

    private static bool IsSafetyDeflectionBoilerplate(string spoken) =>
        spoken.Contains("me concentro en el", StringComparison.OrdinalIgnoreCase) ||
        spoken.Contains("focus on the match", StringComparison.OrdinalIgnoreCase) ||
        spoken.Contains("no hablo ", StringComparison.OrdinalIgnoreCase) ||
        spoken.Contains("soy un coach de", StringComparison.OrdinalIgnoreCase) ||
        spoken.Contains("only here to coach", StringComparison.OrdinalIgnoreCase);

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

            _session.FlushMatchSnapshot(resetMatchLog: false, _config);
            _session.OnStop();
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
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0) return;
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
