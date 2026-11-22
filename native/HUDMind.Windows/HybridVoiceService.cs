using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Hybrid Voice Engine combining:
/// 1) Human Neural Voices (es-AR-TomasNeural Male / es-AR-ElenaNeural Female; EN Andrew / Ava)
///    via a persistent warm zero-GPU sidecar with LRU RAM cache and instant Drop-Oldest cancellation.
/// 2) Instant Windows OneCore Voices (Raul Male / Sabina-Laura Female, &lt;8ms offline, 0% GPU)
///    selectable directly or used automatically as instant fallback if offline.
/// </summary>
public sealed class HybridVoiceService : IVoiceOutputService
{
    private readonly InstantSapiVoiceService _instant = new();
    private readonly string _workspaceRoot;
    private readonly object _procLock = new();

    private Process? _neuralProc;
    private StreamWriter? _neuralIn;
    private volatile bool _neuralReady;
    private volatile bool _neuralUnavailable;
    private volatile bool _disposed;

    private string _language = "es";
    private string _gender = "male";
    private string _style = "neural";
    private double _volume = 0.75;
    private int _reqSeq;

    public HybridVoiceService(string? workspaceRoot = null)
    {
        _workspaceRoot = workspaceRoot ?? ConfigRepository.DiscoverWorkspaceRoot();
    }

    public string EngineName
    {
        get
        {
            if (_style == "neural" && _neuralReady)
                return $"neural-humana({ResolveNeuralVoice(_language, _gender)})";
            return _instant.EngineName;
        }
    }

    public static string ResolveNeuralVoice(string language, string gender)
    {
        bool isEn = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        bool isFemale = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase);
        return (isEn, isFemale) switch
        {
            (false, false) => "es-AR-TomasNeural",
            (false, true) => "es-AR-ElenaNeural",
            (true, false) => "en-US-AndrewMultilingualNeural",
            (true, true) => "en-US-AvaMultilingualNeural"
        };
    }

    public void WarmUp(string language, double volume, string gender = "male", string style = "neural")
    {
        if (_disposed) return;
        _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
        _gender = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
        _style = string.Equals(style, "instant", StringComparison.OrdinalIgnoreCase) ? "instant" : "neural";
        _volume = Math.Clamp(volume, 0.0, 1.0);

        _instant.WarmUp(_language, _volume, _gender, "instant");
        if (_style == "neural")
            EnsureNeuralWorkerStarted();
    }

    public void UpdateSettings(string language, double volume, string gender = "male", string style = "neural")
    {
        if (_disposed) return;
        _language = string.IsNullOrWhiteSpace(language) ? "es" : language.ToLowerInvariant();
        _gender = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
        _style = string.Equals(style, "instant", StringComparison.OrdinalIgnoreCase) ? "instant" : "neural";
        _volume = Math.Clamp(volume, 0.0, 1.0);

        _instant.UpdateSettings(_language, _volume, _gender, "instant");
        if (_style == "neural" && !_neuralReady && !_neuralUnavailable)
            _ = Task.Run(EnsureNeuralWorkerStarted);
    }

    public double SpeakInstant(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return 0;

        if (_style == "neural")
        {
            if (!_neuralReady && !_neuralUnavailable)
                EnsureNeuralWorkerStarted();

            if (_neuralReady && TrySendNeuralCommand(new
            {
                cmd = "speak",
                id = Interlocked.Increment(ref _reqSeq),
                text = text.Trim(),
                voice = ResolveNeuralVoice(_language, _gender),
                rate = "+15%",
                volume = _volume
            }))
            {
                // Stop OneCore in case it was speaking before user switched to Neural
                _instant.StopSpeaking();
                return 1.5; // Non-blocking instant dispatch to persistent neural audio worker
            }
        }

        // Instant OneCore path (or automatic zero-gap fallback if Neural worker unavailable)
        return _instant.SpeakInstant(text);
    }

    public void StopSpeaking()
    {
        _instant.StopSpeaking();
        if (_neuralReady)
            TrySendNeuralCommand(new { cmd = "stop" });
    }

    private void EnsureNeuralWorkerStarted()
    {
        lock (_procLock)
        {
            if (_disposed || _neuralUnavailable)
                return;
            if (_neuralProc is { HasExited: false } && _neuralReady)
                return;

            var pyExe = Path.Combine(_workspaceRoot, ".venv", "Scripts", "python.exe");
            var workerScript = Path.Combine(_workspaceRoot, "src", "output", "neural_worker.py");
            if (!File.Exists(pyExe) || !File.Exists(workerScript))
            {
                _neuralUnavailable = true;
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pyExe,
                    Arguments = $"\"{workerScript}\"",
                    WorkingDirectory = _workspaceRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8
                };

                var proc = Process.Start(psi);
                if (proc is null)
                {
                    _neuralUnavailable = true;
                    return;
                }

                // Wait briefly for {"ready": true} handshake
                var readyTask = proc.StandardOutput.ReadLineAsync();
                if (readyTask.Wait(TimeSpan.FromSeconds(4)) &&
                    readyTask.Result is not null &&
                    readyTask.Result.Contains("ready", StringComparison.OrdinalIgnoreCase))
                {
                    _neuralProc = proc;
                    _neuralIn = proc.StandardInput;
                    _neuralReady = true;

                    // Drain stdout asynchronously to detect synthesis errors and avoid pipe deadlock
                    _ = Task.Run(() => DrainWorkerOutputAsync(proc));
                }
                else
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    proc.Dispose();
                    _neuralUnavailable = true;
                }
            }
            catch
            {
                _neuralUnavailable = true;
            }
        }
    }

    private async Task DrainWorkerOutputAsync(Process proc)
    {
        try
        {
            while (!proc.HasExited)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (line is null) break;
            }
        }
        catch
        {
            // Worker exited
        }
        finally
        {
            _neuralReady = false;
        }
    }

    private bool TrySendNeuralCommand(object payload)
    {
        lock (_procLock)
        {
            if (!_neuralReady || _neuralIn is null || _neuralProc is null || _neuralProc.HasExited)
            {
                _neuralReady = false;
                return false;
            }

            try
            {
                var json = JsonSerializer.Serialize(payload);
                _neuralIn.WriteLine(json);
                _neuralIn.Flush();
                return true;
            }
            catch
            {
                _neuralReady = false;
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _instant.Dispose();

        lock (_procLock)
        {
            try
            {
                if (_neuralIn is not null && _neuralProc is { HasExited: false })
                {
                    _neuralIn.WriteLine("{\"cmd\":\"quit\"}");
                    _neuralIn.Flush();
                    if (!_neuralProc.WaitForExit(600))
                        _neuralProc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                try { _neuralProc?.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                _neuralIn?.Dispose();
                _neuralProc?.Dispose();
                _neuralIn = null;
                _neuralProc = null;
                _neuralReady = false;
            }
        }
    }
}
