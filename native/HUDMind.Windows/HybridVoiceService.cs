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
    private readonly object _dispatchLock = new();
    private readonly WinRtNaturalVoiceService _winRt = new();
    private readonly InstantSapiVoiceService _instant = new();
    private volatile bool _disposed;

    private string _language = "es";
    private string _gender = "male";
    private volatile string _style = "neural";
    private double _volume = 0.75;
    private long _settingsGen;

    public HybridVoiceService(string? _ = null)
    {
        // Parameter retained cleanly for caller compatibility; runtime is 100% self-contained in Win32/WinRT.
    }

    public bool IsNeuralWorkerPresent => _winRt.IsAvailable;
    public bool SupportsContinuationQueue => true;
    public bool IsSpeaking => (_style == "neural" && _winRt.IsInitializedAndAvailable) ? _winRt.IsSpeaking : _instant.IsSpeaking;
    public bool IsActivelySpeaking => (_style == "neural" && _winRt.IsInitializedAndAvailable) ? _winRt.IsActivelySpeaking : _instant.IsActivelySpeaking;

    public string EngineName
    {
        get
        {
            lock (_dispatchLock)
                return _style == "neural" && _winRt.IsAvailable ? _winRt.EngineName : _instant.EngineName;
        }
    }

    public void WarmUp(string language, double volume, string gender = "male", string style = "neural")
    {
        if (_disposed) return;
        string styleNorm;
        long gen;
        lock (_dispatchLock)
        {
            if (_disposed) return;
            gen = Interlocked.Increment(ref _settingsGen);
            string langNorm = _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
            string genderNorm = _gender = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
            styleNorm = _style = string.Equals(style, "instant", StringComparison.OrdinalIgnoreCase) ? "instant" : "neural";
            double volNorm = _volume = Math.Clamp(volume, 0.0, 1.0);
            _winRt.UpdateSettings(langNorm, volNorm, genderNorm, styleNorm);
            _instant.UpdateSettings(langNorm, volNorm, genderNorm, "instant");
        }

        if (_disposed || Interlocked.Read(ref _settingsGen) != gen) return;
        if (styleNorm == "neural")
            _winRt.EnsureInitializedInternal();
        if (_disposed || Interlocked.Read(ref _settingsGen) != gen) return;
        _instant.EnsureInitializedFromStoredSettings();
    }

    public void UpdateSettings(string language, double volume, string gender = "male", string style = "neural")
    {
        if (_disposed) return;
        lock (_dispatchLock)
        {
            if (_disposed) return;
            Interlocked.Increment(ref _settingsGen);
            string langNorm = _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
            string genderNorm = _gender = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
            string styleNorm = string.Equals(style, "instant", StringComparison.OrdinalIgnoreCase) ? "instant" : "neural";
            bool switchedToInstant = _style == "neural" && styleNorm == "instant";
            _style = styleNorm;
            double volNorm = _volume = Math.Clamp(volume, 0.0, 1.0);
            if (switchedToInstant)
                _winRt.StopSpeaking();
            _winRt.UpdateSettings(langNorm, volNorm, genderNorm, styleNorm);
            _instant.UpdateSettings(langNorm, volNorm, genderNorm, "instant");
        }
    }

    public void SetOutputDevice(string? deviceName) => _winRt.SetOutputDevice(deviceName);

    public void SetSpeechRate(string? rate)
    {
        _winRt.SetSpeechRate(rate);
        _instant.SetSpeechRate(rate);
    }

    public double SpeakInstant(string text) =>
        SpeakOrQueue(text, chainIfSpeaking: false, interruptCurrent: true);

    public double SpeakOrQueue(string text, bool chainIfSpeaking, bool interruptCurrent)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return 0;

        lock (_dispatchLock)
        {
            if (_disposed) return 0;
            bool useNeural = _style == "neural" && _winRt.IsAvailable;
            if (useNeural)
            {
                _instant.StopSpeaking();
                return _winRt.SpeakOrQueue(text, chainIfSpeaking, interruptCurrent);
            }

            _winRt.StopSpeaking();
            return _instant.SpeakOrQueue(text, chainIfSpeaking, interruptCurrent);
        }
    }

    public void StopSpeaking()
    {
        lock (_dispatchLock)
        {
            _winRt.StopSpeaking();
            _instant.StopSpeaking();
        }
    }

    public void Dispose()
    {
        lock (_dispatchLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _winRt.Dispose();
        _instant.Dispose();
    }
}
