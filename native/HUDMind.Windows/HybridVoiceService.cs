using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// 100% Native C# Hybrid Voice Engine (zero Python subprocesses, zero NuGet, 0% GPU):
/// 1) "neural" / default style: Windows 10/11 WinRT SpeechSynthesizer (Natural/OneCore voices, custom rate,
///    routed via Win32StreamingPcmPlayer directly to the user-selected output speaker device).
/// 2) "instant" / automatic fallback: Persistent Windows OneCore/SAPI5 COM STA worker (&lt;8ms, 0% network).
/// </summary>
public sealed class HybridVoiceService : IVoiceOutputService
{
    private readonly WinRtNaturalVoiceService _winRt = new();
    private readonly InstantSapiVoiceService _instant = new();
    private volatile bool _disposed;

    private string _language = "es";
    private string _gender = "male";
    private string _style = "neural";
    private double _volume = 0.75;

    public HybridVoiceService(string? _ = null)
    {
        // Parameter retained cleanly for caller compatibility; runtime is 100% self-contained in Win32/WinRT.
    }

    public bool IsNeuralWorkerPresent => _winRt.IsAvailable;

    public string EngineName =>
        _style == "neural" && _winRt.IsAvailable
            ? _winRt.EngineName
            : _instant.EngineName;

    public void WarmUp(string language, double volume, string gender = "male", string style = "neural")
    {
        if (_disposed) return;
        _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
        _gender = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
        _style = string.Equals(style, "instant", StringComparison.OrdinalIgnoreCase) ? "instant" : "neural";
        _volume = Math.Clamp(volume, 0.0, 1.0);

        if (_style == "neural")
            _winRt.WarmUp(_language, _volume, _gender, _style);
        _instant.WarmUp(_language, _volume, _gender, "instant");
    }

    public void UpdateSettings(string language, double volume, string gender = "male", string style = "neural")
    {
        if (_disposed) return;
        _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
        _gender = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
        var newStyle = string.Equals(style, "instant", StringComparison.OrdinalIgnoreCase) ? "instant" : "neural";
        bool switchedToInstant = _style == "neural" && newStyle == "instant";
        _style = newStyle;
        _volume = Math.Clamp(volume, 0.0, 1.0);

        if (switchedToInstant)
            _winRt.StopSpeaking();

        _winRt.UpdateSettings(_language, _volume, _gender, _style);
        _instant.UpdateSettings(_language, _volume, _gender, "instant");
    }

    public void SetOutputDevice(string? deviceName) => _winRt.SetOutputDevice(deviceName);

    public void SetSpeechRate(string? rate) => _winRt.SetSpeechRate(rate);

    public double SpeakInstant(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return 0;

        if (_style == "neural" && _winRt.IsAvailable)
        {
            _instant.StopSpeaking();
            return _winRt.SpeakInstant(text);
        }

        _winRt.StopSpeaking();
        return _instant.SpeakInstant(text);
    }

    public void StopSpeaking()
    {
        _winRt.StopSpeaking();
        _instant.StopSpeaking();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _winRt.Dispose();
        _instant.Dispose();
    }
}
