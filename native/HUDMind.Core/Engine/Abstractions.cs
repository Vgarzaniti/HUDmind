using HUDMind.Core.Models;

namespace HUDMind.Core.Engine;

public sealed record TemporalFrameSample(
    byte[] JpegBytes,
    DateTimeOffset CapturedAtUtc,
    ulong PerceptualHash
);

/// <summary>
/// Dual-representation temporal clip payload: native chronological HD frames for multi-image APIs
/// (Gemini, OpenAI, Anthropic, Live) plus a CPU-composed filmstrip fallback for single-image Vision endpoints.
/// </summary>
public sealed record TemporalVisualPayload(
    IReadOnlyList<TemporalFrameSample> Frames,
    byte[]? StoryboardJpeg,
    int PanelCount,
    string SequenceNote
)
{
    public byte[]? FallbackSingleJpeg =>
        StoryboardJpeg ?? (Frames.Count > 0 ? Frames[^1].JpegBytes : null);
}

/// <summary>
/// Pure CPU (0% GPU) temporal sequence composer combining recent frames into a single
/// timestamp-labeled motion strip (e.g. 2x1 live delta or 2x2 storyboard) for any Vision provider.
/// </summary>
public interface ITemporalStoryboardComposer
{
    byte[]? ComposeSequenceJpeg(
        IReadOnlyList<TemporalFrameSample> frames,
        int targetWidth,
        int targetHeight,
        int jpegQuality);
}

public interface IForegroundWindowDetector
{
    string? GetForegroundExecutable();
    (int Left, int Top, int Width, int Height)? GetForegroundClientRect();
}

public interface IScreenCaptureService : IDisposable
{
    /// <summary>
    /// Captures, computes 64-bit dHash, and only encodes JPEG when forced or Hamming distance &gt;= threshold.
    /// Returns (Frame, SkippedByHash).
    /// </summary>
    (CapturedFrameData? Frame, bool SkippedByHash) CaptureIfChanged(
        int monitorIndex,
        (int Left, int Top, int Width, int Height)? regionRect,
        int targetWidth,
        int targetHeight,
        int jpegQuality,
        long frameId,
        ulong? lastAcceptedHash,
        int phashThreshold,
        bool isForced);

    int ConsecutiveBlackFrames => 0;
}

public sealed record AudioSceneSnapshot(
    double OtherSessionsPeak01,
    bool HighBurstActive,
    DateTimeOffset SampledAtUtc
)
{
    public static AudioSceneSnapshot Quiet { get; } = new(0.0, false, DateTimeOffset.MinValue);
}

/// <summary>
/// Passive read-only Windows/OS mixer telemetry observing non-HUDMind session peak levels
/// without mutating or hooking game processes (100% Anti-Cheat Safe).
/// </summary>
public interface IAudioSceneMonitor : IDisposable
{
    AudioSceneSnapshot SampleOtherAudio();
}

public sealed class NullAudioSceneMonitor : IAudioSceneMonitor
{
    public static NullAudioSceneMonitor Instance { get; } = new();
    private NullAudioSceneMonitor() { }
    public AudioSceneSnapshot SampleOtherAudio() => AudioSceneSnapshot.Quiet;
    public void Dispose() { }
}

public interface IVoiceOutputService : IDisposable
{
    string EngineName { get; }
    bool IsSpeaking => false;
    bool IsActivelySpeaking => IsSpeaking;
    bool SupportsContinuationQueue => false;
    void WarmUp(string language, double volume, string gender = "male", string style = "neural");
    void UpdateSettings(string language, double volume, string gender = "male", string style = "neural");
    void SetSpeechRate(string? rate) { }
    void SetOutputDevice(string? deviceName) { }
    double SpeakInstant(string text);
    double SpeakOrQueue(string text, bool chainIfSpeaking, bool interruptCurrent) => SpeakInstant(text);
    void StopSpeaking();
}

public enum OverlayEntryKind
{
    Status = 0,
    CoachLive = 1,
    CoachFinal = 2,
    UserMessage = 3
}

public sealed record OverlayCalloutMessage(
    string Text,
    UrgencyLevel Urgency,
    string Title,
    double DurationSeconds,
    OverlayEntryKind Kind = OverlayEntryKind.CoachFinal,
    string? Speaker = null
);

public interface IOverlayOutputService
{
    void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds);

    void ShowMessage(OverlayCalloutMessage message) =>
        ShowCallout(message.Text, message.Urgency, message.Title, message.DurationSeconds);

    void UpdateVoiceActivity(bool userMicActive, double userLevel01, bool coachVoiceActive, double coachLevel01) { }

    void ClearHistory() { }
}

/// <summary>
/// Sensor layer combining local passive game telemetry/logs (~0-5ms) and optional WinRT OCR for text-only models.
/// </summary>
public interface ITextOnlySensorService
{
    Task<HudSensorSnapshot> ExtractAsync(byte[] jpegBytes, GameProfile profile, CancellationToken ct = default);
    Task<string> ReadTelemetryAsync(GameProfile profile, CancellationToken ct = default) => Task.FromResult(string.Empty);
    void Reset();
}

/// <summary>
/// Streaming 24kHz 16-bit mono PCM audio player for Gemini Live Native-Audio (&lt;10ms latency, 0% GPU).
/// </summary>
public interface IStreamingPcmPlayer : IDisposable
{
    bool IsPlayingAudio { get; }
    bool IsActivelyPlaying => IsPlayingAudio;
    void SetDevice(string? deviceName) { }
    void PreOpenDevice() { }
    void SetVolume(double volume);
    void EnqueuePcm24k(byte[] pcmBytes);
    void FlushBuffered();
    void InterruptAndClear();
}

/// <summary>
/// Streaming 16kHz 16-bit mono PCM microphone capture for live two-way voice conversation.
/// </summary>
public interface IStreamingMicCapture : IDisposable
{
    bool IsCapturing { get; }
    void SetDevice(string? deviceName) { }
    bool TryStart(Action<byte[]> onPcm16kChunk, Action<string>? onError = null);
    void Stop();
}

/// <summary>
/// Pluggable, Anti-Cheat safe passive game telemetry sensor (read-only logs, local official APIs, Valve GSI).
/// New games can register custom implementations without modifying existing sensor infrastructure.
/// </summary>
public interface IGameTelemetrySensor
{
    string SensorId { get; }
    bool Supports(GameProfile profile);
    Task<string> ReadAsync(GameProfile profile, CancellationToken ct = default);
}

/// <summary>
/// Unified polymorphic contract for any real-time coaching engine (Live WebSocket, Classic Multi-IA, Replay Analyzer, etc.).
/// Allows UI/coordinators to control engines cleanly without concrete type branching.
/// </summary>
public interface ICoachEngine : IAsyncDisposable
{
    bool IsRunning { get; }
    bool IsPaused { get; }
    bool SupportsLiveMic { get; }
    string EngineBadge { get; }
    string ActiveChannelStatus { get; }
    string ActiveVoiceDisplay { get; }
    string ActiveGameId => string.Empty;

    Task StartAsync();
    Task StopAsync();
    void TogglePause();
    void TriggerOnDemandAnalysis();
    void SetCommunicationMode(string mode);
    Task<bool> SendUserTextAsync(string userText, CancellationToken ct = default);
    void RefreshPersona(string? reason = null);
    void UpdateAudioLive(UserConfig config);
    void ApplyLiveSettings(UserConfig config);
    (string GameName, string Digest, int Count) GetMatchSessionDigest(bool isEn = false);
    MatchSessionSnapshot? CaptureMatchSnapshot(bool isEn = false) => null;
}
