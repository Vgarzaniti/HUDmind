using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HUDMind.Core.Config;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Hybrid Voice Engine combining:
/// 1) Human Neural Voices (es-AR-TomasNeural Male / es-AR-ElenaNeural Female; EN Andrew / Ava)
///    via a persistent warm zero-GPU sidecar when workspace Python runtime is present (async stderr+stdout drained).
/// 2) Instant Windows OneCore Voices (Raul Male / Sabina-Laura Female, &lt;8ms offline, 0% GPU)
///    automatically used in standalone HUDMind.exe installs or if Neural fails online.
/// </summary>
public sealed class HybridVoiceService : IVoiceOutputService
{
    private readonly InstantSapiVoiceService _instant = new();
    private readonly string _workspaceRoot;
    private readonly object _procLock = new();

    private Process? _neuralProc;
    private StreamWriter? _neuralIn;
    private volatile bool _neuralReady;
    private long _lastNeuralFailTick;
    private volatile bool _disposed;

    private string _language = "es";
    private string _gender = "male";
    private string _style = "neural";
    private string _rate = "+15%";
    private double _volume = 0.75;
    private int _reqSeq;

    public HybridVoiceService(string? workspaceRoot = null)
    {
        _workspaceRoot = workspaceRoot ?? ConfigRepository.DiscoverWorkspaceRoot();
    }

    public bool IsNeuralWorkerPresent =>
        File.Exists(Path.Combine(_workspaceRoot, ".venv", "Scripts", "python.exe")) &&
        File.Exists(Path.Combine(_workspaceRoot, "src", "output", "neural_worker.py"));

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
        if (_style == "neural" && IsNeuralWorkerPresent)
            EnsureNeuralWorkerStarted();
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

        if (switchedToInstant && _neuralReady)
            TrySendNeuralCommand(new { cmd = "stop" });

        _instant.UpdateSettings(_language, _volume, _gender, "instant");
        if (_style == "neural" && !_neuralReady && IsNeuralWorkerPresent)
            _ = Task.Run(EnsureNeuralWorkerStarted);
    }

    public void SetSpeechRate(string? rate)
    {
        if (!string.IsNullOrWhiteSpace(rate))
            _rate = rate.Trim();
    }

    public double SpeakInstant(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return 0;

        if (_style == "neural" && IsNeuralWorkerPresent)
        {
            if (!_neuralReady && CanRetryNeuralStart())
                EnsureNeuralWorkerStarted();

            if (_neuralReady && TrySendNeuralCommand(new
            {
                cmd = "speak",
                id = Interlocked.Increment(ref _reqSeq),
                text = text.Trim(),
                voice = ResolveNeuralVoice(_language, _gender),
                rate = _rate,
                volume = _volume
            }))
            {
                _instant.StopSpeaking();
                return 1.5;
            }
        }

        // Instant OneCore path (or automatic fallback when standalone/offline)
        return _instant.SpeakInstant(text);
    }

    public void StopSpeaking()
    {
        _instant.StopSpeaking();
        if (_neuralReady)
            TrySendNeuralCommand(new { cmd = "stop" });
    }

    private bool CanRetryNeuralStart()
    {
        long failTick = Volatile.Read(ref _lastNeuralFailTick);
        return failTick == 0 || Stopwatch.GetElapsedTime(failTick).TotalSeconds >= 20.0;
    }

    private void EnsureNeuralWorkerStarted()
    {
        lock (_procLock)
        {
            if (_disposed || !CanRetryNeuralStart())
                return;
            if (_neuralProc is { HasExited: false } && _neuralReady)
                return;

            var pyExe = Path.Combine(_workspaceRoot, ".venv", "Scripts", "python.exe");
            var workerScript = Path.Combine(_workspaceRoot, "src", "output", "neural_worker.py");
            if (!File.Exists(pyExe) || !File.Exists(workerScript))
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pyExe,
                    WorkingDirectory = _workspaceRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.ArgumentList.Add(workerScript);

                var proc = Process.Start(psi);
                if (proc is null)
                {
                    Volatile.Write(ref _lastNeuralFailTick, Stopwatch.GetTimestamp());
                    return;
                }

                // Drain stderr concurrently to prevent Windows 4KB pipe deadlock!
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!proc.HasExited)
                        {
                            var errLine = await proc.StandardError.ReadLineAsync();
                            if (errLine is null) break;
                        }
                    }
                    catch { }
                });

                var readyTask = proc.StandardOutput.ReadLineAsync();
                bool isValidHandshake = readyTask.Wait(TimeSpan.FromSeconds(4)) &&
                    IsWorkerReadyJson(readyTask.Result);

                if (isValidHandshake)
                {
                    _neuralProc = proc;
                    _neuralIn = proc.StandardInput;
                    _neuralReady = true;
                    Volatile.Write(ref _lastNeuralFailTick, 0);

                    _ = Task.Run(() => DrainWorkerOutputAsync(proc));
                }
                else
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    proc.Dispose();
                    Volatile.Write(ref _lastNeuralFailTick, Stopwatch.GetTimestamp());
                }
            }
            catch
            {
                Volatile.Write(ref _lastNeuralFailTick, Stopwatch.GetTimestamp());
            }
        }
    }

    private static bool IsWorkerReadyJson(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
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
                // Detect runtime synthesis failure so future lines fall back cleanly to OneCore
                if (line.Contains("\"ok\":false", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("\"ok\": false", StringComparison.OrdinalIgnoreCase))
                {
                    Volatile.Write(ref _lastNeuralFailTick, Stopwatch.GetTimestamp());
                }
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
                Volatile.Write(ref _lastNeuralFailTick, Stopwatch.GetTimestamp());
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
