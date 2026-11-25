using System.Diagnostics;
using System.Runtime.InteropServices;
using HUDMind.Core.AI;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Persistent Windows OneCore/SAPI5 COM STA worker with 1-slot Drop-Oldest mailbox (&lt;8ms start, 0% GPU, 0 network).
/// Reads COM Gender &amp; Language LCID attributes to strictly avoid cross-gender fallback mistakes (e.g. Zira on Male),
/// releases COM tokens cleanly, and separates cheap Volume updates from voice token re-enumeration.
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
    private object? _activeVoiceToken;

    private readonly List<(string Text, bool Purge, long EstTicks)> _pendingQueue = new(4);
    private bool _pendingStop;
    private bool _pendingConfigUpdate;
    private bool _pendingVoiceReselect;
    private bool _shutdownRequested;
    private volatile bool _ready;
    private volatile bool _initFailed;
    private volatile bool _disposed;

    private string _language = "es";
    private string _gender = "male";
    private double _volume = 0.75;
    private int _sapiRate;
    private volatile string _selectedVoiceName = "OneCore";
    private long _speakingUntilTick;

    public string EngineName => _ready ? $"onecore-instant({_selectedVoiceName})" : "onecore-inactive";
    public bool SupportsContinuationQueue => true;

    public bool IsSpeaking
    {
        get
        {
            long until = Volatile.Read(ref _speakingUntilTick);
            return until > 0 && Stopwatch.GetTimestamp() < until;
        }
    }

    public bool IsActivelySpeaking => IsSpeaking;

    public void SetSpeechRate(string? rate)
    {
        int nextRate = WinRtNaturalVoiceService.ParseSapiRateOffset(rate);
        lock (_sync)
        {
            if (_disposed || _sapiRate == nextRate)
                return;
            Volatile.Write(ref _sapiRate, nextRate);
            if (_staThread is { IsAlive: true })
            {
                _pendingConfigUpdate = true;
                _signal.Set();
            }
        }
    }

    public void WarmUp(string language, double volume, string gender = "male", string style = "instant") =>
        WarmUpCore(useStoredSettings: false, language, volume, gender);

    internal void EnsureInitializedFromStoredSettings() =>
        WarmUpCore(useStoredSettings: true, string.Empty, 0, string.Empty);

    private void WarmUpCore(bool useStoredSettings, string language, double volume, string gender)
    {
        if (_disposed || _initFailed)
            return;

        Task<bool> readyTask;
        lock (_sync)
        {
            if (_disposed || _initFailed) return;
            bool voiceChanged = false;
            if (!useStoredSettings)
            {
                var newLang = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
                var newGender = NormalizeGender(gender);
                voiceChanged = !string.Equals(_language, newLang, StringComparison.OrdinalIgnoreCase) ||
                               !string.Equals(_gender, newGender, StringComparison.OrdinalIgnoreCase);

                _language = newLang;
                _gender = newGender;
                _volume = Math.Clamp(volume, 0.0, 1.0);
            }

            if (_staThread is { IsAlive: true })
            {
                _pendingConfigUpdate = true;
                if (voiceChanged) _pendingVoiceReselect = true;
                _signal.Set();
                var inProgressReady = _readyTcs?.Task;
                if (_ready || inProgressReady is null)
                    return;
                readyTask = inProgressReady;
            }
            else
            {
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
        }

        if (!readyTask.Wait(TimeSpan.FromSeconds(3)) || !readyTask.Result)
            _initFailed = true;
    }

    public void UpdateSettings(string language, double volume, string gender = "male", string style = "instant")
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_disposed) return;
            var newLang = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
            var newGender = NormalizeGender(gender);
            bool voiceChanged = !string.Equals(_language, newLang, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(_gender, newGender, StringComparison.OrdinalIgnoreCase);

            _language = newLang;
            _gender = newGender;
            _volume = Math.Clamp(volume, 0.0, 1.0);

            if (_staThread is { IsAlive: true })
            {
                _pendingConfigUpdate = true;
                if (voiceChanged) _pendingVoiceReselect = true;
                _signal.Set();
            }
        }
    }

    public double SpeakInstant(string text) =>
        SpeakOrQueue(text, chainIfSpeaking: false, interruptCurrent: true);

    public double SpeakOrQueue(string text, bool chainIfSpeaking, bool interruptCurrent)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return 0;

        var cleanSpeech = CoachContentGuard.SanitizeForSpeech(text);
        if (string.IsNullOrWhiteSpace(cleanSpeech))
            return 0;

        if (!_ready && !_initFailed)
            WarmUpCore(useStoredSettings: true, string.Empty, 0, string.Empty);
        if (!_ready || _disposed)
            return 0;

        long t0 = Stopwatch.GetTimestamp();
        int curRate = Volatile.Read(ref _sapiRate);
        double rateFactor = Math.Clamp(1.0 + curRate * 0.12, 0.6, 1.8);
        double estSeconds = Math.Clamp((cleanSpeech.Length / 13.5) / rateFactor + 0.35, 0.6, 18.0);
        long durTicks = (long)(estSeconds * Stopwatch.Frequency);
        lock (_sync)
        {
            if (_disposed) return 0;
            bool hadPendingStop = _pendingStop;
            bool shouldPurge = interruptCurrent || !chainIfSpeaking || hadPendingStop;
            if (shouldPurge)
            {
                _pendingQueue.Clear();
                Volatile.Write(ref _speakingUntilTick, t0 + durTicks);
                _pendingQueue.Add((cleanSpeech, shouldPurge, durTicks));
            }
            else
            {
                long droppedTicks = 0;
                bool evictedPurge = false;
                while (_pendingQueue.Count >= 3)
                {
                    var dropped = _pendingQueue[0];
                    _pendingQueue.RemoveAt(0);
                    droppedTicks += dropped.EstTicks;
                    evictedPurge |= dropped.Purge;
                }
                long prevUntil = Volatile.Read(ref _speakingUntilTick) - droppedTicks;
                long baseTick = prevUntil > t0 ? prevUntil : t0;
                Volatile.Write(ref _speakingUntilTick, baseTick + durTicks);
                _pendingQueue.Add((cleanSpeech, shouldPurge, durTicks));
                if (evictedPurge && _pendingQueue.Count > 0)
                {
                    var head = _pendingQueue[0];
                    _pendingQueue[0] = (head.Text, true, head.EstTicks);
                }
            }
            _pendingStop = false;
            _signal.Set();
        }
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    public void StopSpeaking()
    {
        lock (_sync)
        {
            Volatile.Write(ref _speakingUntilTick, 0);
            _pendingQueue.Clear();
            if (_disposed || !_ready) return;
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
            int initRate;
            lock (_sync)
            {
                initLang = _language;
                initGender = _gender;
                initVol = _volume;
                initRate = _sapiRate;
            }

            ApplyVoiceSettings(spVoice, initLang, initGender, initVol, initRate, reselectVoice: true);
            spVoice.Speak(string.Empty, SvsFlagsAsync | SvsfPurgeBeforeSpeak);
            _ready = true;
            _readyTcs?.TrySetResult(true);

            while (true)
            {
                _signal.WaitOne();

                List<(string Text, bool Purge)>? batch = null;
                bool doStop;
                bool doConfig;
                bool doReselect;
                string lang, gender;
                double vol;
                int rate;

                lock (_sync)
                {
                    if (_shutdownRequested)
                        break;
                    if (_pendingQueue.Count > 0)
                    {
                        batch = new List<(string Text, bool Purge)>(_pendingQueue.Count);
                        foreach (var item in _pendingQueue)
                            batch.Add((item.Text, item.Purge));
                        _pendingQueue.Clear();
                    }
                    doStop = _pendingStop;
                    _pendingStop = false;
                    doConfig = _pendingConfigUpdate;
                    _pendingConfigUpdate = false;
                    doReselect = _pendingVoiceReselect;
                    _pendingVoiceReselect = false;
                    lang = _language;
                    gender = _gender;
                    vol = _volume;
                    rate = Volatile.Read(ref _sapiRate);
                }

                try
                {
                    if (doConfig)
                        ApplyVoiceSettings(spVoice, lang, gender, vol, rate, doReselect);

                    if (doStop)
                        spVoice.Speak(string.Empty, SvsFlagsAsync | SvsfPurgeBeforeSpeak);

                    if (batch is not null)
                    {
                        foreach (var (textToSpeak, doPurge) in batch)
                        {
                            if (!string.IsNullOrEmpty(textToSpeak))
                                spVoice.Speak(textToSpeak, doPurge ? (SvsFlagsAsync | SvsfPurgeBeforeSpeak) : SvsFlagsAsync);
                        }
                    }
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
            if (_activeVoiceToken is not null)
            {
                try { Marshal.ReleaseComObject(_activeVoiceToken); } catch { }
                _activeVoiceToken = null;
            }
            _ready = false;
        }
    }

    private void ApplyVoiceSettings(dynamic spVoice, string lang, string gender, double volume, int rate, bool reselectVoice)
    {
        try
        {
            if (reselectVoice)
                SelectBestVoice(spVoice, lang, gender);
            spVoice.Rate = Math.Clamp(rate, -10, 10);
            spVoice.Volume = (int)Math.Round(Math.Clamp(volume, 0.0, 1.0) * 100.0);
        }
        catch { }
    }

    private void SelectBestVoice(dynamic spVoice, string lang, string gender)
    {
        bool wantEs = lang.StartsWith("es", StringComparison.OrdinalIgnoreCase);
        bool wantMale = gender == "male";

        if (TrySelectFromOneCore(spVoice, wantEs, wantMale))
            return;

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
                string attrGender = TryGetAttr(tok, "Gender");
                string attrLang = TryGetAttr(tok, "Language");

                int score = ScoreVoice(desc, attrGender, attrLang, wantEs, wantMale);
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
                if (_activeVoiceToken is not null && !ReferenceEquals(_activeVoiceToken, chosen))
                {
                    try { Marshal.ReleaseComObject(_activeVoiceToken); } catch { }
                }
                _activeVoiceToken = chosen;
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

    private static string TryGetAttr(dynamic tok, string name)
    {
        try { return ((string)tok.GetAttribute(name)).Trim().ToLowerInvariant(); }
        catch { return string.Empty; }
    }

    internal static int ScoreVoice(string desc, string attrGender, string attrLangLcid, bool wantEs, bool wantMale)
    {
        bool isEsVoice = attrLangLcid.EndsWith("0a", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("spanish") || desc.Contains("español") ||
                         desc.Contains("raul") || desc.Contains("pablo") || desc.Contains("sabina") || desc.Contains("helena") || desc.Contains("laura");
        bool isEnVoice = attrLangLcid is "409" or "809" or "c09" ||
                         attrLangLcid.EndsWith("09", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("english") || desc.Contains("david") || desc.Contains("mark") || desc.Contains("zira") || desc.Contains("hazel");

        // Strictly require language match if specified
        if (wantEs && !isEsVoice) return 0;
        if (!wantEs && !isEnVoice) return 0;

        // Strictly reject explicit opposite-gender tokens (prevents selecting Zira female for Male EN!)
        if (wantMale && attrGender == "female") return 0;
        if (!wantMale && attrGender == "male") return 0;

        if (wantEs)
        {
            if (wantMale)
            {
                if (desc.Contains("raul")) return 130;
                if (desc.Contains("pablo")) return 120;
                if (desc.Contains("jorge") || desc.Contains("tomas") || desc.Contains("alvaro")) return 115;
                if (attrGender == "male") return 90;
            }
            else
            {
                if (desc.Contains("sabina")) return 130;
                if (desc.Contains("laura")) return 125;
                if (desc.Contains("helena")) return 110;
                if (desc.Contains("elena") || desc.Contains("dalia")) return 120;
                if (attrGender == "female") return 90;
            }
        }
        else
        {
            if (wantMale)
            {
                if (desc.Contains("mark")) return 130;
                if (desc.Contains("david")) return 125;
                if (desc.Contains("george")) return 115;
                if (attrGender == "male") return 90;
            }
            else
            {
                if (desc.Contains("zira")) return 130;
                if (desc.Contains("hazel") || desc.Contains("aria") || desc.Contains("eva")) return 120;
                if (attrGender == "female") return 90;
            }
        }
        return attrGender == string.Empty ? 20 : 0;
    }

    private static string ShortVoiceLabel(string desc)
    {
        if (desc.Contains("Raul", StringComparison.OrdinalIgnoreCase)) return "Raul-H";
        if (desc.Contains("Pablo", StringComparison.OrdinalIgnoreCase)) return "Pablo-H";
        if (desc.Contains("Jorge", StringComparison.OrdinalIgnoreCase)) return "Jorge-H";
        if (desc.Contains("Tomas", StringComparison.OrdinalIgnoreCase)) return "Tomas-H";
        if (desc.Contains("Alvaro", StringComparison.OrdinalIgnoreCase)) return "Alvaro-H";
        if (desc.Contains("Sabina", StringComparison.OrdinalIgnoreCase)) return "Sabina-M";
        if (desc.Contains("Laura", StringComparison.OrdinalIgnoreCase)) return "Laura-M";
        if (desc.Contains("Helena", StringComparison.OrdinalIgnoreCase)) return "Helena-M";
        if (desc.Contains("Elena", StringComparison.OrdinalIgnoreCase)) return "Elena-M";
        if (desc.Contains("Dalia", StringComparison.OrdinalIgnoreCase)) return "Dalia-M";
        if (desc.Contains("David", StringComparison.OrdinalIgnoreCase)) return "David-H";
        if (desc.Contains("Mark", StringComparison.OrdinalIgnoreCase)) return "Mark-H";
        if (desc.Contains("George", StringComparison.OrdinalIgnoreCase)) return "George-H";
        if (desc.Contains("Zira", StringComparison.OrdinalIgnoreCase)) return "Zira-M";
        if (desc.Contains("Hazel", StringComparison.OrdinalIgnoreCase)) return "Hazel-M";
        if (desc.Contains("Aria", StringComparison.OrdinalIgnoreCase)) return "Aria-M";
        if (desc.Contains("Eva", StringComparison.OrdinalIgnoreCase)) return "Eva-M";
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
            _pendingQueue.Clear();
            Volatile.Write(ref _speakingUntilTick, 0);
            _signal.Set();
            threadToJoin = _staThread;
        }

        bool joined = threadToJoin?.Join(TimeSpan.FromSeconds(1.5)) ?? true;
        if (joined)
            _signal.Dispose();
    }
}
