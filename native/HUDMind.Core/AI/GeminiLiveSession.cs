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
/// (`gemini-3.1-flash-live-preview` ~960ms TTFA -> `gemini-2.5-flash-native-audio-latest` -> `gemini-3.8-live`).
/// Streams continuous 1 FPS game video + live 16kHz microphone audio and plays streamed 24kHz human audio
/// directly as chunks arrive (zero separate TTS, instant barge-in interruption, continuous match memory!).
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
    private readonly bool _testMode;
    private readonly string? _forcedGameId;
    private readonly Action<string>? _onLog;
    private readonly Action<CoachRuntimeStats>? _onStats;
    private readonly Action<bool>? _onPauseChanged;

    private readonly SemaphoreSlim _wsSendLock = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private CancellationTokenSource? _runCts;
    private ClientWebSocket? _ws;
    private Task? _sessionTask;

    private volatile GameProfile? _activeProfile;
    private volatile bool _userPaused;
    private volatile bool _micEnabled;
    private volatile bool _modelSpeaking;
    private volatile bool _connected;
    private string _activeModel = ModelCascade[0];
    private ulong? _lastSentHash;
    private long _framesSent;
    private long _interventions;
    private double _lastCaptureMs;
    private double _lastTtfaMs;
    private long _lastTurnRequestTick;
    private bool _waitingFirstAudio;
    private DateTimeOffset _lastAutoNudgeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFrameSentUtc = DateTimeOffset.MinValue;

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
        Action<bool>? onPauseChanged = null)
    {
        _apiKey = apiKey.Trim();
        _config = config;
        _profiles = profiles;
        _windowDetector = windowDetector;
        _capture = capture;
        _pcmPlayer = pcmPlayer;
        _mic = mic;
        _overlay = overlay;
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
        bool isEn = (config.VoiceLanguage ?? "es").StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var gameName = profile is null || profile.Manifest.GameId.Equals("universal", StringComparison.OrdinalIgnoreCase)
            ? (isEn ? "the video game on screen" : "el videojuego en pantalla")
            : profile.Manifest.DisplayName;

        var sysText = isEn
            ? $"You are a real, friendly human eSports coach on Discord watching {gameName} LIVE in real time. " +
              "Speak naturally, warmly, and concisely in 1 short clear sentence (8-15 words). " +
              "Observe the continuous video feed carefully (kickoffs, score, timer, boost/HP/ammo, replays, menus). " +
              "NEVER invent things you do not see. Stay quiet during menus, loading screens, or goal replays unless the player talks to you directly. " +
              "When the player speaks via microphone, answer naturally like a real teammate/coach right away."
            : $"Sos un coach humano real de eSports hablando por Discord en español argentino natural mientras mirás {gameName} EN VIVO. " +
              "Hablá fluido, claro, cercano y breve (1 sola oración natural de 8 a 15 palabras que se entienda perfecto). " +
              "Observá la secuencia real del video (saques 3-2-1, goles, repeticiones, reloj, marcador, boost/vida/recursos, compañeros y rivales). " +
              "JAMÁS inventes cosas que no ves en pantalla. En menús, pantallas de carga o repeticiones de gol quedate callado salvo que el jugador te hable. " +
              "Si el jugador te habla por micrófono, respondele al toque como en una charla real de Discord.";

        if (!string.IsNullOrWhiteSpace(profile?.CustomRules))
            sysText += $" Reglas del jugador: {profile.CustomRules.Trim()}";

        var genCfg = new Dictionary<string, object>
        {
            ["responseModalities"] = new[] { "AUDIO" },
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
            await SendFreshFrameAsync(force: true, ct);
            await SendUserTurnPromptAsync("¿Qué ves ahora mismo en esta jugada? Dame un consejo corto y claro.", ct);
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
            try
            {
                await RunSingleWebSocketSessionAsync(model, ct);
                // Clean reconnect (e.g. after 10-min Google Live session limit)
                if (!ct.IsCancellationRequested)
                {
                    _onLog?.Invoke("Reconectando sesión Gemini Live automáticamente...");
                    await Task.Delay(700, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var safe = SecretRedactor.Redact(ex.Message, [_apiKey]);
                _onLog?.Invoke($"Sesión Live ({model}) desconectada: {safe}. Probando siguiente modelo...");
                cascadeIdx++;
                try { await Task.Delay(1200, ct); } catch { break; }
            }
            finally
            {
                _connected = false;
                _modelSpeaking = false;
                _mic.Stop();
                if (_ws is not null)
                {
                    try { _ws.Dispose(); } catch { }
                    _ws = null;
                }
            }
        }
    }

    private async Task RunSingleWebSocketSessionAsync(string modelName, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("x-goog-api-key", _apiKey);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _ws = ws;

        _onLog?.Invoke($"Conectando Gemini Live Native-Audio ({modelName}, voz={ResolveLiveVoiceName(_config.VoiceGender)})...");
        await ws.ConnectAsync(new Uri(BidiEndpoint), ct);

        var setupJson = BuildSetupPayloadJson(modelName, _config, _activeProfile);
        await SendRawJsonAsync(ws, setupJson, ct);

        // Wait for setupComplete
        var firstResp = await ReceiveFullMessageAsync(ws, ct);
        if (firstResp is null || !firstResp.Contains("setupComplete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Handshake Live falló: {firstResp ?? "cierre inmediato"}");

        _connected = true;
        _onLog?.Invoke($"🎙 LIVE CONECTADO [{modelName}] — Voz humana {ResolveLiveVoiceName(_config.VoiceGender)} · Hablale por mic o usá {_config.HotkeyAnalyzeNow}!");
        if (_config.EnableOverlay)
        {
            _overlay.ShowCallout(
                _micEnabled ? "Coach Live conectado — podés hablarle directo" : "Coach Live conectado — observando en vivo",
                UrgencyLevel.Low,
                _activeProfile?.Manifest.DisplayName ?? "HUDMind Live",
                3.5);
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
                // Simple RMS noise gate (threshold ~320) so static fan noise doesn't trigger false speech
                if (ComputeRms16(pcmChunk) < 280.0 && !_modelSpeaking)
                    return;
                var ct = _runCts?.Token ?? CancellationToken.None;
                _ = SendMicChunkAsync(pcmChunk, ct);
            },
            err => _onLog?.Invoke(err));
        if (ok)
            _onLog?.Invoke("🎤 Micrófono 16kHz en vivo activo — hablá cuando quieras.");
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

                    bool sent = await SendFreshFrameAsync(force: false, ct, ++frameCounter);

                    // Gentle proactive coach pulse every ~15s when live gameplay changed & coach is quiet
                    if (sent && !_modelSpeaking && (DateTimeOffset.UtcNow - _lastAutoNudgeUtc).TotalSeconds >= 15.0)
                    {
                        _lastAutoNudgeUtc = DateTimeOffset.UtcNow;
                        await SendUserTurnPromptAsync(
                            "Si hay un momento táctico útil en esta jugada (NO en menú, carga ni replay), decilo en 1 frase corta y natural; si no, quedate callado.",
                            ct);
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

    private async Task<bool> SendFreshFrameAsync(bool force, CancellationToken ct, long frameId = 0)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return false;

        var region = _testMode ? null : _windowDetector.GetForegroundClientRect();
        var (frame, skipped) = _capture.CaptureIfChanged(
            _config.CaptureMonitor,
            region,
            targetWidth: 768,
            targetHeight: 432,
            jpegQuality: 72,
            frameId: frameId,
            lastAcceptedHash: _lastSentHash,
            phashThreshold: 6,
            isForced: force);

        if (skipped || frame is null)
            return false;

        _lastSentHash = frame.PerceptualHash;
        _lastCaptureMs = frame.CaptureMs + frame.EncodeMs;
        Interlocked.Increment(ref _framesSent);
        _lastFrameSentUtc = DateTimeOffset.UtcNow;

        var buf = new ArrayBufferWriter<byte>(frame.JpegBytes.Length * 4 / 3 + 128);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WritePropertyName("realtimeInput");
            w.WriteStartObject();
            w.WritePropertyName("video");
            w.WriteStartObject();
            w.WriteString("mimeType", "image/jpeg");
            w.WriteBase64String("data", frame.JpegBytes);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        await SendRawBytesAsync(ws, buf.WrittenMemory, ct);
        return true;
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

    private async Task SendUserTurnPromptAsync(string text, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        _lastTurnRequestTick = Stopwatch.GetTimestamp();
        _waitingFirstAudio = true;

        string json;
        if (_activeModel.Contains("3.", StringComparison.OrdinalIgnoreCase))
        {
            json = JsonSerializer.Serialize(new { realtimeInput = new { text } });
        }
        else
        {
            json = JsonSerializer.Serialize(new
            {
                clientContent = new
                {
                    turns = new[] { new { role = "user", parts = new[] { new { text } } } },
                    turnComplete = true
                }
            });
        }
        await SendRawJsonAsync(ws, json, ct);
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

            // Barge-in: user interrupted the coach -> stop playback immediately!
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
                    if (_config.EnableOverlay && currentLine.Length >= 4)
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
                _modelSpeaking = false;
                var spoken = transcriptBuf.ToString().Trim();
                transcriptBuf.Clear();
                if (!string.IsNullOrWhiteSpace(spoken))
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
