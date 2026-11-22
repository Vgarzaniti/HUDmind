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
