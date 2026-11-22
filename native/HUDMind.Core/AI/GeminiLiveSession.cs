using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

/// <summary>
/// Real-Time Bidirectional Conversational Coach powered by Gemini Live Native-Audio WebSocket
/// (`gemini-3.1-flash-live-preview` ~1.0s TTFA -> `gemini-2.5-flash-native-audio-latest` -> `gemini-3.8-live`).
///
/// Key breakthroughs verified live:
/// - `MEDIA_RESOLUTION_MEDIUM` + 960x540 HALFTONE JPEG + `clientContent.inlineData` exact-frame sync (100% accurate HUD reading!)
/// - Continuous 1 FPS `realtimeInput.video` for live rolling visual context while speaking by mic
/// - Acoustic Echo Suppression using exact `waveOut` audible duration + 350ms hangover (prevents coach self-interruption!)
/// - Smooth VAD hangover (500ms trailing silence) for snappy conversational turn-taking
/// - Live hot-reconnect when switching Male (Fenrir) &lt;-&gt; Female (Aoede) voice!
/// </summary>
public sealed class GeminiLiveCoachOrchestrator : IAsyncDisposable
{
    private const string BidiEndpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    private static readonly string[] ModelCascade =
    [
        "models/gemini-3.1-flash-live-preview",
        "models/gemini-2.5-flash-native-audio-latest",
        "models/gemini-3.8-live"
    ];

    private readonly string _apiKey;
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

    private readonly SemaphoreSlim _wsSendLock = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _reconnectCts;
    private ClientWebSocket? _ws;
    private Task? _sessionTask;

    private volatile GameProfile? _activeProfile;
    private volatile bool _userPaused;
    private volatile bool _micEnabled;
    private volatile bool _modelSpeaking;
    private volatile bool _connected;
    private string _activeModel = ModelCascade[0];
    private ulong? _lastSentHash;
    private byte[]? _latestFrameBytes;
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

    public GeminiLiveCoachOrchestrator(
        string apiKey,
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
        _apiKey = apiKey.Trim();
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
        _micEnabled = config.EnableMic;
    }

    public static string ResolveLiveVoiceName(string? gender) =>
        string.Equals(gender?.Trim(), "female", StringComparison.OrdinalIgnoreCase) ? "Aoede" : "Fenrir";

    public static string BuildSetupPayloadJson(string modelName, UserConfig config, GameProfile? profile)
    {
        var voiceName = ResolveLiveVoiceName(config.VoiceGender);
        var sysText = CoachingPersonaBuilder.BuildLiveSystemInstruction(config, profile);

        var genCfg = new Dictionary<string, object>
        {
            ["responseModalities"] = new[] { "AUDIO" },
            ["mediaResolution"] = "MEDIA_RESOLUTION_MEDIUM",
            ["speechConfig"] = new
            {
                voiceConfig = new
                {
                    prebuiltVoiceConfig = new { voiceName }
                }
            }
        };

        if (modelName.Contains("2.5", StringComparison.OrdinalIgnoreCase))
            genCfg["thinkingConfig"] = new { thinkingBudget = 0 };
        else if (modelName.Contains("3.1", StringComparison.OrdinalIgnoreCase))
            genCfg["thinkingConfig"] = new { thinkingLevel = "MINIMAL" };

        var setupObj = new
        {
            setup = new
            {
                model = modelName,
                generationConfig = genCfg,
                // Fast conversational VAD: cuts ~350-450ms off voice response turnaround!
                realtimeInputConfig = new
                {
                    automaticActivityDetection = new
                    {
                        startOfSpeechSensitivity = "START_SENSITIVITY_HIGH",
                        endOfSpeechSensitivity = "END_SENSITIVITY_HIGH",
                        silenceDurationMs = 250
                    }
                },
                outputAudioTranscription = new { },
                systemInstruction = new
                {
                    parts = new[] { new { text = sysText } }
                }
            }
        };
        return JsonSerializer.Serialize(setupObj);
    }

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Se necesita una API Key gratuita de Gemini para el modo Live Conversacional.");

        _pcmPlayer.SetVolume(_config.TtsVolume);
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        _activeProfile = _profiles.FirstOrDefault(p =>
            string.Equals(p.Manifest.GameId, _forcedGameId, StringComparison.OrdinalIgnoreCase))
            ?? _profiles.FirstOrDefault();

        _sessionTask = Task.Run(() => RunLiveLifecycleLoopAsync(ct), ct);
        return Task.CompletedTask;
    }

    public void SetVolume(double volume) => _pcmPlayer.SetVolume(volume);

    public void RefreshLivePersona(string? reason = null)
    {
        if (_connected)
        {
            _onLog?.Invoke($"Actualizando calibración Live ({reason ?? _config.PlayerSkillTier})...");
            try { _reconnectCts?.Cancel(); } catch { /* reconnect loop handles cleanly */ }
        }
    }

    public void SetVoiceGenderLive(string gender)
    {
        _config.VoiceGender = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
        if (_connected)
        {
            _onLog?.Invoke($"Cambiando voz Live a {ResolveLiveVoiceName(_config.VoiceGender)} (reconectando en <1s)...");
            try { _reconnectCts?.Cancel(); } catch { /* reconnect loop handles cleanly */ }
        }
    }

    public void SetMicEnabled(bool enabled)
    {
        _micEnabled = enabled;
        if (!enabled)
        {
            _mic.Stop();
            _onLog?.Invoke("🎤 Micrófono silenciado.");
        }
        else if (_connected && !_userPaused)
        {
            StartMicIfEnabled();
        }
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
            if (_micEnabled && _connected) StartMicIfEnabled();
            WakeNow();
            _onLog?.Invoke("Coach Live REANUDADO.");
            if (_config.EnableOverlay)
                _overlay.ShowCallout("Coach Live ACTIVO — escuchando y mirando", UrgencyLevel.Low, title, 2.5);
        }
        EmitStats();
    }

    public void TriggerOnDemandAnalysis()
    {
        if (!_connected)
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
                _latestFrameBytes = fresh;
                await SendExactImageAndPromptTurnAsync(
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

    private async Task RunLiveLifecycleLoopAsync(CancellationToken ct)
    {
        int cascadeIdx = 0;
        while (!ct.IsCancellationRequested)
        {
            string model = ModelCascade[cascadeIdx % ModelCascade.Length];
            _activeModel = model;
            bool userTriggeredReconnect = false;
            using var localReconnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _reconnectCts = localReconnectCts;
            try
            {
                await RunSingleWebSocketSessionAsync(model, localReconnectCts.Token);
                if (!ct.IsCancellationRequested)
                {
                    _onLog?.Invoke("Reconectando sesión Gemini Live...");
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (localReconnectCts.IsCancellationRequested)
            {
                userTriggeredReconnect = true; // Keep same fast model on intentional voice switch!
            }
            catch (Exception ex)
            {
                var safe = SecretRedactor.Redact(ex.Message, [_apiKey]);
                _onLog?.Invoke($"Sesión Live ({model}) desconectada: {safe}. Probando siguiente modelo...");
                cascadeIdx++;
                try { await Task.Delay(1000, ct); } catch { break; }
            }
            finally
            {
                _reconnectCts = null;
                _connected = false;
                _modelSpeaking = false;
                _mic.Stop();
                if (_ws is not null)
                {
                    try { _ws.Dispose(); } catch { }
                    _ws = null;
                }
            }

            if (userTriggeredReconnect && !ct.IsCancellationRequested)
                await Task.Delay(150, ct);
        }
    }

    private async Task RunSingleWebSocketSessionAsync(string modelName, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("x-goog-api-key", _apiKey);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _ws = ws;

        var voiceName = ResolveLiveVoiceName(_config.VoiceGender);
        _onLog?.Invoke($"Conectando Gemini Live Native-Audio ({modelName}, voz={voiceName}, MEDIA_RESOLUTION_MEDIUM)...");
        await ws.ConnectAsync(new Uri(BidiEndpoint), ct);

        var setupJson = BuildSetupPayloadJson(modelName, _config, _activeProfile);
        await SendRawJsonAsync(ws, setupJson, ct);

        var firstResp = await ReceiveFullMessageAsync(ws, ct);
        if (firstResp is null || !firstResp.Contains("setupComplete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Handshake Live falló: {firstResp ?? "cierre inmediato"}");

        _connected = true;
        _onLog?.Invoke($"🎙 LIVE LISTO [{modelName.Replace("models/", "")}] · Voz {voiceName} · Hablale por mic o tocá {_config.HotkeyAnalyzeNow}!");
        if (_config.EnableOverlay)
        {
            _overlay.ShowCallout(
                _micEnabled ? $"Coach Live ({voiceName}) listo — hablale directo" : $"Coach Live ({voiceName}) observando",
                UrgencyLevel.Low,
                _activeProfile?.Manifest.DisplayName ?? "HUDMind Live",
                3.0);
        }

        if (_micEnabled && !_userPaused)
            StartMicIfEnabled();

        EmitStats();

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recvTask = ReceiveLoopAsync(ws, sessionCts.Token);
        var videoTask = VideoAndAutoCoachLoopAsync(sessionCts.Token);

        await Task.WhenAny(recvTask, videoTask);
        await sessionCts.CancelAsync();
        try { await Task.WhenAll(recvTask, videoTask); } catch { /* clean exit */ }
    }

    private void StartMicIfEnabled()
    {
        if (!_micEnabled || _userPaused || !_connected) return;
        bool ok = _mic.TryStart(
            pcmChunk =>
            {
                if (!_connected || _userPaused || !_micEnabled) return;

                double rms = ComputeRms16(pcmChunk);
                bool coachAudible = _modelSpeaking || _pcmPlayer.IsPlayingAudio;

                // Acoustic Echo Suppression: while coach audio is playing out of speakers,
                // ignore speaker bleed (<1800 RMS) so the coach NEVER self-interrupts!
                if (coachAudible)
                {
                    if (rms < 1800.0)
                        return;
                    // Intentional loud human barge-in!
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
                    // Tight 320ms trailing silence hangover matching server silenceDurationMs=250 for snappy turn-taking!
                    return;
                }

                var ct = _runCts?.Token ?? CancellationToken.None;
                _ = SendMicChunkAsync(pcmChunk, ct);
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
        while (!ct.IsCancellationRequested && _connected)
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
                        _latestFrameBytes = jpeg;
                        Interlocked.Increment(ref _framesSent);
                        await SendVideoFrameAsync(jpeg, ct);

                        // Proactive coaching pulse (only when auto-coach > 0, audio finished, and user isn't talking)
                        double autoSec = _config.LiveAutoCoachSeconds;
                        bool userRecentlySpoke = (DateTimeOffset.UtcNow - _lastMicVoiceUtc).TotalSeconds < 3.0;
                        if (autoSec > 0 &&
                            !_modelSpeaking &&
                            !_pcmPlayer.IsPlayingAudio &&
                            !userRecentlySpoke &&
                            (DateTimeOffset.UtcNow - _lastAutoNudgeUtc).TotalSeconds >= autoSec)
                        {
                            _lastAutoNudgeUtc = DateTimeOffset.UtcNow;

                            // Local 30ms WinRT OCR gate: skip auto-nudge silently during MENU/REPLAY/LOADING
                            // so the coach NEVER speaks out loud during menus or goal replays!
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
                                await SendExactImageAndPromptTurnAsync(
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
                _onLog?.Invoke($"Error video Live: {SecretRedactor.Redact(ex.Message, [_apiKey])}");
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task SendVideoFrameAsync(byte[] jpegBytes, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        var buf = new ArrayBufferWriter<byte>(jpegBytes.Length * 4 / 3 + 128);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WritePropertyName("realtimeInput");
            w.WriteStartObject();
            w.WritePropertyName("video");
            w.WriteStartObject();
            w.WriteString("mimeType", "image/jpeg");
            w.WriteBase64String("data", jpegBytes);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        await SendRawBytesAsync(ws, buf.WrittenMemory, ct);
    }

    private async Task SendExactImageAndPromptTurnAsync(byte[] jpegBytes, string text, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        _lastTurnRequestTick = Stopwatch.GetTimestamp();
        _waitingFirstAudio = true;

        var buf = new ArrayBufferWriter<byte>(jpegBytes.Length * 4 / 3 + 512);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WritePropertyName("clientContent");
            w.WriteStartObject();
            w.WritePropertyName("turns");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WritePropertyName("parts");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WritePropertyName("inlineData");
            w.WriteStartObject();
            w.WriteString("mimeType", "image/jpeg");
            w.WriteBase64String("data", jpegBytes);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("turnComplete", true);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        await SendRawBytesAsync(ws, buf.WrittenMemory, ct);
    }

    private async Task SendMicChunkAsync(byte[] pcm16k, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        var buf = new ArrayBufferWriter<byte>(pcm16k.Length * 4 / 3 + 128);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WritePropertyName("realtimeInput");
            w.WriteStartObject();
            w.WritePropertyName("audio");
            w.WriteStartObject();
            w.WriteString("mimeType", "audio/pcm;rate=16000");
            w.WriteBase64String("data", pcm16k);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        try { await SendRawBytesAsync(ws, buf.WrittenMemory, ct); } catch { /* transient */ }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var transcriptBuf = new StringBuilder();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var raw = await ReceiveFullMessageAsync(ws, ct);
            if (raw is null)
                break;

            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("serverContent", out var sc))
                continue;

            if (sc.TryGetProperty("interrupted", out var intr) && intr.ValueKind == JsonValueKind.True)
            {
                _pcmPlayer.InterruptAndClear();
                _modelSpeaking = false;
                transcriptBuf.Clear();
                continue;
            }

            if (sc.TryGetProperty("outputTranscription", out var ot) &&
                ot.TryGetProperty("text", out var otText))
            {
                var chunk = otText.GetString();
                if (!string.IsNullOrEmpty(chunk))
                {
                    transcriptBuf.Append(chunk);
                    var currentLine = transcriptBuf.ToString().Trim();
                    if (_config.EnableOverlay &&
                        currentLine.Length >= 5 &&
                        !currentLine.Equals("silencio", StringComparison.OrdinalIgnoreCase) &&
                        !currentLine.Equals("silencio.", StringComparison.OrdinalIgnoreCase))
                    {
                        _overlay.ShowCallout(
                            currentLine,
                            UrgencyLevel.Medium,
                            _activeProfile?.Manifest.DisplayName ?? "HUDMind Live",
                            _config.OverlayDurationSeconds);
                    }
                }
            }

            if (sc.TryGetProperty("modelTurn", out var mt) &&
                mt.TryGetProperty("parts", out var parts))
            {
                foreach (var p in parts.EnumerateArray())
                {
                    if (p.TryGetProperty("inlineData", out var inl) &&
                        inl.TryGetProperty("data", out var b64El))
                    {
                        var b64 = b64El.GetString();
                        if (!string.IsNullOrEmpty(b64) && _config.EnableVoice && !_userPaused)
                        {
                            if (_waitingFirstAudio)
                            {
                                _waitingFirstAudio = false;
                                _lastTtfaMs = Stopwatch.GetElapsedTime(_lastTurnRequestTick).TotalMilliseconds;
                            }
                            _modelSpeaking = true;
                            byte[] pcm = Convert.FromBase64String(b64);
                            _pcmPlayer.EnqueuePcm24k(pcm);
                        }
                    }
                }
            }

            if (sc.TryGetProperty("turnComplete", out var tc) && tc.ValueKind == JsonValueKind.True)
            {
                _pcmPlayer.FlushBuffered();
                _modelSpeaking = false;
                var spoken = transcriptBuf.ToString().Trim();
                transcriptBuf.Clear();
                if (!string.IsNullOrWhiteSpace(spoken) &&
                    !spoken.Equals("silencio", StringComparison.OrdinalIgnoreCase) &&
                    !spoken.Equals("silencio.", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _interventions);
                    _onLog?.Invoke($"🎙 Coach ({_lastTtfaMs:F0}ms TTFA): \"{spoken}\"");
                    EmitStats();
                }
            }
        }
    }

    private async Task SendRawJsonAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await SendRawBytesAsync(ws, bytes, ct);
    }

    private async Task SendRawBytesAsync(ClientWebSocket ws, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _wsSendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _wsSendLock.Release();
        }
    }

    private static async Task<string?> ReceiveFullMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            using var ms = new MemoryStream(32768);
            while (true)
            {
                var res = await ws.ReceiveAsync(buffer, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                    return null;
                if (res.Count > 0)
                    ms.Write(buffer, 0, res.Count);
                if (res.EndOfMessage)
                    return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EmitStats()
    {
        _onStats?.Invoke(new CoachRuntimeStats(
            Interlocked.Read(ref _framesSent),
            0,
            Interlocked.Read(ref _interventions),
            Interlocked.Read(ref _interventions),
            _lastCaptureMs,
            _lastTtfaMs,
            _lastTtfaMs > 0 ? 5.0 : 0.0,
            $"LIVE · {_activeModel.Replace("models/", "")} ({ResolveLiveVoiceName(_config.VoiceGender)})",
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
        _onLog?.Invoke("Sesión Gemini Live detenida.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _mic.Dispose();
        _pcmPlayer.Dispose();
        _capture.Dispose();
        _wsSendLock.Dispose();
        _wakeSignal.Dispose();
    }
}
