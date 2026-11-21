using System.Diagnostics;
using System.Threading.Channels;
using HUDMind.Core.AI;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;

namespace HUDMind.Core.Engine;

/// <summary>
/// Event-driven bounded-channel (DropOldest) real-time coach engine with zero-wait F10 wakeup.
/// </summary>
public sealed class CoachOrchestrator : IAsyncDisposable
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
    private readonly bool _testMode;
    private readonly string? _forcedGameId;

    private readonly InterventionStateMachine _stateMachine = new();
    private readonly InterventionMemory _memory = new(maxItems: 12, ttlSeconds: 420.0);
    private readonly Channel<CapturedFrameData> _frameChannel;

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource _wakeCts = new();
    private readonly object _wakeLock = new();
    private Task? _producerTask;
    private Task? _consumerTask;

    private GameProfile? _activeProfile;
    private ulong? _lastAcceptedHash;
    private volatile bool _userPaused;
    private volatile bool _forceAnalysisPending;

    private long _framesCaptured;
    private long _framesFiltered;
    private long _aiCalls;
    private long _interventions;
    private double _lastCaptureMs;
    private double _lastAiMs;
    private double _lastVoiceMs;

    public bool IsRunning => _runCts is { IsCancellationRequested: false };
    public bool IsPaused => _userPaused;

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
        Action<CoachRuntimeStats>? onStats = null)
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

        _frameChannel = Channel.CreateBounded<CapturedFrameData>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public async Task StartAsync()
    {
        if (IsRunning)
            return;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        if (_config.EnableVoice)
            _voice.WarmUp(_config.VoiceLanguage, _config.TtsVolume);

        await _router.PrewarmAsync(ct);

        if (_testMode)
        {
            _activeProfile = _profiles.FirstOrDefault(p =>
                string.Equals(p.Manifest.GameId, _forcedGameId, StringComparison.OrdinalIgnoreCase))
                ?? _profiles.FirstOrDefault();

            if (_activeProfile is not null)
            {
                _onLog?.Invoke($"[TEST-MODE] Perfil activo: {_activeProfile.Manifest.DisplayName} | Voz: {_voice.EngineName}");
                if (_config.EnableOverlay)
                    _overlay.ShowCallout("Coach Nativo .NET 9 ACTIVO — observando", UrgencyLevel.Low, _activeProfile.Manifest.DisplayName, 3.5);
            }
        }
        else
        {
            _onLog?.Invoke($"[AUTO-DETECT] Esperando juego registrado ({_profiles.Count} perfiles) | Voz: {_voice.EngineName}");
        }

        _producerTask = Task.Run(() => ProducerLoopAsync(ct), ct);
        _consumerTask = Task.Run(() => ConsumerLoopAsync(ct), ct);
        EmitStats();
    }

    public void TogglePause()
    {
        _userPaused = !_userPaused;
        var title = _activeProfile?.Manifest.DisplayName ?? "HUDMind";
        if (_userPaused)
        {
            _voice.StopSpeaking();
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
    }

    public void TriggerOnDemandAnalysis()
    {
        if (_activeProfile is null && _profiles.Count > 0 && _testMode)
            _activeProfile = _profiles[0];

        if (_activeProfile is null)
        {
            _onLog?.Invoke("F10 ignorado: todavía no hay un juego activo detectado.");
            return;
        }

        _userPaused = false;
        _forceAnalysisPending = true;
        WakeNow();
        _onLog?.Invoke($"Análisis inmediato ({_config.HotkeyAnalyzeNow}) disparado (<1ms wake).");
        if (_config.EnableOverlay)
            _overlay.ShowCallout("Analizando jugada...", UrgencyLevel.Low, _activeProfile.Manifest.DisplayName, 1.8);
    }

    public void WakeNow()
    {
        lock (_wakeLock)
        {
            try { _wakeCts.Cancel(); } catch { /* ignored */ }
        }
    }

    private async Task WaitInterruptibleAsync(TimeSpan delay, CancellationToken ct)
    {
        CancellationTokenSource localWake;
        lock (_wakeLock)
        {
            if (_wakeCts.IsCancellationRequested)
            {
                _wakeCts.Dispose();
                _wakeCts = new CancellationTokenSource();
            }
            localWake = _wakeCts;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, localWake.Token);
        try
        {
            await Task.Delay(delay, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Woken up early via WakeNow()!
        }
    }

    private (int W, int H, int Quality) ResolveTargetResolution() =>
        _config.TokenEfficiencyMode.ToLowerInvariant() switch
        {
            "eco" => (512, 288, Math.Min(_config.ImageCompressionQuality, 58)),
            "balanced" => (640, 360, Math.Min(_config.ImageCompressionQuality, 68)),
            _ => (960, 540, Math.Min(_config.ImageCompressionQuality, 78))
        };

    private double EffectiveCooldown(GameProfile profile)
    {
        var baseCd = profile.Manifest.CooldownSeconds;
        return _config.CoachMode.Equals("training", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(3.5, Math.Round(baseCd * 0.7, 1))
            : baseCd;
    }

    private async Task ProducerLoopAsync(CancellationToken ct)
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
                    var matched = _profiles.FirstOrDefault(p => p.MatchesProcess(fg));
                    if (matched is not null && !ReferenceEquals(matched, _activeProfile))
                    {
                        _activeProfile = matched;
                        _lastAcceptedHash = null;
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

                bool forced = _forceAnalysisPending;
                bool canRun = forced
                    ? !_stateMachine.IsBusy
                    : _stateMachine.CanAnalyze(EffectiveCooldown(profile), _config.MinInferenceIntervalSeconds);

                if (canRun)
                {
                    var (w, h, q) = ResolveTargetResolution();
                    var region = _testMode ? null : _windowDetector.GetForegroundClientRect();
                    var frame = _capture.CaptureCompressedFrame(
                        _config.CaptureMonitor, region, w, h, q, Interlocked.Increment(ref frameCounter));

                    if (frame is not null)
                    {
                        Interlocked.Increment(ref _framesCaptured);
                        _lastCaptureMs = frame.CaptureMs + frame.EncodeMs;
                        _frameChannel.Writer.TryWrite(frame);
                    }
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
                _onLog?.Invoke($"Error en captura: {ex.Message}");
                await WaitInterruptibleAsync(TimeSpan.FromMilliseconds(500), ct);
            }
        }
    }

    private async Task ConsumerLoopAsync(CancellationToken ct)
    {
        await foreach (var frame in _frameChannel.Reader.ReadAllAsync(ct))
        {
            var profile = _activeProfile;
            if (profile is null || _userPaused)
                continue;

            bool forceNow = _forceAnalysisPending;
            if (forceNow)
                _forceAnalysisPending = false;

            if (!forceNow)
            {
                if (_lastAcceptedHash.HasValue)
                {
                    int dist = PerceptualHasher.HammingDistance(frame.PerceptualHash, _lastAcceptedHash.Value);
                    if (dist < profile.Manifest.PhashThreshold)
                    {
                        Interlocked.Increment(ref _framesFiltered);
                        continue;
                    }
                }

                if (!_stateMachine.CanAnalyze(EffectiveCooldown(profile), _config.MinInferenceIntervalSeconds))
                {
                    Interlocked.Increment(ref _framesFiltered);
                    continue;
                }
            }

            if (!_stateMachine.TryBeginAnalysis())
                continue;

            _lastAcceptedHash = frame.PerceptualHash;
            bool delivered = false;
            try
            {
                Interlocked.Increment(ref _aiCalls);
                var history = _memory.FormatCompactHistory();
                var (resp, aiMs, model) = await _router.AnalyzeFrameAsync(
                    frame.JpegBytes, profile, _config, history, forceNow, ct);
                _lastAiMs = aiMs;

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

                        if (_config.EnableOverlay)
                            _overlay.ShowCallout(resp.OverlayText, resp.Urgency, profile.Manifest.DisplayName, _config.OverlayDurationSeconds);

                        double voiceMs = 0;
                        if (_config.EnableVoice && !string.IsNullOrWhiteSpace(resp.VoiceLine))
                            voiceMs = _voice.SpeakInstant(resp.VoiceLine);

                        _lastVoiceMs = voiceMs;
                        double totalMs = frame.CaptureMs + frame.EncodeMs + aiMs + voiceMs;
                        _onLog?.Invoke(
                            $"[{resp.Urgency.ToString().ToUpperInvariant()}] \"{resp.OverlayText}\" | " +
                            $"TOTAL={totalMs:F0}ms (cap={frame.CaptureMs + frame.EncodeMs:F0}ms + IA={aiMs:F0}ms + voz={voiceMs:F1}ms) [{model}]");
                    }
                }
                else
                {
                    _onLog?.Invoke($"Sin intervención ({aiMs:F0}ms, {frame.JpegBytes.Length / 1024.0:F1}KB) [{model}]");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Error IA: {ex.Message}");
            }
            finally
            {
                _stateMachine.EndAnalysis(delivered);
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
            _lastCaptureMs,
            _lastAiMs,
            _lastVoiceMs,
            _router.ActiveProviderLabel,
            _activeProfile?.Manifest.DisplayName ?? "(esperando juego)"
        ));
    }

    public async Task StopAsync()
    {
        if (_runCts is null)
            return;

        await _runCts.CancelAsync();
        WakeNow();
        _frameChannel.Writer.TryComplete();

        var tasks = new[] { _producerTask, _consumerTask }.Where(t => t is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks); } catch { /* clean shutdown */ }
        }

        _voice.StopSpeaking();
        _runCts.Dispose();
        _runCts = null;
        _onLog?.Invoke("Motor nativo HUDMind detenido.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _router.DisposeAsync();
        _voice.Dispose();
        _capture.Dispose();
        _wakeCts.Dispose();
    }
}
