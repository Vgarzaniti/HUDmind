using System.Diagnostics;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Persistent Windows SAPI5 COM STA worker with 1-slot Drop-Oldest mailbox (&lt;5ms start, 0% GPU, 0 network).
/// Eliminates disposable wait-handle races, unbounded queues, and COM RCW leaks.
/// </summary>
public sealed class InstantSapiVoiceService : IVoiceOutputService
{
    private const int SvsFlagsAsync = 1;
    private const int SvsfPurgeBeforeSpeak = 2;

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
    private double _volume = 0.75;

    public string EngineName => _ready ? $"sapi5-native-instant({_language})" : "sapi5-inactive";

    public void WarmUp(string language, double volume)
    {
        if (_disposed)
            return;

        Task<bool> readyTask;
        lock (_sync)
        {
            if (_disposed) return;
            _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
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
                Name = "HUDMind-SAPI5-STA"
            };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
        }

        readyTask.Wait(TimeSpan.FromSeconds(3));
    }

    public void UpdateSettings(string language, double volume)
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_disposed) return;
            _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
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
            WarmUp(_language, _volume);
        if (!_ready || _disposed)
            return 0;

        var sw = Stopwatch.StartNew();
        lock (_sync)
        {
            if (_disposed) return 0;
            // Drop-Oldest 1-slot mailbox: fresher callout replaces any unstarted stale line immediately
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

            string initLang;
            double initVol;
            lock (_sync)
            {
                initLang = _language;
                initVol = _volume;
            }

            ApplyVoiceSettings(spVoice, initLang, initVol);
            spVoice.Speak(string.Empty, SvsFlagsAsync | SvsfPurgeBeforeSpeak);
            _ready = true;
            _readyTcs?.TrySetResult(true);

            while (true)
            {
                _signal.WaitOne();

                string? textToSpeak;
                bool doStop;
                bool doConfig;
                string lang;
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
                    vol = _volume;
                }

                try
                {
                    if (doConfig)
                        ApplyVoiceSettings(spVoice, lang, vol);

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

    private static void ApplyVoiceSettings(dynamic spVoice, string lang, double volume)
    {
        try
        {
            SelectPreferredVoice(spVoice, lang);
            spVoice.Rate = 2; // Snappy Discord coach cadence
            spVoice.Volume = (int)Math.Round(Math.Clamp(volume, 0.0, 1.0) * 100.0);
        }
        catch
        {
            // Keep existing SAPI5 properties
        }
    }

    private static void SelectPreferredVoice(dynamic spVoice, string lang)
    {
        dynamic? tokens = null;
        var allTokens = new List<object>();
        object? chosen = null;
        try
        {
            tokens = spVoice.GetVoices();
            int count = (int)tokens.Count;
            bool wantEs = lang.StartsWith("es", StringComparison.OrdinalIgnoreCase);
            int bestScore = 0;

            for (int i = 0; i < count; i++)
            {
                dynamic tok = tokens.Item(i);
                allTokens.Add(tok);
                string desc = ((string)tok.GetDescription()).ToLowerInvariant();
                int score = 0;
                if (wantEs)
                {
                    if (desc.Contains("sabina") || desc.Contains("es-mx") || desc.Contains("mexico"))
                        score = 100;
                    else if (desc.Contains("raul") || desc.Contains("pablo") || desc.Contains("es-ar"))
                        score = 95;
                    else if (desc.Contains("helena") || desc.Contains("spanish") || desc.Contains("español"))
                        score = 80;
                }
                else
                {
                    if (desc.Contains("zira") || desc.Contains("david") || desc.Contains("en-us"))
                        score = 100;
                    else if (desc.Contains("english"))
                        score = 80;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    chosen = tok;
                }
            }

            if (chosen is not null)
                spVoice.Voice = chosen;
        }
        catch
        {
            // Keep default Windows SAPI5 voice
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
