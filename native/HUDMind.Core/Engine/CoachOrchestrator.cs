using System.Collections.Concurrent;
using System.Threading.Channels;
using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;

namespace HUDMind.Core.Engine;

/// <summary>
/// Event-driven bounded-channel (DropOldest) real-time coach engine with latched zero-wait F10 wakeup,
/// isolated chat continuity memory, epoch-guarded game switching, and lock-synchronized voice channels.
/// </summary>
public sealed class CoachOrchestrator : ICoachEngine
{
    private readonly UserConfig _config;
    private readonly int _profilesCount;
    private readonly ProviderFailoverRouter _router;
    private readonly IForegroundWindowDetector _windowDetector;
    private readonly IScreenCaptureService _capture;
    private readonly IVoiceOutputService _voice;
    private readonly IOverlayOutputService _overlay;
    private readonly Action<string>? _onLog;
    private readonly Action<CoachRuntimeStats>? _onStats;
    private readonly Action<bool>? _onPauseChanged;
    private readonly ITextOnlySensorService? _telemetrySensor;
    private readonly ITemporalStoryboardComposer? _storyboardComposer;
    private readonly ICoachSafetyPipeline _safety;
    private readonly ConversationalSpeechCoordinator _speech;
    private readonly MatchSessionCoordinator _session;
    private readonly bool _testMode;
    private readonly bool _disposeVoice;

    private readonly InterventionStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly object _hashLock = new();
    private readonly object _voiceGate = new();
    private readonly object _pauseLock = new();
    private readonly ConcurrentDictionary<int, Task> _inFlightChat = new();
    private int _chatSeq;
    private string _latestDeterministicFact = string.Empty;

    private Channel<CapturedFrameData>? _frameChannel;
    private CancellationTokenSource? _runCts;
    private Task? _producerTask;
    private Task? _consumerTask;

    private ulong? _lastAcceptedHash;
    private ulong? _lastRingHash;
    private volatile bool _userPaused;
    private volatile bool _disposed;
    private int _disposeOnce;
    private long _voiceConfigEpoch;
    private int _forceAnalysisPending;

    private long _framesCaptured;
    private long _framesFiltered;
    private long _aiCalls;
    private long _interventions;
    private double _lastCaptureMs;
    private double _lastAiMs;
    private double _lastVoiceMs;

    public bool IsRunning => Volatile.Read(ref _runCts) is { IsCancellationRequested: false };
    public bool IsPaused => _userPaused;
    public bool SupportsLiveMic => false;
    public string EngineBadge => ".NET 9 · 0% GPU · HTTP/2 MULTI-IA";
    public string ActiveChannelStatus => $"Corriendo · {_router.ActiveProviderLabel}";
    public string ActiveVoiceDisplay => _voice.EngineName;
    public string ActiveGameId => _session.ActiveProfile?.Manifest.GameId ?? string.Empty;

    public void RefreshPersona(string? reason = null) => WakeNow();

    public void UpdateAudioLive(UserConfig config)
    {
        if (_disposed) return;
        lock (_voiceGate)
        {
            if (_disposed) return;
            _config.CopyRuntimePreferencesFrom(config);
            Interlocked.Increment(ref _voiceConfigEpoch);
            if (_config.IsTextOnlyMode)
                _speech.InterruptAndClear();
            _voice.SetOutputDevice(_config.AudioOutputDeviceName);
            _voice.SetSpeechRate(_config.TtsRate);
            _voice.UpdateSettings(_config.VoiceLanguage, _config.TtsVolume, _config.VoiceGender, _config.VoiceStyle);
        }
    }

    public void ApplyLiveSettings(UserConfig config) => UpdateAudioLive(config);

    public CoachOrchestrator(
        UserConfig config,
        List<GameProfile> profiles,
        ProviderFailoverRouter router,
        IForegroundWindowDetector windowDetector,
        IScreenCaptureService capture,
        IVoiceOutputService voice,
        IOverlayOutputService overlay,
        bool testMode = false,
        string? forcedGameId = null,
        Action<string>? onLog = null,
        Action<CoachRuntimeStats>? onStats = null,
        Action<bool>? onPauseChanged = null,
        bool disposeVoice = true,
        ITextOnlySensorService? telemetrySensor = null,
        Action<MatchSessionSnapshot>? onMatchCompleted = null,
        Func<GameProfile, AssembledCoachContext>? resolveContextForProfile = null,
        ITemporalStoryboardComposer? storyboardComposer = null,
        ICoachSafetyPipeline? safetyPipeline = null,
        IAudioSceneMonitor? audioMonitor = null)
    {
        _config = config;
        _profilesCount = profiles.Count;
        _router = router;
        _windowDetector = windowDetector;
        _capture = capture;
        _voice = voice;
        _overlay = overlay;
        _testMode = testMode;
        _onLog = onLog;
        _onStats = onStats;
        _onPauseChanged = onPauseChanged;
        _disposeVoice = disposeVoice;
        _telemetrySensor = telemetrySensor;
        _storyboardComposer = storyboardComposer;
        _safety = safetyPipeline ?? CoachContentGuard.DefaultPipeline;
        _speech = new ConversationalSpeechCoordinator(
            voice,
            audioMonitor ?? NullAudioSceneMonitor.Instance,
            new NaturalConnectorResolver(),
            () => CoachingPersonaBuilder.IsEnglish(_config),
            () => _config.IsCoachVoiceActive && !_userPaused && !_disposed,
            onLog);
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
    }

    public async Task StartAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_disposed || IsRunning)
                return;

            var cts = new CancellationTokenSource();
            var ct = cts.Token;
            lock (_pauseLock) _userPaused = false;
            Volatile.Write(ref _runCts, cts);
            _config.RuntimeTiltRisk = false;
            Interlocked.Exchange(ref _forceAnalysisPending, 0);
            _stateMachine.Reset();
            Volatile.Write(ref _latestDeterministicFact, string.Empty);
            SetLastHash(null);
            _lastRingHash = null;

            try
            {
                bool shouldWarmVoice;
                string outputDevice = string.Empty, rate = string.Empty, lang = string.Empty, gender = string.Empty, style = string.Empty;
                double vol = 0;
                long epoch = 0;
                lock (_voiceGate)
                {
                    shouldWarmVoice = !_disposed && _config.IsCoachVoiceActive;
                    if (shouldWarmVoice)
                    {
                        outputDevice = _config.AudioOutputDeviceName;
                        rate = _config.TtsRate;
                        lang = _config.VoiceLanguage;
                        vol = _config.TtsVolume;
                        gender = _config.VoiceGender;
                        style = _config.VoiceStyle;
                        epoch = Interlocked.Increment(ref _voiceConfigEpoch);
                    }
                }
                if (shouldWarmVoice)
                    await Task.Run(() => WarmUpVoiceForEpoch(outputDevice, rate, lang, vol, gender, style, epoch), ct);

                await _router.PrewarmAsync(ct);
            }
            catch
            {
                Volatile.Write(ref _runCts, null);
                try { cts.Cancel(); } catch { }
                cts.Dispose();
                throw;
            }

            string commLabel = _config.IsTextOnlyMode
                ? "Solo texto (silencioso)"
                : _config.IsCoachVoiceOnlyMode
                    ? $"Solo voz coach ({_voice.EngineName})"
                    : $"Chat escrito + Voz ({_voice.EngineName})";

            var initialProfile = _session.InitializeOnStart(_config, predetectInNonTestMode: false);
            if (_testMode)
            {
                if (initialProfile is not null)
                {
                    _onLog?.Invoke($"[TEST-MODE] Perfil activo: {initialProfile.Manifest.DisplayName} | Comunicación: {commLabel}");
                    if (_config.EnableOverlay)
                        _overlay.ShowCallout("Coach Nativo .NET 9 ACTIVO — observando", UrgencyLevel.Low, initialProfile.Manifest.DisplayName, 3.5);
                }
            }
            else
            {
                _onLog?.Invoke($"[AUTO-DETECT] Esperando juego registrado ({_profilesCount} perfiles) | Comunicación: {commLabel}");
            }

            var channel = Channel.CreateBounded<CapturedFrameData>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
            _frameChannel = channel;

            _producerTask = Task.Run(() => ProducerLoopAsync(channel, ct), ct);
            _consumerTask = Task.Run(() => ConsumerLoopAsync(channel, ct), ct);
            EmitStats();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void SetCommunicationMode(string mode)
    {
        if (_disposed) return;
        string logMsg;
        lock (_voiceGate)
        {
            if (_disposed) return;
            _config.SetCommunicationMode(mode);
            bool isTextOnly = _config.IsTextOnlyMode;
            string lang = _config.VoiceLanguage;
            double vol = _config.TtsVolume;
            string gender = _config.VoiceGender;
            string style = _config.VoiceStyle;
            string outputDevice = _config.AudioOutputDeviceName;
            string rate = _config.TtsRate;
            long epoch = Interlocked.Increment(ref _voiceConfigEpoch);
            if (isTextOnly)
            {
                _speech.InterruptAndClear();
                logMsg = "💬 Modo SOLO TEXTO activo (silencioso: solo chat y overlay).";
            }
            else
            {
                int warmId = Interlocked.Increment(ref _chatSeq);
                var warmDoneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlightChat[warmId] = warmDoneTcs.Task;
                _ = Task.Run(() =>
                {
                    try
                    {
                        WarmUpVoiceForEpoch(outputDevice, rate, lang, vol, gender, style, epoch);
                    }
                    finally
                    {
                        _inFlightChat.TryRemove(warmId, out _);
                        warmDoneTcs.TrySetResult();
                    }
                });
                logMsg = _config.IsCoachVoiceOnlyMode
                    ? "🔊 Modo SOLO VOZ DEL COACH activo (micrófono apagado)."
                    : "🎙 Modo CHARLA seleccionado (en motor Multi-IA clásico: usá chat escrito + voz del coach, o elegí motor Live para mic 16kHz).";
            }
        }
        _onLog?.Invoke(logMsg);
        EmitStats();
    }

    private void WarmUpVoiceForEpoch(
        string outputDevice,
        string rate,
        string lang,
        double vol,
        string gender,
        string style,
        long epoch)
    {
        if (_disposed || !_config.IsCoachVoiceActive || Volatile.Read(ref _voiceConfigEpoch) != epoch)
            return;
        _voice.SetOutputDevice(outputDevice);
        _voice.SetSpeechRate(rate);
        _voice.WarmUp(lang, vol, gender, style);
        lock (_voiceGate)
        {
            if (_disposed || !_config.IsCoachVoiceActive)
                return;
            if (Volatile.Read(ref _voiceConfigEpoch) == epoch)
            {
                _voice.SetOutputDevice(outputDevice);
                _voice.SetSpeechRate(rate);
            }
            else
            {
                _voice.SetOutputDevice(_config.AudioOutputDeviceName);
                _voice.SetSpeechRate(_config.TtsRate);
                _voice.UpdateSettings(_config.VoiceLanguage, _config.TtsVolume, _config.VoiceGender, _config.VoiceStyle);
            }
        }
    }

    public async Task<bool> SendUserTextAsync(string userText, CancellationToken ct = default)
    {
        var msg = (userText ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(msg))
            return false;

        var runCts = Volatile.Read(ref _runCts);
        if (runCts is null || runCts.IsCancellationRequested)
        {
            _onLog?.Invoke("Iniciá el Coach primero para enviar mensajes.");
            return false;
        }

        var profile = _session.ActiveProfile ?? _session.ResolveForcedOrDefaultProfile();
        if (profile is null)
            return false;

        long chatEpoch = _session.CurrentEpoch;
        _onLog?.Invoke($"💬 Vos: \"{msg}\"");

        bool isEnMsg = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
        var inputGuard = _safety.EvaluateUserInput(msg, isEnMsg);
        if (!inputGuard.IsAllowed)
        {
            if (_config.EnableOverlay)
                _overlay.ShowMessage(new OverlayCalloutMessage(inputGuard.SafeRedirectReply, UrgencyLevel.Low, profile.Manifest.DisplayName, Math.Min(_config.OverlayDurationSeconds, 3.5), OverlayEntryKind.Status, "HUD"));
            double guardVoiceMs = 0;
            if (_config.IsCoachVoiceActive && !_userPaused)
            {
                int guardId = Interlocked.Increment(ref _chatSeq);
                var guardDoneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlightChat[guardId] = guardDoneTcs.Task;
                CancellationTokenSource? guardCts = null;
                try
                {
                    if (Volatile.Read(ref _runCts) != runCts || runCts.IsCancellationRequested || _disposed)
                    {
                        guardVoiceMs = 0;
                    }
                    else
                    {
                        guardCts = CancellationTokenSource.CreateLinkedTokenSource(ct, runCts.Token);
                        guardVoiceMs = await _speech.SpeakDirectAndAwaitDispatchAsync(
                            new SpeechIntent(
                                inputGuard.SafeRedirectReply,
                                UrgencyLevel.Low,
                                IsDirectUserResponse: true,
                                InterruptCurrent: true,
                                TopicTag: "safety"),
                            guardCts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                finally
                {
                    try { guardCts?.Dispose(); } catch { }
                    _inFlightChat.TryRemove(guardId, out _);
                    guardDoneTcs.TrySetResult();
                }
            }
            Volatile.Write(ref _lastVoiceMs, guardVoiceMs);
            _onLog?.Invoke($"🛡️ Coach [safety-guard]: \"{inputGuard.SafeRedirectReply}\"");
            EmitStats();
            return true;
        }
        int taskId = Interlocked.Increment(ref _chatSeq);
        if (_config.EnableOverlay && !_userPaused)
            _overlay.ShowMessage(new OverlayCalloutMessage(msg, UrgencyLevel.Low, profile.Manifest.DisplayName, _config.OverlayDurationSeconds, OverlayEntryKind.UserMessage, "Vos"));
        CancellationTokenSource? linkedCts = null;
        try
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, runCts.Token);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        var doneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Pre-register tracking BEFORE scheduling so StopAsync never returns before chat finishes!
        _inFlightChat[taskId] = doneTcs.Task;
        if (Volatile.Read(ref _runCts) != runCts || runCts.IsCancellationRequested || _disposed)
        {
            _inFlightChat.TryRemove(taskId, out _);
            doneTcs.TrySetResult();
            linkedCts.Dispose();
            return false;
        }

        var execTask = Task.Run(async () =>
        {
            using (linkedCts)
            {
                try
                {
                    var (w, h, q) = ResolveTargetResolution();
                    var region = _testMode ? null : _windowDetector.GetForegroundClientRect();
                    var (frame, _) = _capture.CaptureIfChanged(
                        _config.CaptureMonitor, region, w, h, q,
                        frameId: 0, lastAcceptedHash: null, phashThreshold: 0, isForced: true);

                    bool isEn = CoachingPersonaBuilder.IsEnglish(_config, preferTextLang: true);
                    var clip = _session.FrameRingBuffer.BuildVisualClipPayload(
                        _storyboardComposer,
                        frame,
                        maxFrames: 6,
                        maxAgeSeconds: 8.0,
                        jpegQuality: Math.Max(q, 84),
                        isEn: isEn);
                    var timelineText = _session.Memory.FormatVisualTimeline(isEn: isEn);
                    if (clip.PanelCount >= 2 && !string.IsNullOrWhiteSpace(clip.SequenceNote))
                    {
                        timelineText = string.IsNullOrWhiteSpace(timelineText)
                            ? clip.SequenceNote
                            : $"{clip.SequenceNote} ; {timelineText}";
                    }
                    var (reply, aiMs, model) = await _router.AskQuestionSequenceAsync(
                        clip,
                        msg,
                        profile,
                        _config,
                        _session.Memory.FormatCompactHistory(),
                        timelineText,
                        _session.Memory.FormatChatHistory(isEn: isEn),
                        linkedCts.Token);

                    if (string.IsNullOrWhiteSpace(reply))
                    {
                        _onLog?.Invoke($"Sin respuesta del coach [{model}].");
                        return false;
                    }

                    if (_session.CurrentEpoch != chatEpoch)
                        return false;

                    Volatile.Write(ref _lastAiMs, aiMs);
                    Interlocked.Increment(ref _interventions);
                    _session.Memory.RecordChatTurn(msg, reply);
                    _session.Memory.RecordObservation("CHAT", msg);
                    _session.Memory.RecordMatchEvent(profile.Manifest.GameId, profile.Manifest.DisplayName, "CHAT", msg, reply);

                    if (_config.EnableOverlay)
                        _overlay.ShowMessage(new OverlayCalloutMessage(reply, UrgencyLevel.Medium, profile.Manifest.DisplayName, _config.OverlayDurationSeconds, OverlayEntryKind.CoachFinal, "Coach"));

                    double voiceMs = 0;
                    if (_config.IsCoachVoiceActive && !_userPaused && !linkedCts.Token.IsCancellationRequested)
                    {
                        voiceMs = await _speech.SpeakDirectAndAwaitDispatchAsync(
                            new SpeechIntent(
                                reply,
                                UrgencyLevel.Medium,
                                IsDirectUserResponse: true,
                                InterruptCurrent: false,
                                TopicTag: msg),
                            linkedCts.Token);
                    }
                    Volatile.Write(ref _lastVoiceMs, voiceMs);

                    string icon = (_config.IsCoachVoiceActive && !_userPaused) ? "🔊" : "💬";
                    _onLog?.Invoke($"{icon} Coach ({aiMs:F0}ms) [{model}]: \"{reply}\"");
                    EmitStats();
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    _onLog?.Invoke($"Error en chat con coach: {SecretRedactor.Redact(ex.Message)}");
                    return false;
                }
                finally
                {
                    _inFlightChat.TryRemove(taskId, out _);
                    doneTcs.TrySetResult();
                }
            }
        });

        return await execTask;
    }

    public void TogglePause()
    {
        if (_disposed || !IsRunning)
            return;
        bool pausedNow;
        lock (_pauseLock)
        {
            if (_disposed || !IsRunning)
                return;
            pausedNow = !_userPaused;
            _userPaused = pausedNow;
        }
        var title = _session.ActiveProfile?.Manifest.DisplayName ?? "HUDMind";
        _onPauseChanged?.Invoke(pausedNow);
        if (pausedNow)
        {
            lock (_voiceGate)
            {
                if (!_disposed)
                    _speech.InterruptAndClear();
            }
            _onLog?.Invoke($"Coach PAUSADO ({_config.HotkeyTogglePause} para reanudar).");
            if (_config.EnableOverlay)
                _overlay.ShowMessage(new OverlayCalloutMessage($"Coach PAUSADO — {_config.HotkeyTogglePause} para reanudar", UrgencyLevel.Low, title, 2.5, OverlayEntryKind.Status, "HUD"));
        }
        else
        {
            WakeNow();
            _onLog?.Invoke("Coach REANUDADO.");
            if (_config.EnableOverlay)
                _overlay.ShowMessage(new OverlayCalloutMessage("Coach ACTIVO — observando", UrgencyLevel.Low, title, 2.5, OverlayEntryKind.Status, "HUD"));
        }
        EmitStats();
    }

    public void TriggerOnDemandAnalysis()
    {
        if (_disposed || !IsRunning)
            return;
        var active = _session.ActiveProfile ?? (_testMode ? _session.ResolveForcedOrDefaultProfile() : null);
        if (active is null)
        {
            _onLog?.Invoke($"{_config.HotkeyAnalyzeNow} ignorado: todavía no hay un juego activo detectado.");
            return;
        }

        bool unpausedNow = false;
        lock (_pauseLock)
        {
            if (!_disposed && _userPaused)
            {
                _userPaused = false;
                unpausedNow = true;
            }
        }
        if (unpausedNow)
            _onPauseChanged?.Invoke(false);

        Interlocked.Exchange(ref _forceAnalysisPending, 1);
        WakeNow();
        _onLog?.Invoke($"Análisis inmediato ({_config.HotkeyAnalyzeNow}) disparado (<1ms wake).");
        if (_config.EnableOverlay)
            _overlay.ShowMessage(new OverlayCalloutMessage("Analizando jugada...", UrgencyLevel.Low, active.Manifest.DisplayName, 1.8, OverlayEntryKind.Status, "HUD"));
    }

    public void WakeNow()
    {
        try
        {
            if (!_disposed && _wakeSignal.CurrentCount == 0)
                _wakeSignal.Release();
        }
        catch (SemaphoreFullException) { /* already latched */ }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private async Task WaitInterruptibleAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
            return;
        try
        {
            await _wakeSignal.WaitAsync(delay, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Ignored
        }
    }

    private ulong? GetLastHash()
    {
        lock (_hashLock) return _lastAcceptedHash;
    }

    private void SetLastHash(ulong? hash)
    {
        lock (_hashLock) _lastAcceptedHash = hash;
    }

    private (int W, int H, int Quality) ResolveTargetResolution() =>
        _config.TokenEfficiencyMode.ToLowerInvariant() switch
        {
            "eco" => (960, 540, Math.Max(_config.ImageCompressionQuality, 76)),
            "high" => (1440, 810, Math.Max(_config.ImageCompressionQuality, 84)),
            _ => (1280, 720, Math.Max(_config.ImageCompressionQuality, 80))
        };

    private double EffectiveCooldown(GameProfile profile)
    {
        var baseCd = profile.Manifest.CooldownSeconds;
        return _config.CoachMode.Equals("training", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(2.8, Math.Round(baseCd * 0.65, 1))
            : baseCd;
    }

    private async Task ProducerLoopAsync(Channel<CapturedFrameData> channel, CancellationToken ct)
    {
        long frameCounter = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_userPaused)
                {
                    await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(400), ct);
                    continue;
                }

                if (!_testMode)
                {
                    var switchOutcome = _session.SyncForegroundProfile(_config, "Coach Nativo ACTIVO — observando");
                    if (!switchOutcome.GameInForeground)
                    {
                        await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(750), ct);
                        continue;
                    }

                    if (switchOutcome.Switched)
                    {
                        SetLastHash(null);
                        _lastRingHash = null;
                        // Clear cooldown timers on game switch WITHOUT resetting _busy owned by ConsumerLoopAsync
                        _stateMachine.ResetCooldownsKeepBusy();
                        Volatile.Write(ref _latestDeterministicFact, string.Empty);
                        EmitStats();
                    }
                }

                var profile = _session.ActiveProfile;
                if (profile is null)
                {
                    await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(500), ct);
                    continue;
                }

                bool isEnEval = CoachingPersonaBuilder.IsEnglish(_config);
                string rawTelemetry = _telemetrySensor is not null
                    ? await _telemetrySensor.ReadTelemetryAsync(profile, ct)
                    : string.Empty;
                var tactical = _session.EvaluateTacticalSignal(rawTelemetry, profile, _config, isEnEval);
                Volatile.Write(ref _latestDeterministicFact, tactical.DeterministicFact);

                var (w, h, q) = ResolveTargetResolution();
                var region = _testMode ? null : _windowDetector.GetForegroundClientRect();

                bool hasPendingForce = Volatile.Read(ref _forceAnalysisPending) == 1;
                if (!hasPendingForce && tactical.ShouldSilence)
                {
                    await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(550), ct);
                    continue;
                }

                if (hasPendingForce && _stateMachine.IsBusy)
                {
                    await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(150), ct);
                    continue;
                }

                bool isForced = false;
                double? approvedCooldownTarget = null;
                if (hasPendingForce)
                {
                    isForced = Interlocked.Exchange(ref _forceAnalysisPending, 0) == 1;
                }
                else
                {
                    double scaledCd = Math.Max(2.2, EffectiveCooldown(profile) * tactical.CooldownMultiplier);
                    double tacticalEarlyCd = Math.Max(2.2, EffectiveCooldown(profile) * 0.45);
                    double spikeEarlyCd = Math.Max(2.8, EffectiveCooldown(profile) * 0.50);
                    double motionEnergy = _session.FrameRingBuffer.ComputeRecentMotionEnergy();
                    bool visualSpike = motionEnergy >= 0.42 && _session.FrameRingBuffer.MacroCount >= 2;

                    bool tacticalReady = tactical.TriggerImmediate &&
                        _stateMachine.CanAnalyze(tacticalEarlyCd, _config.MinInferenceIntervalSeconds);
                    bool spikeReady = visualSpike &&
                        _stateMachine.CanAnalyze(spikeEarlyCd, _config.MinInferenceIntervalSeconds);

                    if (tacticalReady)
                    {
                        approvedCooldownTarget = tacticalEarlyCd;
                    }
                    else if (spikeReady)
                    {
                        approvedCooldownTarget = spikeEarlyCd;
                    }
                    else
                    {
                        var waitRem = _stateMachine.GetWaitUntilReady(
                            scaledCd,
                            _config.MinInferenceIntervalSeconds);
                        if (waitRem > TimeSpan.Zero)
                        {
                            // Keep an OBS-style dual-tier 0%-GPU temporal replay buffer in RAM (~10 Hz burst + ~25s macro window)
                            // so silent proactive turns, F10, and chat questions see the entire recent play without gaps.
                            bool macroDue = _session.FrameRingBuffer.NeedsMacroKeyframe(DateTimeOffset.UtcNow);
                            var (ringFrame, ringSkipped) = _capture.CaptureIfChanged(
                                _config.CaptureMonitor,
                                region,
                                w,
                                h,
                                Math.Min(q, 78),
                                Interlocked.Increment(ref frameCounter),
                                _lastRingHash,
                                macroDue ? 0 : Math.Min(profile.Manifest.PhashThreshold, 4),
                                isForced: false);
                            _session.ObserveCaptureHealth(_config);
                            if (!ringSkipped && ringFrame is not null)
                            {
                                _lastRingHash = ringFrame.PerceptualHash;
                                _session.FrameRingBuffer.Record(ringFrame);
                            }

                            var boundedWait = waitRem > TimeSpan.FromMilliseconds(100)
                                ? TimeSpan.FromMilliseconds(100)
                                : waitRem;
                            await WaitInterruptibleAsync(boundedWait, ct);
                            continue;
                        }
                        if (scaledCd < EffectiveCooldown(profile))
                            approvedCooldownTarget = scaledCd;
                    }
                }

                bool mainMacroDue = !isForced && _session.FrameRingBuffer.NeedsMacroKeyframe(DateTimeOffset.UtcNow);
                long frameEpoch = _session.CurrentEpoch;
                var (frame, skippedByHash) = _capture.CaptureIfChanged(
                    _config.CaptureMonitor,
                    region,
                    w,
                    h,
                    q,
                    Interlocked.Increment(ref frameCounter),
                    GetLastHash(),
                    (isForced || mainMacroDue) ? 0 : profile.Manifest.PhashThreshold,
                    isForced);

                _session.ObserveCaptureHealth(_config);

                if (skippedByHash)
                {
                    Interlocked.Increment(ref _framesFiltered);
                }
                else if (frame is not null && _session.CurrentEpoch == frameEpoch)
                {
                    var queuedFrame = frame with
                    {
                        TargetCooldownSeconds = approvedCooldownTarget,
                        SessionEpoch = frameEpoch
                    };
                    _lastRingHash = queuedFrame.PerceptualHash;
                    _session.FrameRingBuffer.Record(queuedFrame, forceReplaceTailIfTooClose: isForced);
                    Interlocked.Increment(ref _framesCaptured);
                    Volatile.Write(ref _lastCaptureMs, queuedFrame.CaptureMs + queuedFrame.EncodeMs);
                    channel.Writer.TryWrite(queuedFrame);
                }

                var fps = Math.Clamp(profile.Manifest.CaptureFps, 0.5, 4.0);
                await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(1000.0 / fps), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Error en captura: {SecretRedactor.Redact(ex.Message)}");
                await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(500), ct);
            }
        }
    }

    private async Task ConsumerLoopAsync(Channel<CapturedFrameData> channel, CancellationToken ct)
    {
        await foreach (var frame in channel.Reader.ReadAllAsync(ct))
        {
            var profile = _session.ActiveProfile;
            long startEpoch = _session.CurrentEpoch;
            if (profile is null || _userPaused || frame.SessionEpoch != startEpoch)
                continue;

            bool forceNow = frame.IsForced;
            double requiredCooldown = frame.TargetCooldownSeconds ?? EffectiveCooldown(profile);
            if (!forceNow && !_stateMachine.CanAnalyze(requiredCooldown, _config.MinInferenceIntervalSeconds))
            {
                Interlocked.Increment(ref _framesFiltered);
                continue;
            }

            if (!_stateMachine.TryBeginAnalysis())
                continue;

            if (_session.CurrentEpoch == startEpoch)
                SetLastHash(frame.PerceptualHash);
            bool delivered = false;
            bool epochValid = true;
            try
            {
                Interlocked.Increment(ref _aiCalls);
                bool isEn = CoachingPersonaBuilder.IsEnglish(_config);
                var (_, _, cfgQuality) = ResolveTargetResolution();
                var clip = _session.FrameRingBuffer.BuildVisualClipPayload(
                    _storyboardComposer,
                    frame,
                    maxFrames: forceNow ? 4 : 3,
                    maxAgeSeconds: forceNow ? 4.5 : 3.2,
                    jpegQuality: Math.Max(cfgQuality, 82),
                    isEn: isEn);

                var history = _session.Memory.FormatCompactHistory();
                var timeline = _session.Memory.FormatVisualTimeline(isEn: isEn);
                var fact = Volatile.Read(ref _latestDeterministicFact);
                var contextParts = new List<string>(3);
                if (clip.PanelCount >= 2 && !string.IsNullOrWhiteSpace(clip.SequenceNote))
                    contextParts.Add(clip.SequenceNote);
                if (!string.IsNullOrWhiteSpace(fact)) contextParts.Add(fact);
                if (!string.IsNullOrWhiteSpace(timeline)) contextParts.Add(timeline);
                var combinedContext = string.Join(" | ", contextParts);

                var (resp, aiMs, model) = await _router.AnalyzeFrameSequenceAsync(
                    clip,
                    profile,
                    _config,
                    history,
                    forceNow,
                    combinedContext,
                    ct);
                Volatile.Write(ref _lastAiMs, aiMs);

                if (_session.CurrentEpoch != startEpoch || _userPaused)
                {
                    epochValid = false;
                    continue;
                }

                if (resp is not null && !string.IsNullOrWhiteSpace(resp.MatchContext))
                {
                    var ctx = resp.MatchContext.Trim();
                    string phase = "PLAY";
                    string summary = ctx;
                    if (ctx.StartsWith('[') && ctx.IndexOf(']') is int closeIdx && closeIdx > 1)
                    {
                        phase = ctx[1..closeIdx];
                        summary = ctx[(closeIdx + 1)..].Trim();
                    }
                    _session.Memory.RecordObservation(phase, summary);
                }

                if (resp is { ShouldIntervene: true } && !string.IsNullOrWhiteSpace(resp.OverlayText))
                {
                    if (!forceNow && _session.Memory.IsRepetitive(resp))
                    {
                        _onLog?.Invoke($"Consejo repetitivo omitido ({aiMs:F0}ms) [{model}]");
                    }
                    else
                    {
                        delivered = true;
                        Interlocked.Increment(ref _interventions);
                        _session.Memory.Record(resp);
                        _session.Memory.RecordMatchEvent(profile.Manifest.GameId, profile.Manifest.DisplayName, "PLAY", resp.MatchContext, resp.OverlayText);

                        if (_config.EnableOverlay)
                            _overlay.ShowMessage(new OverlayCalloutMessage(resp.OverlayText, resp.Urgency, profile.Manifest.DisplayName, _config.OverlayDurationSeconds, OverlayEntryKind.CoachFinal, "Coach"));

                        double voiceMs = 0;
                        if (_config.IsCoachVoiceActive && !_userPaused && !ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(resp.VoiceLine))
                        {
                            long tSpeech = System.Diagnostics.Stopwatch.GetTimestamp();
                            _speech.Enqueue(new SpeechIntent(
                                resp.VoiceLine,
                                resp.Urgency,
                                IsDirectUserResponse: forceNow,
                                InterruptCurrent: forceNow,
                                TopicTag: resp.MatchContext,
                                FreshnessTtlSeconds: Math.Clamp(EffectiveCooldown(profile) * 0.75, 2.5, 4.5)));
                            voiceMs = Math.Max(0.1, System.Diagnostics.Stopwatch.GetElapsedTime(tSpeech).TotalMilliseconds);
                        }

                        Volatile.Write(ref _lastVoiceMs, voiceMs);
                        double totalMs = frame.CaptureMs + frame.EncodeMs + aiMs + voiceMs;
                        string seenNote = !string.IsNullOrWhiteSpace(resp.MatchContext) ? $" | vio: {resp.MatchContext}" : string.Empty;
                        _onLog?.Invoke(
                            $"[{resp.Urgency.ToString().ToUpperInvariant()}] \"{resp.OverlayText}\"{seenNote} | " +
                            $"TOTAL={totalMs:F0}ms (cap={frame.CaptureMs + frame.EncodeMs:F1}ms + IA={aiMs:F0}ms) [{model}]");
                    }
                }
                else
                {
                    string seenNote = resp is not null && !string.IsNullOrWhiteSpace(resp.MatchContext)
                        ? $" | vio: {resp.MatchContext}"
                        : string.Empty;
                    _onLog?.Invoke($"Silencio inteligente ({aiMs:F0}ms, {frame.JpegBytes.Length / 1024.0:F1}KB){seenNote} [{model}]");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Error IA: {SecretRedactor.Redact(ex.Message)}");
            }
            finally
            {
                if (epochValid)
                    _stateMachine.EndAnalysis(delivered);
                else
                {
                    _stateMachine.Reset();
                    SetLastHash(null);
                }

                if (Volatile.Read(ref _forceAnalysisPending) == 1)
                    WakeNow();
                EmitStats();
            }
        }
    }

    private void EmitStats()
    {
        _onStats?.Invoke(new CoachRuntimeStats(
            Interlocked.Read(ref _framesCaptured),
            Interlocked.Read(ref _framesFiltered),
            Interlocked.Read(ref _aiCalls),
            Interlocked.Read(ref _interventions),
            Volatile.Read(ref _lastCaptureMs),
            Volatile.Read(ref _lastAiMs),
            Volatile.Read(ref _lastVoiceMs),
            _router.ActiveProviderLabel,
            _session.ActiveProfile?.Manifest.DisplayName ?? "(esperando juego)",
            _userPaused
        ));
    }

    public (string GameName, string Digest, int Count) GetMatchSessionDigest(bool isEn = false) =>
        _session.GetMatchSessionDigest(isEn);

    public MatchSessionSnapshot? CaptureMatchSnapshot(bool isEn = false) =>
        _session.CaptureMatchSnapshot(isEn);

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            var cts = Interlocked.Exchange(ref _runCts, null);
            if (cts is null)
                return;

            await cts.CancelAsync();
            WakeNow();
            _frameChannel?.Writer.TryComplete();

            var tasks = new List<Task>();
            if (_producerTask is not null) tasks.Add(_producerTask);
            if (_consumerTask is not null) tasks.Add(_consumerTask);
            tasks.AddRange(_inFlightChat.Values);
            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks); } catch { /* clean shutdown */ }
            }

            _session.FlushMatchSnapshot(resetMatchLog: false, _config);
            _session.OnStop();
            _producerTask = null;
            _consumerTask = null;
            _frameChannel = null;
            lock (_voiceGate) _speech.InterruptAndClear();
            cts.Dispose();
            _onLog?.Invoke("Motor nativo HUDMind detenido.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0) return;
        _disposed = true;
        await StopAsync();
        var pendingWarmups = _inFlightChat.Values.ToArray();
        if (pendingWarmups.Length > 0)
        {
            try { await Task.WhenAll(pendingWarmups); } catch { }
        }
        await _speech.DisposeAsync();
        await _router.DisposeAsync();
        if (_disposeVoice)
            lock (_voiceGate) _voice.Dispose();
        _capture.Dispose();
        _wakeSignal.Dispose();
        _lifecycleLock.Dispose();
    }
}
