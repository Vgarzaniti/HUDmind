using System.Diagnostics;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Persistent Windows OneCore/SAPI5 COM STA worker with 1-slot Drop-Oldest mailbox (&lt;8ms start, 0% GPU, 0 network).
/// Loads modern Windows Speech_OneCore voices first (unlocking Male Raul/Pablo &amp; Female Sabina/Laura)
/// and falls back cleanly to standard SAPI5 Desktop voices.
/// </summary>
public sealed class InstantSapiVoiceService : IVoiceOutputService
{
    private const int SvsFlagsAsync = 1;
    private const int SvsfPurgeBeforeSpeak = 2;
    private const string OneCoreVoicesRegPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices";

    private readonly object _sync = new();
    private readonly AutoResetEvent _signal = new(false);
    private TaskCompletionSource<bool>? _readyTcs;
    private Thread? _staThread;

    private string? _pendingText;
    private bool _pendingStop;
    private bool _pendingConfigUpdate;
    private bool _shutdownRequested;
    private volatile bool _ready;
    private volatile bool _disposed;

    private string _language = "es";
    private string _gender = "male";
    private double _volume = 0.75;
    private string _selectedVoiceName = "OneCore";

    public string EngineName => _ready ? $"onecore-instant({_selectedVoiceName})" : "onecore-inactive";

    public void WarmUp(string language, double volume, string gender = "male", string style = "instant")
    {
        if (_disposed)
            return;

        Task<bool> readyTask;
        lock (_sync)
        {
            if (_disposed) return;
            _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
            _gender = NormalizeGender(gender);
            _volume = Math.Clamp(volume, 0.0, 1.0);

            if (_staThread is { IsAlive: true })
            {
                _pendingConfigUpdate = true;
                _signal.Set();
                return;
            }

            _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            readyTask = _readyTcs.Task;
            _shutdownRequested = false;

            _staThread = new Thread(StaWorkerLoop)
            {
                IsBackground = true,
                Name = "HUDMind-OneCore-SAPI5-STA"
            };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
        }

        readyTask.Wait(TimeSpan.FromSeconds(3));
    }

    public void UpdateSettings(string language, double volume, string gender = "male", string style = "instant")
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_disposed) return;
            _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
            _gender = NormalizeGender(gender);
            _volume = Math.Clamp(volume, 0.0, 1.0);
            if (_staThread is { IsAlive: true })
            {
                _pendingConfigUpdate = true;
                _signal.Set();
            }
        }
    }

    public double SpeakInstant(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return 0;

        if (!_ready)
            WarmUp(_language, _volume, _gender);
        if (!_ready || _disposed)
            return 0;

        var sw = Stopwatch.StartNew();
        lock (_sync)
        {
            if (_disposed) return 0;
            _pendingText = text.Trim();
            _pendingStop = false;
            _signal.Set();
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    public void StopSpeaking()
    {
        if (_disposed || !_ready) return;
        lock (_sync)
        {
            if (_disposed) return;
            _pendingText = null;
            _pendingStop = true;
            _signal.Set();
        }
    }

    private static string NormalizeGender(string? g) =>
        string.Equals(g?.Trim(), "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";

    private void StaWorkerLoop()
    {
        dynamic? spVoice = null;
        try
        {
            var sapiType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (sapiType is null)
            {
                _readyTcs?.TrySetResult(false);
                return;
            }

            spVoice = Activator.CreateInstance(sapiType);
            if (spVoice is null)
            {
                _readyTcs?.TrySetResult(false);
                return;
            }

            string initLang, initGender;
            double initVol;
            lock (_sync)
            {
                initLang = _language;
                initGender = _gender;
                initVol = _volume;
            }

            ApplyVoiceSettings(spVoice, initLang, initGender, initVol);
            spVoice.Speak(string.Empty, SvsFlagsAsync | SvsfPurgeBeforeSpeak);
            _ready = true;
            _readyTcs?.TrySetResult(true);

            while (true)
            {
                _signal.WaitOne();

                string? textToSpeak;
                bool doStop;
                bool doConfig;
                string lang, gender;
                double vol;

                lock (_sync)
                {
                    if (_shutdownRequested)
                        break;
                    textToSpeak = _pendingText;
                    _pendingText = null;
                    doStop = _pendingStop;
                    _pendingStop = false;
                    doConfig = _pendingConfigUpdate;
                    _pendingConfigUpdate = false;
                    lang = _language;
                    gender = _gender;
                    vol = _volume;
                }

                try
                {
                    if (doConfig)
                        ApplyVoiceSettings(spVoice, lang, gender, vol);

                    if (doStop)
                        spVoice.Speak(string.Empty, SvsFlagsAsync | SvsfPurgeBeforeSpeak);
                    else if (!string.IsNullOrEmpty(textToSpeak))
                        spVoice.Speak(textToSpeak, SvsFlagsAsync | SvsfPurgeBeforeSpeak);
                }
                catch
                {
                    // Keep persistent STA worker healthy
                }
            }
        }
        catch
        {
            _ready = false;
            _readyTcs?.TrySetResult(false);
        }
        finally
        {
            if (spVoice is not null)
            {
                try { spVoice.Speak(string.Empty, SvsFlagsAsync | SvsfPurgeBeforeSpeak); } catch { }
                try { Marshal.FinalReleaseComObject(spVoice); } catch { }
            }
            _ready = false;
        }
    }

    private void ApplyVoiceSettings(dynamic spVoice, string lang, string gender, double volume)
    {
        try
        {
            SelectBestVoice(spVoice, lang, gender);
            spVoice.Rate = 1; // Natural conversational coaching speed (-10..+10)
            spVoice.Volume = (int)Math.Round(Math.Clamp(volume, 0.0, 1.0) * 100.0);
        }
        catch
        {
            // Keep existing SAPI5 properties
        }
    }

    private void SelectBestVoice(dynamic spVoice, string lang, string gender)
    {
        bool wantEs = lang.StartsWith("es", StringComparison.OrdinalIgnoreCase);
        bool wantMale = gender == "male";

        // 1. Try modern Windows Speech_OneCore voices first (includes Raul/Pablo Male + Sabina/Laura Female!)
        if (TrySelectFromOneCore(spVoice, wantEs, wantMale))
            return;

        // 2. Fallback to standard SAPI5 Desktop voices
        TrySelectFromTokenList(spVoice, spVoice.GetVoices(), wantEs, wantMale);
    }

    private bool TrySelectFromOneCore(dynamic spVoice, bool wantEs, bool wantMale)
    {
        dynamic? cat = null;
        try
        {
            var catType = Type.GetTypeFromProgID("SAPI.SpObjectTokenCategory");
            if (catType is null) return false;
            cat = Activator.CreateInstance(catType);
            if (cat is null) return false;
            cat.SetId(OneCoreVoicesRegPath, false);
            dynamic tokens = cat.EnumerateTokens();
            return TrySelectFromTokenList(spVoice, tokens, wantEs, wantMale);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (cat is not null)
            {
                try { Marshal.ReleaseComObject(cat); } catch { }
            }
        }
    }

    private bool TrySelectFromTokenList(dynamic spVoice, dynamic tokens, bool wantEs, bool wantMale)
    {
        var allTokens = new List<object>();
        object? chosen = null;
        string chosenLabel = string.Empty;
        try
        {
            int count = (int)tokens.Count;
            int bestScore = 0;

            for (int i = 0; i < count; i++)
            {
                dynamic tok = tokens.Item(i);
                allTokens.Add(tok);
                string desc = ((string)tok.GetDescription()).ToLowerInvariant();
                int score = ScoreVoice(desc, wantEs, wantMale);
                if (score > bestScore)
                {
                    bestScore = score;
                    chosen = tok;
                    chosenLabel = (string)tok.GetDescription();
                }
            }

            if (chosen is not null && bestScore > 0)
            {
                spVoice.Voice = chosen;
                _selectedVoiceName = ShortVoiceLabel(chosenLabel);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var tok in allTokens)
            {
                if (!ReferenceEquals(tok, chosen))
                {
                    try { Marshal.ReleaseComObject(tok); } catch { }
                }
            }
            if (tokens is not null)
            {
                try { Marshal.ReleaseComObject(tokens); } catch { }
            }
        }
    }

    private static int ScoreVoice(string desc, bool wantEs, bool wantMale)
    {
        if (wantEs)
        {
            if (wantMale)
            {
                if (desc.Contains("raul")) return 120;
                if (desc.Contains("pablo")) return 110;
                if (desc.Contains("jorge") || desc.Contains("tomas") || desc.Contains("alvaro")) return 105;
                if (desc.Contains("spanish") || desc.Contains("español")) return 40;
            }
            else
            {
                if (desc.Contains("sabina")) return 120;
                if (desc.Contains("laura")) return 115;
                if (desc.Contains("helena")) return 100;
                if (desc.Contains("elena") || desc.Contains("dalia")) return 110;
                if (desc.Contains("spanish") || desc.Contains("español")) return 40;
            }
        }
        else
        {
            if (wantMale)
            {
                if (desc.Contains("mark")) return 120;
                if (desc.Contains("david")) return 115;
                if (desc.Contains("george")) return 105;
                if (desc.Contains("english")) return 40;
            }
            else
            {
                if (desc.Contains("zira")) return 120;
                if (desc.Contains("hazel") || desc.Contains("aria") || desc.Contains("eva")) return 110;
                if (desc.Contains("english")) return 40;
            }
        }
        return 0;
    }

    private static string ShortVoiceLabel(string desc)
    {
        if (desc.Contains("Raul", StringComparison.OrdinalIgnoreCase)) return "Raul-H";
        if (desc.Contains("Pablo", StringComparison.OrdinalIgnoreCase)) return "Pablo-H";
        if (desc.Contains("Sabina", StringComparison.OrdinalIgnoreCase)) return "Sabina-M";
        if (desc.Contains("Laura", StringComparison.OrdinalIgnoreCase)) return "Laura-M";
        if (desc.Contains("Helena", StringComparison.OrdinalIgnoreCase)) return "Helena-M";
        if (desc.Contains("David", StringComparison.OrdinalIgnoreCase)) return "David-H";
        if (desc.Contains("Mark", StringComparison.OrdinalIgnoreCase)) return "Mark-H";
        if (desc.Contains("Zira", StringComparison.OrdinalIgnoreCase)) return "Zira-M";
        return "OneCore";
    }

    public void Dispose()
    {
        Thread? threadToJoin;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdownRequested = true;
            _pendingText = null;
            _signal.Set();
            threadToJoin = _staThread;
        }

        threadToJoin?.Join(TimeSpan.FromSeconds(1.5));
        _signal.Dispose();
    }
}
