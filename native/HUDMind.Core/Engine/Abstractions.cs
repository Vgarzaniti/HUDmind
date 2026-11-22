using HUDMind.Core.Models;

namespace HUDMind.Core.Engine;

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
}

public interface IVoiceOutputService : IDisposable
{
    string EngineName { get; }
    void WarmUp(string language, double volume, string gender = "male", string style = "neural");
    void UpdateSettings(string language, double volume, string gender = "male", string style = "neural");
    double SpeakInstant(string text);
    void StopSpeaking();
}

public interface IOverlayOutputService
{
    void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds);
}

/// <summary>
/// Lazy sensor layer (WinRT OCR + read-only Game Logs/APIs) invoked ONLY for text-only ("blind") AI models.
/// Vision models never invoke this interface (0ms overhead on Vision hot path).
/// </summary>
public interface ITextOnlySensorService
{
    Task<HudSensorSnapshot> ExtractAsync(byte[] jpegBytes, GameProfile profile, CancellationToken ct = default);
    void Reset();
}

/// <summary>
/// Streaming 24kHz 16-bit mono PCM audio player for Gemini Live Native-Audio (&lt;10ms latency, 0% GPU).
/// </summary>
public interface IStreamingPcmPlayer : IDisposable
{
    bool IsPlayingAudio { get; }
    void SetDevice(string? deviceName) { }
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
}
