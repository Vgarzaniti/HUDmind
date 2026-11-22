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

    private static readonly string[] ModelCascade =
    [
        "models/gemini-3.1-flash-live-preview",
        "models/gemini-2.5-flash-native-audio-latest",
        "models/gemini-3.8-live"
    ];

    private readonly string _apiKey;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private int _cascadeIdx;
    private string _activeModel = ModelCascade[0];
    private volatile bool _connected;

    public string ProviderName => "Gemini Live";
    public string ActiveModelLabel => _activeModel.Replace("models/", "", StringComparison.OrdinalIgnoreCase);
    public bool IsConnected => _connected && _ws is { State: WebSocketState.Open };

    public GeminiLiveBackend(string apiKey)
    {
        _apiKey = apiKey.Trim();
    }

    public string ResolveVoiceName(string? gender) => ResolveVoiceNameStatic(gender);

    public static string ResolveVoiceNameStatic(string? gender) =>
        string.Equals(gender?.Trim(), "female", StringComparison.OrdinalIgnoreCase) ? "Aoede" : "Fenrir";

    public static string BuildSetupPayloadJson(string modelName, UserConfig config, GameProfile? profile)
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

        var setupObj = new
        {
            setup = new
            {
                model = modelName,
                generationConfig = genCfg,
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
        string model = ModelCascade[_cascadeIdx % ModelCascade.Length];
        _activeModel = model;

        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("x-goog-api-key", _apiKey);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _ws = ws;

        try
        {
            onLog($"Conectando backend Live ({ActiveModelLabel}, voz={ResolveVoiceName(config.VoiceGender)})...");
            await ws.ConnectAsync(new Uri(BidiEndpoint), sessionCt);

            var setupJson = BuildSetupPayloadJson(model, config, profile);
            await SendRawBytesAsync(ws, Encoding.UTF8.GetBytes(setupJson), sessionCt);

            var firstResp = await ReceiveFullMessageAsync(ws, sessionCt);
            if (firstResp is null || !firstResp.Contains("setupComplete", StringComparison.OrdinalIgnoreCase))
            {
                _cascadeIdx++;
                throw new InvalidOperationException($"Handshake Live falló: {firstResp ?? "cierre inmediato"}");
            }

            _connected = true;
            onConnected();

            var transcriptBuf = new StringBuilder();
            while (!sessionCt.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var raw = await ReceiveFullMessageAsync(ws, sessionCt);
                if (raw is null)
                    break;

                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("serverContent", out var sc))
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
                        if (p.TryGetProperty("inlineData", out var inl) &&
                            inl.TryGetProperty("data", out var b64El))
                        {
                            var b64 = b64El.GetString();
                            if (!string.IsNullOrEmpty(b64))
                                onPcm24kAudio(Convert.FromBase64String(b64));
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
        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
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

    public ValueTask DisposeAsync()
    {
        _connected = false;
        try { _ws?.Dispose(); } catch { }
        _ws = null;
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
