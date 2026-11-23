using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI.LiveBackends;

/// <summary>
/// Concrete <see cref="ILiveRealtimeBackend"/> adapter for Google's BidiGenerateContent Native-Audio WebSocket.
/// Isolated under `AI/LiveBackends/` so additional real-time backends (OpenAI Realtime, xAI, etc.) plug in cleanly.
/// </summary>
public sealed class GeminiLiveBackend : ILiveRealtimeBackend
{
    private const string BidiEndpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    private const int MaxWsMessageBytes = 4 * 1024 * 1024; // CWE-400 bounded receive cap

    private static readonly string[] DefaultModelCascade =
    [
        "models/gemini-3.1-flash-live-preview",
        "models/gemini-2.5-flash-native-audio-latest",
        "models/gemini-3.8-live"
    ];

    private readonly string _apiKey;
    private readonly string[] _modelCascade;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile ClientWebSocket? _ws;
    private int _cascadeIdx;
    private string _activeModel;
    private string? _resumptionHandle;
    private volatile bool _connected;
    private volatile bool _disposed;

    public string ProviderName => "Gemini Live";
    public string ActiveModelLabel => _activeModel.Replace("models/", "", StringComparison.OrdinalIgnoreCase);
    public bool IsConnected => _connected && _ws is { State: WebSocketState.Open };

    public GeminiLiveBackend(string apiKey, string? preferredModel = null)
    {
        _apiKey = apiKey.Trim();
        var cleanPref = (preferredModel ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(cleanPref) && cleanPref.Contains("live", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = cleanPref.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? cleanPref
                : $"models/{cleanPref}";
            _modelCascade = [normalized, .. DefaultModelCascade.Where(m => !m.Equals(normalized, StringComparison.OrdinalIgnoreCase))];
        }
        else
        {
            _modelCascade = DefaultModelCascade;
        }
        _activeModel = _modelCascade[0];
    }

    public string ResolveVoiceName(string? gender) => ResolveVoiceNameStatic(gender);

    public static string ResolveVoiceNameStatic(string? gender) =>
        string.Equals(gender?.Trim(), "female", StringComparison.OrdinalIgnoreCase) ? "Aoede" : "Fenrir";

    public static string BuildSetupPayloadJson(string modelName, UserConfig config, GameProfile? profile, string? resumptionHandle = null)
    {
        var voiceName = ResolveVoiceNameStatic(config.VoiceGender);
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

        var setupInner = new Dictionary<string, object>
        {
            ["model"] = modelName,
            ["generationConfig"] = genCfg,
            ["realtimeInputConfig"] = new
            {
                automaticActivityDetection = new
                {
                    startOfSpeechSensitivity = "START_SENSITIVITY_LOW",
                    endOfSpeechSensitivity = "END_SENSITIVITY_HIGH",
                    silenceDurationMs = 250
                }
            },
            ["outputAudioTranscription"] = new { },
            ["systemInstruction"] = new
            {
                parts = new[] { new { text = sysText } }
            }
        };

        if (!string.IsNullOrWhiteSpace(resumptionHandle))
            setupInner["sessionResumption"] = new { handle = resumptionHandle };
        else
            setupInner["sessionResumption"] = new { };

        return JsonSerializer.Serialize(new { setup = setupInner });
    }

    public async Task RunSessionLoopAsync(
        UserConfig config,
        GameProfile? profile,
        Action<byte[]> onPcm24kAudio,
        Action<string> onPartialTranscript,
        Action<string> onTurnComplete,
        Action onInterrupted,
        Action<string> onLog,
        Action onConnected,
        CancellationToken sessionCt)
    {
        var (okUrl, errUrl) = BaseUrlSecurityValidator.Validate(BidiEndpoint, allowLoopbackHttp: false, allowWebSocket: true);
        if (!okUrl)
            throw new InvalidOperationException(errUrl);

        string model = _modelCascade[_cascadeIdx % _modelCascade.Length];
        _activeModel = model;

        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("x-goog-api-key", _apiKey);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _ws = ws;
        bool handshakeDone = false;

        try
        {
            onLog($"Conectando backend Live ({ActiveModelLabel}, voz={ResolveVoiceName(config.VoiceGender)})...");
            await ws.ConnectAsync(new Uri(BidiEndpoint), sessionCt);

            var setupJson = BuildSetupPayloadJson(model, config, profile, _resumptionHandle);
            await SendRawBytesAsync(ws, Encoding.UTF8.GetBytes(setupJson), sessionCt);

            var firstResp = await ReceiveFullMessageAsync(ws, sessionCt);
            if (!IsSetupCompleteJson(firstResp))
            {
                _resumptionHandle = null;
                var snippet = SecretRedactor.Redact(firstResp is { Length: > 200 } ? firstResp[..200] : (firstResp ?? "cierre inmediato"), [_apiKey]);
                throw new InvalidOperationException($"Handshake Live falló: {snippet}");
            }

            handshakeDone = true;
            _connected = true;
            onConnected();

            var transcriptBuf = new StringBuilder();
            while (!sessionCt.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var raw = await ReceiveFullMessageAsync(ws, sessionCt);
                if (raw is null)
                    break;

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.TryGetProperty("sessionResumptionUpdate", out var resUpd) &&
                    resUpd.TryGetProperty("newHandle", out var newH))
                {
                    var h = newH.GetString();
                    if (!string.IsNullOrWhiteSpace(h))
                        _resumptionHandle = h;
                }

                if (root.TryGetProperty("goAway", out _))
                {
                    onLog("Sesión Live renovando conexión limpia antes de expiración...");
                    break;
                }

                if (root.TryGetProperty("error", out var errEl))
                {
                    var errText = SecretRedactor.Redact(errEl.ToString(), [_apiKey]);
                    if (errText.Length > 180) errText = errText[..180];
                    throw new InvalidOperationException($"Error servidor Live: {errText}");
                }

                if (!root.TryGetProperty("serverContent", out var sc))
                    continue;

                if (sc.TryGetProperty("interrupted", out var intr) && intr.ValueKind == JsonValueKind.True)
                {
                    transcriptBuf.Clear();
                    onInterrupted();
                    continue;
                }

                if (sc.TryGetProperty("outputTranscription", out var ot) &&
                    ot.TryGetProperty("text", out var otText))
                {
                    var chunk = otText.GetString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        transcriptBuf.Append(chunk);
                        onPartialTranscript(transcriptBuf.ToString().Trim());
                    }
                }

                if (sc.TryGetProperty("modelTurn", out var mt) &&
                    mt.TryGetProperty("parts", out var parts))
                {
                    foreach (var p in parts.EnumerateArray())
                    {
                        if (p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True)
                            continue;

                        if (config.IsCoachVoiceActive &&
                            p.TryGetProperty("inlineData", out var inl) &&
                            inl.TryGetProperty("data", out var b64El))
                        {
                            var b64 = b64El.GetString();
                            if (!string.IsNullOrEmpty(b64))
                                onPcm24kAudio(Convert.FromBase64String(b64));
                        }
                        else if (p.TryGetProperty("text", out var txtEl))
                        {
                            var txt = txtEl.GetString();
                            if (!string.IsNullOrEmpty(txt) && transcriptBuf.Length == 0)
                            {
                                transcriptBuf.Append(txt);
                                onPartialTranscript(transcriptBuf.ToString().Trim());
                            }
                        }
                    }
                }

                if (sc.TryGetProperty("turnComplete", out var tc) && tc.ValueKind == JsonValueKind.True)
                {
                    var spoken = transcriptBuf.ToString().Trim();
                    transcriptBuf.Clear();
                    onTurnComplete(spoken);
                }
            }
        }
        catch (OperationCanceledException) when (sessionCt.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Rotate fallback model ONLY when initial handshake failed (keep primary healthy on brief network drops!)
            if (!handshakeDone)
                _cascadeIdx++;
            throw;
        }
        finally
        {
            _connected = false;
            try { ws.Dispose(); } catch { }
            if (ReferenceEquals(_ws, ws))
                _ws = null;
        }
    }

    private static bool IsSetupCompleteJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("setupComplete", out _);
        }
        catch
        {
            return false;
        }
    }

    public async Task SendVideoFrameAsync(byte[] jpegBytes, CancellationToken ct)
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

    public async Task SendMicAudio16kAsync(byte[] pcm16k, CancellationToken ct)
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

    public async Task SendImageAndPromptTurnAsync(byte[] jpegBytes, string promptText, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

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
            w.WriteString("text", promptText);
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

    public async Task SendTextTurnAsync(string promptText, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        var buf = new ArrayBufferWriter<byte>(512);
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
            w.WriteString("text", promptText);
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

    private async Task SendRawBytesAsync(ClientWebSocket ws, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_disposed) return;
        try
        {
            await _sendLock.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (!_disposed && ws.State == WebSocketState.Open)
                await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (ObjectDisposedException) { /* closing cleanly */ }
        catch (WebSocketException) { /* reconnect loop handles cleanly */ }
        finally
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException) { }
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
                {
                    if (ms.Length + res.Count > MaxWsMessageBytes)
                        throw new InvalidOperationException($"Mensaje WebSocket excedió el límite de {MaxWsMessageBytes / (1024 * 1024)} MB (CWE-400).");
                    ms.Write(buffer, 0, res.Count);
                }
                if (res.EndOfMessage)
                    return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public ValueTask ResetSessionStateAsync()
    {
        _resumptionHandle = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _connected = false;
        var ws = _ws;
        _ws = null;
        try { ws?.Dispose(); } catch { }
        try { _sendLock.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
