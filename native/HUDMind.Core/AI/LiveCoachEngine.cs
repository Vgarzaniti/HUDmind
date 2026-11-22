using System.Diagnostics;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

/// <summary>
/// Provider-agnostic contract for any bidirectional real-time Audio+Video AI WebSocket backend
/// (e.g. Gemini Live, OpenAI Realtime API, xAI Realtime, etc.).
/// </summary>
public interface ILiveRealtimeBackend : IAsyncDisposable
{
    string ProviderName { get; }
    string ActiveModelLabel { get; }
    bool IsConnected { get; }

    string ResolveVoiceName(string? gender);

    Task RunSessionLoopAsync(
        UserConfig config,
        GameProfile? profile,
        Action<byte[]> onPcm24kAudio,
        Action<string> onPartialTranscript,
        Action<string> onTurnComplete,
        Action onInterrupted,
        Action<string> onLog,
        Action onConnected,
        CancellationToken sessionCt);

    Task SendVideoFrameAsync(byte[] jpegBytes, CancellationToken ct);
    Task SendMicAudio16kAsync(byte[] pcm16k, CancellationToken ct);
    Task SendImageAndPromptTurnAsync(byte[] jpegBytes, string promptText, CancellationToken ct);
    Task SendTextTurnAsync(string promptText, CancellationToken ct);
}

public static class LiveBackendFactory
{
    /// <summary>
    /// Selects the highest-priority enabled account that supports real-time streaming audio+video.
    /// Extensible to OpenAI Realtime / xAI / future providers without modifying the orchestrator or UI.
    /// </summary>
    public static ILiveRealtimeBackend? TryCreateFromAccounts(IEnumerable<ProviderAccount> accounts)
    {
        foreach (var acc in accounts.Where(a => a.IsReady()).OrderBy(a => a.Priority))
        {
            if (acc.Provider.Equals("gemini", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(acc.ApiKey))
            {
                return new LiveBackends.GeminiLiveBackend(acc.ApiKey);
            }
        }
        return null;
    }
}

/// <summary>
/// Provider-agnostic Real-Time Conversational Coach Engine.
/// Coordinates 1 FPS sharp screen capture, 16kHz live microphone with Acoustic Echo Suppression &amp; VAD,
/// 24kHz zero-stutter PCM playback, local WinRT OCR menu/replay silence gate, barge-in, and live overlay.
/// Works identically with any <see cref="ILiveRealtimeBackend"/> implementation.
/// </summary>
public sealed class RealtimeLiveCoachOrchestrator : IAsyncDisposable
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
    private readonly bool _testMode;
    private readonly string? _forcedGameId;
    private readonly Action<string>? _onLog;
    private readonly Action<CoachRuntimeStats>? _onStats;
    private readonly Action<bool>? _onPauseChanged;

    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _reconnectCts;
    private Task? _sessionTask;

    private volatile GameProfile? _activeProfile;
    private volatile bool _userPaused;
    private volatile bool _micEnabled;
    private volatile bool _modelSpeaking;
    private ulong? _lastSentHash;
    private long _framesSent;
    private long _interventions;
    private double _lastCaptureMs;
    private double _lastTtfaMs;
    private long _lastTurnRequestTick;
    private bool _waitingFirstAudio;
    private DateTimeOffset _lastAutoNudgeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMicVoiceUtc = DateTimeOffset.MinValue;

    public bool IsRunning => _runCts is { IsCancellationRequested: false };
    public bool IsPaused => _userPaused;
    public string ActiveVoiceLabel => _backend.ResolveVoiceName(_config.VoiceGender);

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
        ITextOnlySensorService? localPhaseGate = null)
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
        _testMode = testMode;
        _forcedGameId = forcedGameId;
        _onLog = onLog;
        _onStats = onStats;
        _onPauseChanged = onPauseChanged;
        _micEnabled = config.IsMicActive;
    }

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;

        _pcmPlayer.SetVolume(_config.TtsVolume);
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        _activeProfile = _profiles.FirstOrDefault(p =>
            string.Equals(p.Manifest.GameId, _forcedGameId, StringComparison.OrdinalIgnoreCase))
            ?? _profiles.FirstOrDefault();

        _sessionTask = Task.Run(() => LifecycleLoopAsync(ct), ct);
        return Task.CompletedTask;
    }

    public void SetVolume(double volume) => _pcmPlayer.SetVolume(volume);

    public void RefreshLivePersona(string? reason = null)
    {
        if (_backend.IsConnected)
        {
            _onLog?.Invoke($"Actualizando sesión Live ({reason ?? _config.PlayerSkillTier})...");
            try { _reconnectCts?.Cancel(); } catch { /* reconnect loop handles cleanly */ }
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
            _mic.Stop();
            _onLog?.Invoke("🎤 Micrófono silenciado.");
        }
        else if (_backend.IsConnected && !_userPaused)
        {
            StartMicIfEnabled();
        }
    }

    public void SetCommunicationMode(string mode)
    {
        _config.SetCommunicationMode(mode);
        _micEnabled = _config.IsMicActive;

        if (_config.IsTextOnlyMode)
        {
            _mic.Stop();
            _pcmPlayer.InterruptAndClear();
            _modelSpeaking = false;
            _onLog?.Invoke("💬 Modo SOLO TEXTO activo (micrófono y voz silenciados; usá el chat escrito y overlay).");
        }
        else if (_config.IsCoachVoiceOnlyMode)
        {
            _mic.Stop();
            _onLog?.Invoke("🔊 Modo SOLO VOZ DEL COACH activo (micrófono apagado; solo te habla el coach).");
        }
        else
        {
            _onLog?.Invoke("🎙 Modo CHARLA activo (micrófono en vivo + voz del coach).");
            if (_backend.IsConnected && !_userPaused)
                StartMicIfEnabled();
        }
        EmitStats();
    }

    public async Task SendUserTextAsync(string userText, bool attachCurrentScreen = true)
    {
        var msg = (userText ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(msg))
            return;

        if (!_backend.IsConnected)
        {
            _onLog?.Invoke("Esperando conexión Live WebSocket para enviar tu mensaje...");
            return;
        }

        if (_userPaused)
        {
            _userPaused = false;
            _onPauseChanged?.Invoke(false);
            if (_micEnabled) StartMicIfEnabled();
        }

        _onLog?.Invoke($"💬 Vos: \"{msg}\"");
        var ct = _runCts?.Token ?? CancellationToken.None;
        _lastTurnRequestTick = Stopwatch.GetTimestamp();
        _waitingFirstAudio = true;

        byte[]? fresh = attachCurrentScreen
            ? CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _framesSent))
            : null;

        if (fresh is not null)
            await _backend.SendImageAndPromptTurnAsync(fresh, msg, ct);
        else
            await _backend.SendTextTurnAsync(msg, ct);
    }

    public void TogglePause()
    {
        _userPaused = !_userPaused;
        _onPauseChanged?.Invoke(_userPaused);
        var title = _activeProfile?.Manifest.DisplayName ?? "HUDMind Live";

        if (_userPaused)
        {
            _pcmPlayer.InterruptAndClear();
            _mic.Stop();
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
                _overlay.ShowCallout(
                    _config.IsTextOnlyMode ? "Coach Live ACTIVO — solo texto" : _micEnabled ? "Coach Live ACTIVO — charla en vivo" : "Coach Live ACTIVO — solo voz del coach",
                    UrgencyLevel.Low, title, 2.5);
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
        if (_userPaused)
        {
            _userPaused = false;
            _onPauseChanged?.Invoke(false);
            if (_micEnabled) StartMicIfEnabled();
        }

        _ = Task.Run(async () =>
        {
            var ct = _runCts?.Token ?? CancellationToken.None;
            var fresh = CaptureSharpFrame(force: true, frameId: Interlocked.Increment(ref _framesSent));
            if (fresh is not null)
            {
                _lastTurnRequestTick = Stopwatch.GetTimestamp();
                _waitingFirstAudio = true;
                await _backend.SendImageAndPromptTurnAsync(
                    fresh,
                    "Mirá esta imagen actual exacta del juego. Decime en 1 oración corta y clara qué está pasando y qué me conviene hacer ahora.",
                    ct);
            }
        });
    }

    private void WakeNow()
    {
        if (_wakeSignal.CurrentCount == 0)
        {
            try { _wakeSignal.Release(); } catch { /* latched */ }
        }
    }

    private async Task LifecycleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var localReconnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _reconnectCts = localReconnectCts;
            Task? videoTask = null;

            try
            {
                await _backend.RunSessionLoopAsync(
                    _config,
                    _activeProfile,
                    onPcm24kAudio: HandleIncomingAudioChunk,
                    onPartialTranscript: HandlePartialTranscript,
                    onTurnComplete: HandleTurnComplete,
                    onInterrupted: HandleInterrupted,
                    onLog: msg => _onLog?.Invoke(msg),
                    onConnected: () =>
                    {
                        var voiceName = _backend.ResolveVoiceName(_config.VoiceGender);
                        string modeDesc = _config.IsTextOnlyMode
                            ? "💬 SOLO TEXTO (silencioso — escribí abajo o tocá F10)"
                            : _config.IsCoachVoiceOnlyMode
                                ? $"🔊 SOLO VOZ COACH ({voiceName} · mic apagado)"
                                : $"🎙 CHARLA ({voiceName} · hablale por mic o escribí)";

                        _onLog?.Invoke($"LIVE LISTO [{_backend.ActiveModelLabel}] · {modeDesc}");
                        if (_config.EnableOverlay)
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
                // Intentional hot-reconnect (voice/rank changed)
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Sesión Live interrumpida: {SecretRedactor.Redact(ex.Message)}. Reconectando...");
                try { await Task.Delay(900, ct); } catch { break; }
            }
            finally
            {
                _reconnectCts = null;
                _modelSpeaking = false;
                _mic.Stop();
                if (videoTask is not null)
                {
                    try { await videoTask; } catch { /* clean exit */ }
                }
            }

            if (!ct.IsCancellationRequested)
                try { await Task.Delay(250, ct); } catch { break; }
        }
    }

    private void HandleIncomingAudioChunk(byte[] pcm24k)
    {
        if (!_config.IsCoachVoiceActive || _userPaused || pcm24k.Length == 0) return;
        if (_waitingFirstAudio)
        {
            _waitingFirstAudio = false;
            _lastTtfaMs = Stopwatch.GetElapsedTime(_lastTurnRequestTick).TotalMilliseconds;
        }
        _modelSpeaking = true;
        _pcmPlayer.EnqueuePcm24k(pcm24k);
    }

    private void HandlePartialTranscript(string currentLine)
    {
        if (_waitingFirstAudio && _config.IsTextOnlyMode && !string.IsNullOrWhiteSpace(currentLine))
        {
            _waitingFirstAudio = false;
            _lastTtfaMs = Stopwatch.GetElapsedTime(_lastTurnRequestTick).TotalMilliseconds;
        }
        if (_config.EnableOverlay && currentLine.Length >= 5)
        {
            _overlay.ShowCallout(
                currentLine,
                UrgencyLevel.Medium,
                _activeProfile?.Manifest.DisplayName ?? "HUDMind Live",
                _config.OverlayDurationSeconds);
        }
    }

    private void HandleTurnComplete(string spoken)
    {
        _pcmPlayer.FlushBuffered();
        _modelSpeaking = false;
        if (!string.IsNullOrWhiteSpace(spoken))
        {
            Interlocked.Increment(ref _interventions);
            string icon = _config.IsTextOnlyMode ? "💬" : (_config.IsCoachVoiceOnlyMode ? "🔊" : "🎙");
            _onLog?.Invoke($"{icon} Coach ({_lastTtfaMs:F0}ms): \"{spoken}\"");
            EmitStats();
        }
    }

    private void HandleInterrupted()
    {
        _pcmPlayer.InterruptAndClear();
        _modelSpeaking = false;
    }

    private void StartMicIfEnabled()
    {
        if (!_micEnabled || _userPaused || !_backend.IsConnected) return;
        bool ok = _mic.TryStart(
            pcmChunk =>
            {
                if (!_backend.IsConnected || _userPaused || !_micEnabled) return;

                double rms = ComputeRms16(pcmChunk);
                bool coachAudible = _modelSpeaking || _pcmPlayer.IsPlayingAudio;

                // Acoustic Echo Suppression: ignore speaker bleed while coach speaks
                if (coachAudible)
                {
                    if (rms < 1800.0)
                        return;
                    _pcmPlayer.InterruptAndClear();
                }

                var now = DateTimeOffset.UtcNow;
                if (rms >= 320.0)
                {
                    _lastMicVoiceUtc = now;
                    _lastTurnRequestTick = Stopwatch.GetTimestamp();
                    _waitingFirstAudio = true;
                }
                else if ((now - _lastMicVoiceUtc).TotalMilliseconds > 320.0)
                {
                    return;
                }

                var ct = _runCts?.Token ?? CancellationToken.None;
                _ = _backend.SendMicAudio16kAsync(pcmChunk, ct);
            },
            err => _onLog?.Invoke(err));
        if (ok)
            _onLog?.Invoke("🎤 Micrófono 16kHz con supresión de eco activo — hablá cuando quieras.");
    }

    private static double ComputeRms16(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        if (samples == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            sumSq += s * (double)s;
        }
        return Math.Sqrt(sumSq / samples);
    }

    private byte[]? CaptureSharpFrame(bool force, long frameId)
    {
        var region = _testMode ? null : _windowDetector.GetForegroundClientRect();
        var (frame, skipped) = _capture.CaptureIfChanged(
            _config.CaptureMonitor,
            region,
            targetWidth: 960,
            targetHeight: 540,
            jpegQuality: 78,
            frameId: frameId,
            lastAcceptedHash: _lastSentHash,
            phashThreshold: 6,
            isForced: force);

        if (skipped || frame is null)
            return null;

        _lastSentHash = frame.PerceptualHash;
        _lastCaptureMs = frame.CaptureMs + frame.EncodeMs;
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
                        var matched = _profiles.FirstOrDefault(p => p.MatchesProcess(fg));
                        if (matched is null &&
                            string.Equals(_forcedGameId, "universal", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(fg) &&
                            !fg.Equals("HUDMind.exe", StringComparison.OrdinalIgnoreCase) &&
                            !fg.Equals("HUDMind.Native.exe", StringComparison.OrdinalIgnoreCase) &&
                            !fg.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            matched = _profiles.FirstOrDefault(p => p.Manifest.GameId == "universal");
                        }
                        if (matched is not null)
                            _activeProfile = matched;
                        else
                        {
                            await Task.Delay(700, ct);
                            continue;
                        }
                    }

                    var jpeg = CaptureSharpFrame(force: false, frameId: ++frameCounter);
                    if (jpeg is not null)
                    {
                        Interlocked.Increment(ref _framesSent);
                        await _backend.SendVideoFrameAsync(jpeg, ct);

                        double autoSec = _config.LiveAutoCoachSeconds;
                        bool userRecentlySpoke = (DateTimeOffset.UtcNow - _lastMicVoiceUtc).TotalSeconds < 3.0;
                        if (autoSec > 0 &&
                            !_modelSpeaking &&
                            !_pcmPlayer.IsPlayingAudio &&
                            !userRecentlySpoke &&
                            (DateTimeOffset.UtcNow - _lastAutoNudgeUtc).TotalSeconds >= autoSec)
                        {
                            _lastAutoNudgeUtc = DateTimeOffset.UtcNow;

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
                                _lastTurnRequestTick = Stopwatch.GetTimestamp();
                                _waitingFirstAudio = true;
                                await _backend.SendImageAndPromptTurnAsync(
                                    jpeg,
                                    "Mirá esta captura exacta de la partida actual. Da 1 solo consejo táctico claro, natural y útil en español (8 a 14 palabras) basado únicamente en lo que ves en pantalla.",
                                    ct);
                            }
                        }
                    }
                }

                try { await _wakeSignal.WaitAsync(TimeSpan.FromMilliseconds(1000), ct); }
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
            _lastCaptureMs,
            _lastTtfaMs,
            _config.IsTextOnlyMode ? 0.0 : (_lastTtfaMs > 0 ? 5.0 : 0.0),
            $"LIVE · {_backend.ActiveModelLabel} · {modeTag}",
            _activeProfile?.Manifest.DisplayName ?? "Universal",
            _userPaused
        ));
    }

    public async Task StopAsync()
    {
        if (_runCts is null) return;
        await _runCts.CancelAsync();
        WakeNow();
        _mic.Stop();
        _pcmPlayer.InterruptAndClear();
        if (_sessionTask is not null)
        {
            try { await _sessionTask; } catch { /* clean stop */ }
            _sessionTask = null;
        }
        _runCts.Dispose();
        _runCts = null;
        _onLog?.Invoke("Sesión Live detenida.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _backend.DisposeAsync();
        _mic.Dispose();
        _pcmPlayer.Dispose();
        _capture.Dispose();
        _wakeSignal.Dispose();
    }
}
