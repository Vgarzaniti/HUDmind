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
    void SetVolume(double volume);
    void EnqueuePcm24k(byte[] pcmBytes);
    void InterruptAndClear();
}

/// <summary>
/// Streaming 16kHz 16-bit mono PCM microphone capture for live two-way voice conversation.
/// </summary>
public interface IStreamingMicCapture : IDisposable
{
    bool IsCapturing { get; }
    bool TryStart(Action<byte[]> onPcm16kChunk, Action<string>? onError = null);
    void Stop();
}
