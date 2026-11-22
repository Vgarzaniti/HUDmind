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
    private readonly List<GameProfile> _profiles;
    private readonly ProviderFailoverRouter _router;
    private readonly IForegroundWindowDetector _windowDetector;
    private readonly IScreenCaptureService _capture;
    private readonly IVoiceOutputService _voice;
    private readonly IOverlayOutputService _overlay;
    private readonly Action<string>? _onLog;
    private readonly Action<CoachRuntimeStats>? _onStats;
    private readonly Action<bool>? _onPauseChanged;
    private readonly bool _testMode;
    private readonly bool _disposeVoice;
    private readonly string? _forcedGameId;

    private readonly InterventionStateMachine _stateMachine = new();
    private readonly InterventionMemory _memory = new(maxItems: 12, ttlSeconds: 75.0);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly object _hashLock = new();
    private readonly object _voiceGate = new();
    private readonly ConcurrentDictionary<int, Task> _inFlightChat = new();
    private int _chatSeq;

    private Channel<CapturedFrameData>? _frameChannel;
    private CancellationTokenSource? _runCts;
    private Task? _producerTask;
    private Task? _consumerTask;

    private volatile GameProfile? _activeProfile;
    private long _profileEpoch;
    private ulong? _lastAcceptedHash;
    private volatile bool _userPaused;
    private bool _disposed;
    private int _forceAnalysisPending;

    private long _framesCaptured;
    private long _framesFiltered;
    private long _aiCalls;
    private long _interventions;
    private double _lastCaptureMs;
    private double _lastAiMs;
    private double _lastVoiceMs;
    private long _lastChatSpeechUtcTicks;

    public bool IsRunning => Volatile.Read(ref _runCts) is { IsCancellationRequested: false };
    public bool IsPaused => _userPaused;
    public bool SupportsLiveMic => false;
    public string EngineBadge => ".NET 9 · 0% GPU · HTTP/2 MULTI-IA";
    public string ActiveChannelStatus => $"Corriendo · {_router.ActiveProviderLabel}";
    public string ActiveVoiceDisplay => _voice.EngineName;

    public void RefreshPersona(string? reason = null) => WakeNow();

    public void UpdateAudioLive(UserConfig config)
    {
        _config.CopyRuntimePreferencesFrom(config);
        lock (_voiceGate)
            _voice.UpdateSettings(_config.VoiceLanguage, _config.TtsVolume, _config.VoiceGender, _config.VoiceStyle);
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
        bool disposeVoice = true)
    {
        _config = config;
        _profiles = profiles;
        _router = router;
        _windowDetector = windowDetector;
        _capture = capture;
        _voice = voice;
        _overlay = overlay;
        _testMode = testMode;
        _forcedGameId = forcedGameId;
        _onLog = onLog;
        _onStats = onStats;
        _onPauseChanged = onPauseChanged;
        _disposeVoice = disposeVoice;
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
            Volatile.Write(ref _runCts, cts);
            _userPaused = false;
            Interlocked.Exchange(ref _forceAnalysisPending, 0);
            _stateMachine.Reset();
            _memory.Clear(clearMatchLog: true);
            SetLastHash(null);

            try
            {
                if (_config.IsCoachVoiceActive)
                {
                    await Task.Run(() =>
                        _voice.WarmUp(_config.VoiceLanguage, _config.TtsVolume, _config.VoiceGender, _config.VoiceStyle), ct);
                }

                await _router.PrewarmAsync(ct);
            }
            catch
            {
                Volatile.Write(ref _runCts, null);
                cts.Dispose();
                throw;
            }

            string commLabel = _config.IsTextOnlyMode
                ? "Solo texto (silencioso)"
                : _config.IsCoachVoiceOnlyMode
                    ? $"Solo voz coach ({_voice.EngineName})"
                    : $"Chat escrito + Voz ({_voice.EngineName})";

            if (_testMode)
            {
                _activeProfile = ResolveForcedOrDefaultProfile();
                if (_activeProfile is not null)
                {
                    _onLog?.Invoke($"[TEST-MODE] Perfil activo: {_activeProfile.Manifest.DisplayName} | Comunicación: {commLabel}");
                    if (_config.EnableOverlay)
                        _overlay.ShowCallout("Coach Nativo .NET 9 ACTIVO — observando", UrgencyLevel.Low, _activeProfile.Manifest.DisplayName, 3.5);
                }
            }
            else
            {
                _activeProfile = null;
                _onLog?.Invoke($"[AUTO-DETECT] Esperando juego registrado ({_profiles.Count} perfiles) | Comunicación: {commLabel}");
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

    private GameProfile? ResolveForcedOrDefaultProfile() =>
        _profiles.FirstOrDefault(p => string.Equals(p.Manifest.GameId, _forcedGameId, StringComparison.OrdinalIgnoreCase))
        ?? _profiles.FirstOrDefault();

    public void SetCommunicationMode(string mode)
    {
        _config.SetCommunicationMode(mode);
        lock (_voiceGate)
        {
            if (_config.IsTextOnlyMode)
            {
                _voice.StopSpeaking();
                _onLog?.Invoke("💬 Modo SOLO TEXTO activo (silencioso: solo chat y overlay).");
            }
            else
            {
                _ = Task.Run(() => _voice.WarmUp(_config.VoiceLanguage, _config.TtsVolume, _config.VoiceGender, _config.VoiceStyle));
                if (_config.IsCoachVoiceOnlyMode)
                    _onLog?.Invoke("🔊 Modo SOLO VOZ DEL COACH activo (micrófono apagado).");
                else
                    _onLog?.Invoke("🎙 Modo CHARLA seleccionado (en motor Multi-IA clásico: usá chat escrito + voz del coach, o elegí motor Live para mic 16kHz).");
            }
        }
        EmitStats();
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

        var profile = _activeProfile ?? ResolveForcedOrDefaultProfile();
        if (profile is null)
            return false;

        _onLog?.Invoke($"💬 Vos: \"{msg}\"");
        int taskId = Interlocked.Increment(ref _chatSeq);
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
                    var (reply, aiMs, model) = await _router.AskQuestionAsync(
                        frame?.JpegBytes,
                        msg,
                        profile,
                        _config,
                        _memory.FormatCompactHistory(),
                        _memory.FormatVisualTimeline(isEn: isEn),
                        _memory.FormatChatHistory(isEn: isEn),
                        linkedCts.Token);

                    if (string.IsNullOrWhiteSpace(reply))
                    {
                        _onLog?.Invoke($"Sin respuesta del coach [{model}].");
                        return false;
                    }

                    Volatile.Write(ref _lastAiMs, aiMs);
                    Interlocked.Increment(ref _interventions);
                    _memory.RecordChatTurn(msg, reply);
                    _memory.RecordObservation("CHAT", msg);
                    _memory.RecordMatchEvent(profile.Manifest.GameId, profile.Manifest.DisplayName, "CHAT", msg, reply);

                    if (_config.EnableOverlay)
                        _overlay.ShowCallout(reply, UrgencyLevel.Medium, profile.Manifest.DisplayName, _config.OverlayDurationSeconds);

                    double voiceMs = 0;
                    lock (_voiceGate)
                    {
                        if (_config.IsCoachVoiceActive && !_userPaused && !linkedCts.Token.IsCancellationRequested)
                        {
                            voiceMs = _voice.SpeakInstant(reply);
                            Interlocked.Exchange(ref _lastChatSpeechUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                        }
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
        _userPaused = !_userPaused;
        var title = _activeProfile?.Manifest.DisplayName ?? "HUDMind";
        _onPauseChanged?.Invoke(_userPaused);
        if (_userPaused)
        {
            lock (_voiceGate) _voice.StopSpeaking();
            _onLog?.Invoke($"Coach PAUSADO ({_config.HotkeyTogglePause} para reanudar).");
            if (_config.EnableOverlay)
                _overlay.ShowCallout($"Coach PAUSADO — {_config.HotkeyTogglePause} para reanudar", UrgencyLevel.Low, title, 2.5);
        }
        else
        {
            WakeNow();
            _onLog?.Invoke("Coach REANUDADO.");
            if (_config.EnableOverlay)
                _overlay.ShowCallout("Coach ACTIVO — observando", UrgencyLevel.Low, title, 2.5);
        }
        EmitStats();
    }

    public void TriggerOnDemandAnalysis()
    {
        if (_activeProfile is null && _profiles.Count > 0 && _testMode)
            _activeProfile = ResolveForcedOrDefaultProfile();

        var active = _activeProfile;
        if (active is null)
        {
            _onLog?.Invoke($"{_config.HotkeyAnalyzeNow} ignorado: todavía no hay un juego activo detectado.");
            return;
        }

        if (_userPaused)
        {
            _userPaused = false;
            _onPauseChanged?.Invoke(false);
        }

        Interlocked.Exchange(ref _forceAnalysisPending, 1);
        WakeNow();
        _onLog?.Invoke($"Análisis inmediato ({_config.HotkeyAnalyzeNow}) disparado (<1ms wake).");
        if (_config.EnableOverlay)
            _overlay.ShowCallout("Analizando jugada...", UrgencyLevel.Low, active.Manifest.DisplayName, 1.8);
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
            "eco" => (800, 450, Math.Max(_config.ImageCompressionQuality, 74)),
            "balanced" => (960, 540, Math.Max(_config.ImageCompressionQuality, 78)),
            _ => (1280, 720, Math.Max(_config.ImageCompressionQuality, 82))
        };

    private double EffectiveCooldown(GameProfile profile)
    {
        var baseCd = profile.Manifest.CooldownSeconds;
        return _config.CoachMode.Equals("training", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(3.5, Math.Round(baseCd * 0.7, 1))
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
                    var fg = _windowDetector.GetForegroundExecutable();
                    var matched = GameProfile.MatchForegroundProfile(_profiles, fg, _forcedGameId);
                    if (matched is not null && !ReferenceEquals(matched, _activeProfile))
                    {
                        _activeProfile = matched;
                        Interlocked.Increment(ref _profileEpoch);
                        SetLastHash(null);
                        _stateMachine.Reset();
                        _memory.Clear();
                        _onLog?.Invoke($"Juego detectado automáticamente: {matched.Manifest.DisplayName} ({fg})");
                        if (_config.EnableOverlay)
                            _overlay.ShowCallout("Coach Nativo ACTIVO — observando", UrgencyLevel.Low, matched.Manifest.DisplayName, 3.5);
                        EmitStats();
                    }
                    else if (matched is null)
                    {
                        await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(750), ct);
                        continue;
                    }
                }

                var profile = _activeProfile;
                if (profile is null)
                {
                    await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(500), ct);
                    continue;
                }

                bool hasPendingForce = Volatile.Read(ref _forceAnalysisPending) == 1;
                if (hasPendingForce && _stateMachine.IsBusy)
                {
                    await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(150), ct);
                    continue;
                }

                bool isForced = false;
                if (hasPendingForce)
                {
                    isForced = Interlocked.Exchange(ref _forceAnalysisPending, 0) == 1;
                }
                else
                {
                    var waitRem = _stateMachine.GetWaitUntilReady(
                        EffectiveCooldown(profile),
                        _config.MinInferenceIntervalSeconds);
                    if (waitRem > TimeSpan.Zero)
                    {
                        var boundedWait = waitRem > TimeSpan.FromMilliseconds(900)
                            ? TimeSpan.FromMilliseconds(900)
                            : waitRem;
                        await WaitInterruptibleAsync(boundedWait, ct);
                        continue;
                    }
                }

                var (w, h, q) = ResolveTargetResolution();
                var region = _testMode ? null : _windowDetector.GetForegroundClientRect();
                var (frame, skippedByHash) = _capture.CaptureIfChanged(
                    _config.CaptureMonitor,
                    region,
                    w,
                    h,
                    q,
                    Interlocked.Increment(ref frameCounter),
                    GetLastHash(),
                    profile.Manifest.PhashThreshold,
                    isForced);

                if (skippedByHash)
                {
                    Interlocked.Increment(ref _framesFiltered);
                }
                else if (frame is not null)
                {
                    Interlocked.Increment(ref _framesCaptured);
                    Volatile.Write(ref _lastCaptureMs, frame.CaptureMs + frame.EncodeMs);
                    channel.Writer.TryWrite(frame);
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
            var profile = _activeProfile;
            long startEpoch = Volatile.Read(ref _profileEpoch);
            if (profile is null || _userPaused)
                continue;

            bool forceNow = frame.IsForced;
            if (!forceNow && !_stateMachine.CanAnalyze(EffectiveCooldown(profile), _config.MinInferenceIntervalSeconds))
            {
                Interlocked.Increment(ref _framesFiltered);
                continue;
            }

            if (!_stateMachine.TryBeginAnalysis())
                continue;

            SetLastHash(frame.PerceptualHash);
            bool delivered = false;
            bool epochValid = true;
            try
            {
                Interlocked.Increment(ref _aiCalls);
                bool isEn = CoachingPersonaBuilder.IsEnglish(_config);
                var history = _memory.FormatCompactHistory();
                var timeline = _memory.FormatVisualTimeline(isEn: isEn);
                var (resp, aiMs, model) = await _router.AnalyzeFrameAsync(
                    frame.JpegBytes,
                    profile,
                    _config,
                    history,
                    forceNow,
                    timeline,
                    ct);
                Volatile.Write(ref _lastAiMs, aiMs);

                if (Volatile.Read(ref _profileEpoch) != startEpoch || _userPaused)
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
                    _memory.RecordObservation(phase, summary);
                }

                if (resp is { ShouldIntervene: true } && !string.IsNullOrWhiteSpace(resp.OverlayText))
                {
                    if (!forceNow && _memory.IsRepetitive(resp))
                    {
                        _onLog?.Invoke($"Consejo repetitivo omitido ({aiMs:F0}ms) [{model}]");
                    }
                    else
                    {
                        delivered = true;
                        Interlocked.Increment(ref _interventions);
                        _memory.Record(resp);
                        _memory.RecordMatchEvent(profile.Manifest.GameId, profile.Manifest.DisplayName, "PLAY", resp.MatchContext, resp.OverlayText);

                        if (_config.EnableOverlay)
                            _overlay.ShowCallout(resp.OverlayText, resp.Urgency, profile.Manifest.DisplayName, _config.OverlayDurationSeconds);

                        double voiceMs = 0;
                        long lastChatTicks = Interlocked.Read(ref _lastChatSpeechUtcTicks);
                        bool chatRecentlySpoke = !forceNow && lastChatTicks > 0 &&
                            (DateTimeOffset.UtcNow.UtcTicks - lastChatTicks) < TimeSpan.FromSeconds(3.5).Ticks;

                        if (!chatRecentlySpoke)
                        {
                            lock (_voiceGate)
                            {
                                if (_config.IsCoachVoiceActive && !_userPaused && !ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(resp.VoiceLine))
                                    voiceMs = _voice.SpeakInstant(resp.VoiceLine);
                            }
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
                    _stateMachine.Reset();

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
            _activeProfile?.Manifest.DisplayName ?? "(esperando juego)",
            _userPaused
        ));
    }

    public (string GameName, string Digest, int Count) GetMatchSessionDigest(bool isEn = false) =>
        _memory.BuildMatchReviewDigest(isEn: isEn);

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

            _producerTask = null;
            _consumerTask = null;
            _frameChannel = null;
            lock (_voiceGate) _voice.StopSpeaking();
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
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        await _router.DisposeAsync();
        if (_disposeVoice)
            lock (_voiceGate) _voice.Dispose();
        _capture.Dispose();
        _wakeSignal.Dispose();
        _lifecycleLock.Dispose();
    }
}
