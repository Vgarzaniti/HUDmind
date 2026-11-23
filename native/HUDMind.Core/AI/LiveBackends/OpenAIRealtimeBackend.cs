using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI.LiveBackends;

/// <summary>
/// Concrete <see cref="ILiveRealtimeBackend"/> adapter for OpenAI Realtime API and xAI Grok Realtime API
/// (<c>wss://api.openai.com/v1/realtime</c> / <c>wss://api.x.ai/v1/realtime</c>).
/// Demonstrates true multi-vendor provider agnosticism alongside <see cref="GeminiLiveBackend"/>.
/// </summary>
public sealed class OpenAIRealtimeBackend : ILiveRealtimeBackend
{
    private const int MaxWsMessageBytes = 4 * 1024 * 1024;

    private readonly string _provider;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _frameLock = new();
    private volatile ClientWebSocket? _ws;
    private byte[]? _latestJpeg;
    private volatile bool _connected;
    private volatile bool _disposed;

    public string ProviderName => _provider.Equals("xai", StringComparison.OrdinalIgnoreCase) ? "xAI Grok Live" : "OpenAI Realtime";
    public string ActiveModelLabel => _model;
    public bool IsConnected => _connected && _ws is { State: WebSocketState.Open };
    public bool SupportsMultiFrameTurn => true;

    public OpenAIRealtimeBackend(string provider, string apiKey, string? model = null)
    {
        _provider = (provider ?? "openai").Trim().ToLowerInvariant();
        _apiKey = (apiKey ?? string.Empty).Trim();
        _model = ResolveDefaultModel(_provider, model);
    }

    private static string ResolveDefaultModel(string provider, string? model)
    {
        var m = (model ?? string.Empty).Trim();
        if (provider == "xai")
            return string.IsNullOrEmpty(m) ? "grok-2-realtime" : m;

        if (!string.IsNullOrEmpty(m) && m.Contains("realtime", StringComparison.OrdinalIgnoreCase))
            return m;
        return "gpt-realtime";
    }

    public string ResolveVoiceName(string? gender) => ResolveVoiceNameStatic(_provider, gender);

    public static string ResolveVoiceNameStatic(string provider, string? gender)
    {
        bool female = string.Equals(gender?.Trim(), "female", StringComparison.OrdinalIgnoreCase);
        if (provider == "xai")
            return female ? "Ara" : "Rex";
        return female ? "coral" : "ash";
    }

    public static string BuildSessionUpdateJson(string provider, UserConfig config, GameProfile? profile)
    {
        var voiceName = ResolveVoiceNameStatic(provider, config.VoiceGender);
        var instructions = CoachingPersonaBuilder.BuildLiveSystemInstruction(config, profile);
        string[] modalities = config.IsTextOnlyMode ? ["text"] : ["audio", "text"];

        var sessionObj = new Dictionary<string, object>
        {
            ["instructions"] = instructions,
            ["modalities"] = modalities,
            ["voice"] = voiceName,
            ["input_audio_format"] = "pcm16",
            ["output_audio_format"] = "pcm16",
            ["turn_detection"] = new
            {
                type = "server_vad",
                threshold = 0.62,
                prefix_padding_ms = 200,
                silence_duration_ms = 260
            }
        };

        return JsonSerializer.Serialize(new
        {
            type = "session.update",
            session = sessionObj
        });
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
        var endpoint = _provider == "xai"
            ? "wss://api.x.ai/v1/realtime"
            : $"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(_model)}";

        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _ws = ws;

        try
        {
            onLog($"Conectando backend {ProviderName} ({ActiveModelLabel}, voz={ResolveVoiceName(config.VoiceGender)})...");
            await ws.ConnectAsync(new Uri(endpoint), sessionCt);

            var updateJson = BuildSessionUpdateJson(_provider, config, profile);
            await SendRawBytesAsync(ws, Encoding.UTF8.GetBytes(updateJson), sessionCt);

            _connected = true;
            onConnected();

            var transcriptBuf = new StringBuilder();
            while (!sessionCt.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var raw = await ReceiveFullMessageAsync(ws, sessionCt);
                if (raw is null)
                    break;

                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                    continue;
                var msgType = typeEl.GetString() ?? string.Empty;

                if (msgType == "error")
                {
                    var errText = SecretRedactor.Redact(raw.Length > 200 ? raw[..200] : raw, [_apiKey]);
                    throw new InvalidOperationException($"{ProviderName} error: {errText}");
                }
                if (msgType == "input_audio_buffer.speech_started")
                {
                    transcriptBuf.Clear();
                    onInterrupted();
                    continue;
                }
                if (msgType is "response.audio.delta" or "response.output_audio.delta")
                {
                    if (config.IsCoachVoiceActive && doc.RootElement.TryGetProperty("delta", out var d))
                    {
                        var b64 = d.GetString();
                        if (!string.IsNullOrEmpty(b64))
                            onPcm24kAudio(Convert.FromBase64String(b64));
                    }
                    continue;
                }
                if (msgType is "response.audio_transcript.delta" or "response.text.delta" or "response.output_text.delta")
                {
                    if (doc.RootElement.TryGetProperty("delta", out var td))
                    {
                        var chunk = td.GetString();
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            transcriptBuf.Append(chunk);
                            onPartialTranscript(transcriptBuf.ToString().Trim());
                        }
                    }
                    continue;
                }
                if (msgType is "response.done")
                {
                    var spoken = transcriptBuf.ToString().Trim();
                    transcriptBuf.Clear();
                    onTurnComplete(spoken);
                }
            }
        }
        finally
        {
            _connected = false;
            try { ws.Dispose(); } catch { }
            if (ReferenceEquals(_ws, ws))
                _ws = null;
        }
    }

    public Task SendVideoFrameAsync(byte[] jpegBytes, CancellationToken ct)
    {
        if (jpegBytes is { Length: > 0 })
        {
            lock (_frameLock) _latestJpeg = jpegBytes;
        }
        return Task.CompletedTask;
    }

    public async Task SendMicAudio16kAsync(byte[] pcm16k, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open || pcm16k.Length < 2) return;

        // OpenAI Realtime expects 24kHz PCM16: linear resample 16k -> 24k (1.5x) lock-free inline
        byte[] pcm24k = Pipeline.AudioPcmUtilities.Resample16kTo24k(pcm16k);
        var buf = new ArrayBufferWriter<byte>(pcm24k.Length * 4 / 3 + 96);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("type", "input_audio_buffer.append");
            w.WriteBase64String("audio", pcm24k);
            w.WriteEndObject();
        }
        await SendRawBytesAsync(ws, buf.WrittenMemory, ct);
    }

    public Task SendImageAndPromptTurnAsync(byte[] jpegBytes, string promptText, CancellationToken ct) =>
        SendFrameSequenceAndPromptTurnAsync(
            jpegBytes is { Length: > 0 } ? [new TemporalFrameSample(jpegBytes, DateTimeOffset.UtcNow, 0)] : [],
            promptText,
            ct);

    public async Task SendFrameSequenceAndPromptTurnAsync(
        IReadOnlyList<TemporalFrameSample> frames,
        string promptText,
        CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        var validFrames = frames.Where(f => f.JpegBytes is { Length: > 0 }).TakeLast(5).ToArray();
        int totalBytes = validFrames.Sum(f => f.JpegBytes.Length);
        var itemBuf = new ArrayBufferWriter<byte>(totalBytes * 4 / 3 + 512);
        using (var w = new Utf8JsonWriter(itemBuf))
        {
            w.WriteStartObject();
            w.WriteString("type", "conversation.item.create");
            w.WritePropertyName("item");
            w.WriteStartObject();
            w.WriteString("type", "message");
            w.WriteString("role", "user");
            w.WritePropertyName("content");
            w.WriteStartArray();
            foreach (var sample in validFrames)
            {
                w.WriteStartObject();
                w.WriteString("type", "input_image");
                w.WriteString("image_url", $"data:image/jpeg;base64,{Convert.ToBase64String(sample.JpegBytes)}");
                w.WriteEndObject();
            }
            w.WriteStartObject();
            w.WriteString("type", "input_text");
            w.WriteString("text", promptText);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        await SendRawBytesAsync(ws, itemBuf.WrittenMemory, ct);
        await SendRawBytesAsync(ws, Encoding.UTF8.GetBytes("{\"type\":\"response.create\"}"), ct);
    }

    public async Task SendTextTurnAsync(string promptText, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        byte[]? cachedFrame;
        lock (_frameLock) cachedFrame = _latestJpeg;
        if (cachedFrame is { Length: > 0 })
        {
            await SendImageAndPromptTurnAsync(cachedFrame, promptText, ct);
            return;
        }

        var itemBuf = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(itemBuf))
        {
            w.WriteStartObject();
            w.WriteString("type", "conversation.item.create");
            w.WritePropertyName("item");
            w.WriteStartObject();
            w.WriteString("type", "message");
            w.WriteString("role", "user");
            w.WritePropertyName("content");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("type", "input_text");
            w.WriteString("text", promptText);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        await SendRawBytesAsync(ws, itemBuf.WrittenMemory, ct);
        await SendRawBytesAsync(ws, Encoding.UTF8.GetBytes("{\"type\":\"response.create\"}"), ct);
    }

    private async Task SendRawBytesAsync(ClientWebSocket ws, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_disposed) return;
        try { await _sendLock.WaitAsync(ct); }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (!_disposed && ws.State == WebSocketState.Open)
                await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (ObjectDisposedException) { }
        catch (WebSocketException) { }
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
                        throw new InvalidOperationException("Mensaje WebSocket excedió límite de seguridad.");
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
