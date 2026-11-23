using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

/// <summary>
/// Provider-agnostic contract for any bidirectional real-time Audio+Video AI WebSocket backend
/// (Gemini Live, OpenAI Realtime, xAI Grok Realtime, or custom pluggable backends).
/// </summary>
public interface ILiveRealtimeBackend : IAsyncDisposable
{
    string ProviderName { get; }
    string ActiveModelLabel { get; }
    bool IsConnected { get; }
    bool SupportsMultiFrameTurn => false;

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
    Task SendFrameSequenceAndPromptTurnAsync(
        IReadOnlyList<TemporalFrameSample> frames,
        string promptText,
        CancellationToken ct) =>
        frames.Count > 0
            ? SendImageAndPromptTurnAsync(frames[^1].JpegBytes, promptText, ct)
            : SendTextTurnAsync(promptText, ct);
    Task SendTextTurnAsync(string promptText, CancellationToken ct);
    ValueTask ResetSessionStateAsync() => ValueTask.CompletedTask;
}

public static class LiveBackendFactory
{
    /// <summary>
    /// Selects the highest-priority enabled account that supports WebSocket bidirectional Live Audio+Video
    /// via <see cref="ProviderRegistry"/>. Returns null only when no Live-capable account is ready.
    /// </summary>
    public static ILiveRealtimeBackend? TryCreateFromAccounts(IEnumerable<ProviderAccount> accounts)
    {
        foreach (var acc in accounts.Where(a => a.IsReady()).OrderBy(a => a.Priority))
        {
            var backend = ProviderRegistry.TryCreateLiveBackend(acc);
            if (backend is not null)
                return backend;
        }
        return null;
    }
}
