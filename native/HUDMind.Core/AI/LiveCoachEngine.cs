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
    private readonly MatchSessionCoordinator _session;

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
    private int _bargeInConsecutiveFrames;
    private long _lastOverlayPartialTick;
    private long _lastAutoNudgeUtcTicks;
    private long _lastMicVoiceUtcTicks;
    private long _lastPttHeldUtcTicks;
    private long _lastWebSocketFrameTick;
    private long _lastMicVisualBurstTick;
    private long _proactivePendingUntilUtcTicks;
    private int _proactiveTurnInFlight;

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
        Func<GameProfile, AssembledCoachContext>? resolveContextForProfile = null,
        ITemporalStoryboardComposer? storyboardComposer = null,
        ICoachSafetyPipeline? safetyPipeline = null)
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
    }

    public (string GameName, string Digest, int Count) GetMatchSessionDigest(bool isEn = false) =>
        _session.GetMatchSessionDigest(isEn);

    public MatchSessionSnapshot? CaptureMatchSnapshot(bool isEn = false) =>
        _session.CaptureMatchSnapshot(isEn);

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
            if (_config.IsCoachVoiceActive)
                _pcmPlayer.PreOpenDevice();
            _mic.SetDevice(_config.AudioInputDeviceName);
            _userPaused = false;
            _config.RuntimeTiltRisk = false;
            _micEnabled = _config.IsMicActive;
            _bargeInMuted = false;
            _suppressCurrentTurnAudio = false;
            Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, 0);
            Interlocked.Exchange(ref _lastPttHeldUtcTicks, 0);
            Interlocked.Exchange(ref _lastWebSocketFrameTick, 0);
            Interlocked.Exchange(ref _lastMicVisualBurstTick, 0);
            Interlocked.Exchange(ref _proactivePendingUntilUtcTicks, 0);
            Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
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

        bool isEnMsg = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
        var inputGuard = _safety.EvaluateUserInput(msg, isEnMsg);
        if (!inputGuard.IsAllowed)
        {
            _onLog?.Invoke($"💬 Vos: \"{msg}\"");
            _onLog?.Invoke($"🛡️ Coach [safety-guard]: \"{inputGuard.SafeRedirectReply}\"");
            if (_config.EnableOverlay && !_userPaused)
            {
                _overlay.ShowCallout(
                    inputGuard.SafeRedirectReply,
                    UrgencyLevel.Low,
                    _session.ActiveProfile?.Manifest.DisplayName ?? "HUDMind Live",
                    Math.Min(_config.OverlayDurationSeconds, 3.5));
            }
            return true;
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

                    var freshFrame = attachCurrentScreen
                        ? CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _framesSent))
                        : null;
                    var (_, _, cfgQuality) = ResolveTargetResolution();
                    bool isEn = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
                    var clip = freshFrame is not null
                        ? _session.FrameRingBuffer.BuildVisualClipPayload(
                            _storyboardComposer,
                            freshFrame,
                            maxFrames: 6,
                            maxAgeSeconds: 8.0,
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
                        if (clip is { Frames.Count: > 0 })
                        {
                            if (_backend.SupportsMultiFrameTurn && clip.Frames.Count > 1)
                                await _backend.SendFrameSequenceAndPromptTurnAsync(clip.Frames, enrichedPrompt, ct);
                            else if (clip.FallbackSingleJpeg is { Length: > 0 } singleBytes)
                                await _backend.SendImageAndPromptTurnAsync(singleBytes, enrichedPrompt, ct);
                            else
                                await _backend.SendTextTurnAsync(enrichedPrompt, ct);
                        }
                        else
                        {
                            await _backend.SendTextTurnAsync(enrichedPrompt, ct);
                        }
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
        var title = _session.ActiveProfile?.Manifest.DisplayName ?? "HUDMind Live";

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
        if (!_testMode && _session.ActiveProfile is null)
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
            var freshFrame = CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _framesSent));
            if (freshFrame is null)
                return;

            var (_, _, cfgQuality) = ResolveTargetResolution();
            bool isEn = CoachingPersonaBuilder.IsEnglish(_config);
            var clip = _session.FrameRingBuffer.BuildVisualClipPayload(
                _storyboardComposer,
                freshFrame,
                maxFrames: 6,
                maxAgeSeconds: 8.0,
                jpegQuality: Math.Max(cfgQuality, 84),
                isEn: isEn);

            await _turnGate.WaitAsync(ct);
            try
            {
                if (!_backend.IsConnected) return;
                Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

                string seqPrefix = clip.PanelCount >= 2 && !string.IsNullOrWhiteSpace(clip.SequenceNote)
                    ? $"{clip.SequenceNote} "
                    : string.Empty;
                string prompt = isEn
                    ? $"{seqPrefix}Give 1 predictive, crystal-clear tactical callout (8-14 words) for the next 2 seconds based ONLY on clearly visible pixels."
                    : $"{seqPrefix}Dame 1 sola llamada táctica predictiva y clara (8 a 14 palabras) para los próximos 2 segundos basándote SOLO en lo que se ve nítido.";

                if (_backend.SupportsMultiFrameTurn && clip.Frames.Count > 1)
                    await _backend.SendFrameSequenceAndPromptTurnAsync(clip.Frames, prompt, ct);
                else
                    await _backend.SendImageAndPromptTurnAsync(clip.FallbackSingleJpeg ?? freshFrame.JpegBytes, prompt, ct);
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
                                _session.ActiveProfile?.Manifest.DisplayName ?? "HUDMind Live",
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
                Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
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
                _overlay.ShowCallout(outGuard.FallbackReplacement, UrgencyLevel.Low, title, 3.0);
            }
            return;
        }

        if (_config.IsTextOnlyMode && Interlocked.Exchange(ref _waitingFirstAudioInt, 0) == 1)
        {
            long reqTick = Volatile.Read(ref _lastTurnRequestTick);
            if (reqTick > 0)
                Volatile.Write(ref _lastTtfaMs, Stopwatch.GetElapsedTime(reqTick).TotalMilliseconds);
        }

        // Coalesce partial overlay updates (~90ms) to keep 0% GPU CPU rasterization ultra-light!
        bool isNoneMarker = currentLine.Trim().Trim('.', '!', ' ').Equals("NONE", StringComparison.OrdinalIgnoreCase);
        if (!isNoneMarker && _config.EnableOverlay && !_userPaused && currentLine.Length >= 5)
        {
            long prevTick = Interlocked.Read(ref _lastOverlayPartialTick);
            if (prevTick == 0 || Stopwatch.GetElapsedTime(prevTick).TotalMilliseconds >= 90.0)
            {
                Interlocked.Exchange(ref _lastOverlayPartialTick, Stopwatch.GetTimestamp());
                var title = (sessionProfile ?? _session.ActiveProfile)?.Manifest.DisplayName ?? "HUDMind Live";
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
        bool staleEpoch = _session.CurrentEpoch != sessionEpoch;
        bool isEnOut = CoachingPersonaBuilder.IsEnglish(_config);
        var outGuard = _safety.InspectAndCleanModelOutput(spoken, isEnOut);

        bool allowFlush;
        lock (_audioStateLock)
        {
            allowFlush = !staleEpoch && outGuard.IsSafe && _config.IsCoachVoiceActive && !_userPaused && !_bargeInMuted && !_suppressCurrentTurnAudio;
            if (allowFlush)
                _pcmPlayer.FlushBuffered();
            else
                _pcmPlayer.InterruptAndClear();
            _modelSpeaking = false;
            _bargeInMuted = false;
            _suppressCurrentTurnAudio = false;
        }
        bool wasProactive = Interlocked.Exchange(ref _proactiveTurnInFlight, 0) == 1;
        Interlocked.Exchange(ref _proactivePendingUntilUtcTicks, 0);
        Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
        Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
        Interlocked.Exchange(ref _lastOverlayPartialTick, 0);

        bool effectiveSilent = string.IsNullOrWhiteSpace(spoken) ||
            spoken.Trim().Trim('.', '!', ' ').Equals("NONE", StringComparison.OrdinalIgnoreCase);
        if (!staleEpoch && effectiveSilent)
        {
            // Proactive turn returned empty/NONE -> schedule retry cleanly within <=2.5s instead of sleeping full cooldown
            if (wasProactive && _config.LiveAutoCoachSeconds > 0)
            {
                double retryDelay = Math.Min(_config.LiveAutoCoachSeconds, 2.5);
                long backoffAgo = DateTimeOffset.UtcNow.UtcTicks - TimeSpan.FromSeconds(_config.LiveAutoCoachSeconds - retryDelay).Ticks;
                Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, backoffAgo);
            }
            EmitStats();
            return;
        }

        if (!staleEpoch && !effectiveSilent)
        {
            var prof = sessionProfile ?? _session.ActiveProfile;
            if (!outGuard.IsSafe)
            {
                if (_config.EnableOverlay && !_userPaused)
                {
                    _overlay.ShowCallout(
                        outGuard.FallbackReplacement,
                        UrgencyLevel.Low,
                        prof?.Manifest.DisplayName ?? "HUDMind Live",
                        3.5);
                }
                _onLog?.Invoke($"🛡️ Coach [safety-filter]: \"{outGuard.FallbackReplacement}\"");
                if (outGuard.Category == SafetyCategory.SevereSexualOrIllegal)
                    _ = _backend.ResetSessionStateAsync();
                EmitStats();
                return;
            }

            _session.Memory.RecordObservation("LIVE", spoken);
            _session.Memory.RecordMatchEvent(
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
        Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
        Interlocked.Exchange(ref _proactivePendingUntilUtcTicks, 0);
        Interlocked.Exchange(ref _waitingFirstAudioInt, 0);
        Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
    }

    private void StartMicIfEnabled()
    {
        lock (_micGateLock)
        {
            if (_disposed || !_micEnabled || _userPaused || !_backend.IsConnected) return;

            Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
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
                            // Keep sending ~650ms clean digital silence after PTT release (> 520ms Gemini / 260ms OpenAI server VAD silence window)
                            // so server VAD detects end-of-utterance cleanly without ambient noise resetting the timer!
                            long prevPtt = Interlocked.Read(ref _lastPttHeldUtcTicks);
                            if (prevPtt == 0 || (nowTicks - prevPtt) > TimeSpan.FromMilliseconds(650.0).Ticks)
                                return;
                            pttInReleaseTail = true;
                        }
                    }

                    double rms = AudioPcmUtilities.ComputeRms16(pcmChunk);
                    bool coachAudible = _modelSpeaking || _pcmPlayer.IsPlayingAudio;
                    bool awaitingProactiveAudio = !pttMode
                        && Volatile.Read(ref _waitingFirstAudioInt) == 1
                        && nowTicks <= Interlocked.Read(ref _proactivePendingUntilUtcTicks);

                    // Acoustic Echo & Noise Shield: ignore speaker bleed, ambient fans & 100ms slams while coach speaks or prepares proactive audio
                    if (coachAudible || awaitingProactiveAudio)
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
                            // allow single-frame override ONLY on genuine loud player shout (>=2850 RMS) while coach is actively speaking.
                            if (rms < 1750.0)
                            {
                                Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
                                return;
                            }
                            bool sustainedTwoFrames = Interlocked.Increment(ref _bargeInConsecutiveFrames) >= 2;
                            bool urgentShoutOverride = coachAudible && rms >= 2850.0;
                            if (!sustainedTwoFrames && !urgentShoutOverride)
                                return;
                        }

                        Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
                        Interlocked.Exchange(ref _proactivePendingUntilUtcTicks, 0);
                        Interlocked.Exchange(ref _proactiveTurnInFlight, 0);
                        if (coachAudible)
                        {
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
                    }
                    else
                    {
                        Interlocked.Exchange(ref _bargeInConsecutiveFrames, 0);
                    }

                    if (pttMode)
                    {
                        if (pttHeld)
                        {
                            Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                            Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                            TriggerVoiceMotionSyncIfDue(nowTicks);
                        }
                    }
                    else
                    {
                        if (rms >= 460.0)
                        {
                            Interlocked.Exchange(ref _lastMicVoiceUtcTicks, nowTicks);
                            Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                            Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                            TriggerVoiceMotionSyncIfDue(nowTicks);
                        }
                        else
                        {
                            long lastMic = Interlocked.Read(ref _lastMicVoiceUtcTicks);
                            if (lastMic == 0 || (nowTicks - lastMic) > TimeSpan.FromMilliseconds(320.0).Ticks)
                                return;
                        }
                    }

                    micChan.Writer.TryWrite(pttInReleaseTail ? new byte[pcmChunk.Length] : pcmChunk);
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
            var latest = CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _framesSent));
            if (latest is not null)
                _session.FrameRingBuffer.Record(latest, forceReplaceTailIfTooClose: true);

            var seq = _session.FrameRingBuffer.SnapshotRecent(DateTimeOffset.UtcNow, maxAgeSeconds: 4.5, maxCount: 4);
            foreach (var sample in seq)
            {
                if (ct.IsCancellationRequested || !_backend.IsConnected || _modelSpeaking)
                    break;
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
                            lock (_hashLock) _lastSentHash = null;
                            _localPhaseGate?.Reset();
                            EmitStats();

                            var matched = switchOutcome.CurrentProfile!;
                            if (switchOutcome.PreviousProfile is not null || !string.Equals(matched.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase))
                            {
                                RefreshLivePersona(matched.Manifest.DisplayName);
                                break;
                            }
                        }
                    }

                    // Sample ~10 FPS continuously into local OBS-style dual-tier RAM replay buffer (0 API cost)
                    var captured = CaptureSharpFrame(force: false, frameId: ++frameCounter);
                    byte[]? evalJpeg = captured?.JpegBytes ?? _session.FrameRingBuffer.LatestSample?.JpegBytes;
                    if (evalJpeg is { Length: > 0 })
                    {
                        bool isEnEval = CoachingPersonaBuilder.IsEnglish(_config);
                        var activeProf = _session.ActiveProfile;
                        string rawTelemetry = _localPhaseGate is not null && activeProf is not null
                            ? await _localPhaseGate.ReadTelemetryAsync(activeProf, ct)
                            : string.Empty;
                        var tactical = _session.EvaluateTacticalSignal(rawTelemetry, activeProf, _config, isEnEval);

                        double autoSec = _config.LiveAutoCoachSeconds;
                        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                        long lastMicTicks = Interlocked.Read(ref _lastMicVoiceUtcTicks);
                        long lastNudgeTicks = Interlocked.Read(ref _lastAutoNudgeUtcTicks);
                        bool userRecentlySpoke = lastMicTicks > 0 && (nowTicks - lastMicTicks) < TimeSpan.FromSeconds(3.0).Ticks;
                        double pendingShieldSec = Math.Min(2.4, Math.Max(0.25, autoSec * 0.6));
                        long reqTick = Interlocked.Read(ref _lastTurnRequestTick);
                        bool waitingPending = Volatile.Read(ref _waitingFirstAudioInt) == 1
                            && reqTick > 0
                            && Stopwatch.GetElapsedTime(reqTick).TotalSeconds < pendingShieldSec;
                        bool canVoiceTurn = autoSec > 0 && !_modelSpeaking && !_pcmPlayer.IsPlayingAudio && !waitingPending && !userRecentlySpoke;

                        double motionEnergy = _session.FrameRingBuffer.ComputeRecentMotionEnergy();
                        bool visualSpike = motionEnergy >= 0.42 && _session.FrameRingBuffer.MacroCount >= 2;
                        double scaledAutoSec = autoSec * tactical.CooldownMultiplier;
                        bool regularDue = canVoiceTurn &&
                            (lastNudgeTicks == 0 || (nowTicks - lastNudgeTicks) >= TimeSpan.FromSeconds(scaledAutoSec).Ticks);
                        double eventFloorSec = visualSpike ? Math.Max(3.2, autoSec * 0.55) : Math.Max(3.0, autoSec * 0.45);
                        bool eventDue = canVoiceTurn && (tactical.TriggerImmediate || visualSpike) &&
                            (lastNudgeTicks == 0 || (nowTicks - lastNudgeTicks) >= TimeSpan.FromSeconds(eventFloorSec).Ticks);
                        bool shouldNudge = (regularDue || eventDue) && !tactical.ShouldSilence;

                        bool sentAsTurn = false;
                        if (shouldNudge)
                        {
                            Interlocked.Exchange(ref _lastAutoNudgeUtcTicks, nowTicks);

                            bool skipProactive = false;
                            if (_localPhaseGate is not null && activeProf is not null)
                            {
                                var snap = await _localPhaseGate.ExtractAsync(evalJpeg, activeProf, ct);
                                bool trainingActive = string.Equals(_config.CoachMode, "training", StringComparison.OrdinalIgnoreCase);
                                bool confirmedIdleScreen = !string.IsNullOrWhiteSpace(snap.LogSummary)
                                    || snap.Phase is "REPLAY" or "LOADING" or "DESKTOP"
                                    || (snap.Phase == "MENU" && !trainingActive && motionEnergy < 0.14);
                                if (snap.Phase is "MENU" or "REPLAY" or "LOADING" or "DESKTOP" && confirmedIdleScreen)
                                {
                                    skipProactive = true;
                                    _onLog?.Invoke($"Silencio local [{snap.Phase}] (sin molestar en menú/replay).");
                                }
                            }

                            if (!skipProactive)
                            {
                                captured ??= CaptureSharpFrame(force: true, frameId: ++frameCounter);
                                byte[] turnJpeg = captured?.JpegBytes ?? evalJpeg;

                                sentAsTurn = true;
                                var (_, _, cfgQuality) = ResolveTargetResolution();
                                var clip = _session.FrameRingBuffer.BuildVisualClipPayload(
                                    _storyboardComposer,
                                    captured,
                                    maxFrames: 6,
                                    maxAgeSeconds: 15.0,
                                    jpegQuality: Math.Max(cfgQuality, 82),
                                    isEn: isEnEval);

                                await _turnGate.WaitAsync(ct);
                                try
                                {
                                    Interlocked.Exchange(ref _lastTurnRequestTick, Stopwatch.GetTimestamp());
                                    Interlocked.Exchange(ref _waitingFirstAudioInt, 1);
                                    Interlocked.Exchange(ref _proactiveTurnInFlight, 1);
                                    Interlocked.Exchange(ref _proactivePendingUntilUtcTicks, DateTimeOffset.UtcNow.AddSeconds(2.8).Ticks);
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
                                    string seqPrefix = clip.PanelCount >= 2 && !string.IsNullOrWhiteSpace(clip.SequenceNote)
                                        ? $"{clip.SequenceNote} "
                                        : string.Empty;
                                    string prompt = isEnEval
                                        ? $"{seqPrefix}{factNote}{ctxNote}{motionNote} Continuous OBS observation active even while player is silent. Give 1 crisp predictive callout (8-13 words) for the next 1-3s based ONLY on clearly visible gameplay."
                                        : $"{seqPrefix}{factNote}{ctxNote}{motionNote} Observación continua tipo OBS activa aunque no te hayan hablado. Tirate 1 consejo predictivo concreto (8 a 13 palabras) para los próximos 1-3s SOLO sobre lo que se distingue en pantalla.";

                                    Interlocked.Increment(ref _framesSent);
                                    if (_backend.SupportsMultiFrameTurn && clip.Frames.Count > 1)
                                        await _backend.SendFrameSequenceAndPromptTurnAsync(clip.Frames, prompt, ct);
                                    else
                                        await _backend.SendImageAndPromptTurnAsync(clip.FallbackSingleJpeg ?? turnJpeg, prompt, ct);
                                }
                                finally
                                {
                                    _turnGate.Release();
                                }
                            }
                        }

                        // Throttle passive WebSocket JPEG streaming to profile FPS while pausing during active speech for stutter-free audio
                        double fps = _session.ActiveProfile?.Manifest.CaptureFps ?? 1.5;
                        int wsPeriodMs = Math.Clamp((int)Math.Round(1000.0 / Math.Clamp(fps, 0.5, 3.0)), 330, 1500);
                        long prevWsTick = Interlocked.Read(ref _lastWebSocketFrameTick);
                        double sinceWsMs = prevWsTick == 0 ? double.MaxValue : Stopwatch.GetElapsedTime(prevWsTick).TotalMilliseconds;
                        bool wsDue = prevWsTick == 0 || sinceWsMs >= (captured is not null ? wsPeriodMs : Math.Max(wsPeriodMs, 2500));
                        if (!sentAsTurn && !_modelSpeaking && wsDue)
                        {
                            Interlocked.Exchange(ref _lastWebSocketFrameTick, Stopwatch.GetTimestamp());
                            Interlocked.Increment(ref _framesSent);
                            await _backend.SendVideoFrameAsync(captured?.JpegBytes ?? evalJpeg, ct);
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
