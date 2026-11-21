using HUDMind.Core.Models;

namespace HUDMind.Core.Engine;

public interface IForegroundWindowDetector
{
    string? GetForegroundExecutable();
    (int Left, int Top, int Width, int Height)? GetForegroundClientRect();
}

public interface IScreenCaptureService : IDisposable
{
    CapturedFrameData? CaptureCompressedFrame(
        int monitorIndex,
        (int Left, int Top, int Width, int Height)? regionRect,
        int targetWidth,
        int targetHeight,
        int jpegQuality,
        long frameId);
}

public interface IVoiceOutputService : IDisposable
{
    string EngineName { get; }
    void WarmUp(string language, double volume);
    double SpeakInstant(string text);
    void StopSpeaking();
}

public interface IOverlayOutputService
{
    void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds);
}
