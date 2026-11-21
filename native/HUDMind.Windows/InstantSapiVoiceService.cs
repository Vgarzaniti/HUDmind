using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Persistent Windows SAPI5 COM STA worker (&lt;5ms start latency, 0% GPU, 0 network, 0 NuGet).
/// </summary>
public sealed class InstantSapiVoiceService : IVoiceOutputService
{
    private const int SvsFlagsAsync = 1;
    private const int SvsfPurgeBeforeSpeak = 2;

    private readonly BlockingCollection<(string Cmd, string Text, ManualResetEventSlim? Done)> _queue = new();
    private Thread? _staThread;
    private volatile bool _ready;
    private string _language = "es";
    private double _volume = 0.75;

    public string EngineName => _ready ? $"sapi5-native-instant({_language})" : "sapi5-inactive";

    public void WarmUp(string language, double volume)
    {
        _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
        _volume = Math.Clamp(volume, 0.0, 1.0);

        if (_staThread is { IsAlive: true })
            return;

        using var readyEv = new ManualResetEventSlim(false);
        _staThread = new Thread(() => StaWorkerLoop(readyEv))
        {
            IsBackground = true,
            Name = "HUDMind-SAPI5-STA"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        readyEv.Wait(TimeSpan.FromSeconds(3));
    }

    public double SpeakInstant(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        if (!_ready)
            WarmUp(_language, _volume);
        if (!_ready)
            return 0;

        var sw = Stopwatch.StartNew();
        using var done = new ManualResetEventSlim(false);
        _queue.Add(("speak", text.Trim(), done));
        done.Wait(TimeSpan.FromSeconds(1));
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    public void StopSpeaking()
    {
        if (_ready)
            _queue.Add(("stop", string.Empty, null));
    }

    private void StaWorkerLoop(ManualResetEventSlim readyEv)
    {
        dynamic? spVoice = null;
        try
        {
            var sapiType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (sapiType is null)
            {
                readyEv.Set();
                return;
            }

            spVoice = Activator.CreateInstance(sapiType);
            if (spVoice is null)
            {
                readyEv.Set();
                return;
            }

            SelectPreferredVoice(spVoice, _language);
            spVoice.Rate = 2; // Snappy Discord eSports coach cadence (-10..+10)
            spVoice.Volume = (int)Math.Round(_volume * 100.0);
            spVoice.Speak(string.Empty, SvsFlagsAsync | SvsfPurgeBeforeSpeak);
            _ready = true;
            readyEv.Set();

            foreach (var (cmd, text, done) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (cmd == "speak" && !string.IsNullOrEmpty(text))
                        spVoice.Speak(text, SvsFlagsAsync | SvsfPurgeBeforeSpeak);
                    else if (cmd == "stop")
                        spVoice.Speak(string.Empty, SvsFlagsAsync | SvsfPurgeBeforeSpeak);
                }
                catch
                {
                    // Keep voice worker alive
                }
                finally
                {
                    done?.Set();
                }
            }
        }
        catch
        {
            _ready = false;
            readyEv.Set();
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

    private static void SelectPreferredVoice(dynamic spVoice, string lang)
    {
        try
        {
            dynamic tokens = spVoice.GetVoices();
            int count = (int)tokens.Count;
            bool wantEs = lang.StartsWith("es", StringComparison.OrdinalIgnoreCase);
            dynamic? best = null;
            int bestScore = 0;

            for (int i = 0; i < count; i++)
            {
                dynamic tok = tokens.Item(i);
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
                    best = tok;
                }
            }

            if (best is not null)
                spVoice.Voice = best;
        }
        catch
        {
            // Keep default Windows SAPI5 voice
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _staThread?.Join(TimeSpan.FromSeconds(1));
        _queue.Dispose();
    }
}
