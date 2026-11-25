using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HUDMind.Core.AI;
using HUDMind.Core.Engine;
using HUDMind.Core.Pipeline;
using Windows.Media.SpeechSynthesis;
using WinRtGender = Windows.Media.SpeechSynthesis.VoiceGender;

namespace HUDMind.Windows;

/// <summary>
/// Pure native Windows 10/11 WinRT SpeechSynthesizer engine (0% Python, 0% NuGet, 0% GPU)
/// that honors custom SpeakingRate, matches language/gender from Microsoft Natural/OneCore voices,
/// and streams PCM via Win32StreamingPcmPlayer directly to the user-selected output speaker device.
/// </summary>
public sealed class WinRtNaturalVoiceService : IVoiceOutputService
{
    private readonly Win32StreamingPcmPlayer _player = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<(string Text, bool Interrupt)> _pendingQueue = new(4);

    private SpeechSynthesizer? _synth;
    private Task? _workerTask;
    private CancellationTokenSource? _disposeCts;
    private CancellationTokenSource? _utteranceCts;
    private long _utteranceGen;

    private string _language = "es";
    private string _gender = "male";
    private double _rateFactor = 1.05;
    private volatile string _selectedVoiceName = "WinRT";
    private readonly object _voiceConfigLock = new();
    private bool _dirtyVoiceConfig;
    private int _isSynthesizing;
    private bool _initAttempted;
    private volatile bool _available;
    private volatile bool _disposed;

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _available;
        }
    }

    internal bool IsInitializedAndAvailable => _available;

    public bool IsSpeaking => Volatile.Read(ref _isSynthesizing) != 0 || _player.IsPlayingAudio;
    public bool IsActivelySpeaking
    {
        get
        {
            lock (_sync)
                return Volatile.Read(ref _isSynthesizing) != 0 || _player.IsActivelyPlaying || _pendingQueue.Count > 0;
        }
    }
    public bool SupportsContinuationQueue => true;

    public string EngineName => _available ? $"winrt-natural({_selectedVoiceName})" : "winrt-unavailable";

    public void WarmUp(string language, double volume, string gender = "male", string style = "neural")
    {
        if (_disposed) return;
        UpdateSettings(language, volume, gender, style);
        EnsureInitialized();
    }

    internal void EnsureInitializedInternal() => EnsureInitialized();

    public void UpdateSettings(string language, double volume, string gender = "male", string style = "neural")
    {
        if (_disposed) return;
        var nextLang = string.IsNullOrWhiteSpace(language) ? "es" : language.Trim().ToLowerInvariant();
        var nextGender = string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
        _player.SetVolume(volume);

        lock (_sync)
        {
            if (_disposed) return;
            bool changed = !string.Equals(_language, nextLang, StringComparison.OrdinalIgnoreCase) ||
                           !string.Equals(_gender, nextGender, StringComparison.OrdinalIgnoreCase);
            _language = nextLang;
            _gender = nextGender;
            if (changed)
                _dirtyVoiceConfig = true;
        }
    }

    public void SetOutputDevice(string? deviceName) => _player.SetDevice(deviceName);

    public void SetSpeechRate(string? rate)
    {
        double factor = ParseRateFactor(rate);
        lock (_sync)
        {
            if (Math.Abs(_rateFactor - factor) > 0.001)
            {
                _rateFactor = factor;
                _dirtyVoiceConfig = true;
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

        EnsureInitialized();
        if (!_available)
            return 0;

        long t0 = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            if (_disposed || !_available) return 0;

            bool carryInterrupt = interruptCurrent;
            if (interruptCurrent)
            {
                Interlocked.Increment(ref _utteranceGen);
                _pendingQueue.Clear();
                try { _utteranceCts?.Cancel(); } catch { }
            }
            else if (chainIfSpeaking)
            {
                while (_pendingQueue.Count >= 2)
                {
                    carryInterrupt |= _pendingQueue[0].Interrupt;
                    _pendingQueue.RemoveAt(0);
                }
            }
            else
            {
                foreach (var queued in _pendingQueue)
                    carryInterrupt |= queued.Interrupt;
                _pendingQueue.Clear();
            }

            _pendingQueue.Add((cleanSpeech, interruptCurrent));
            if (carryInterrupt && _pendingQueue.Count > 0)
                _pendingQueue[0] = (_pendingQueue[0].Text, true);
        }
        try { _signal.Release(); } catch (ObjectDisposedException) { }
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    public void StopSpeaking()
    {
        lock (_sync)
        {
            Interlocked.Increment(ref _utteranceGen);
            _pendingQueue.Clear();
            try { _utteranceCts?.Cancel(); } catch { }
        }
        _player.InterruptAndClear();
    }

    private void EnsureInitialized()
    {
        lock (_sync)
        {
            if (_initAttempted || _disposed)
                return;
            _initAttempted = true;

            try
            {
                if (SpeechSynthesizer.AllVoices.Count == 0)
                    return;

                var synth = new SpeechSynthesizer();
                synth.Options.SpeakingRate = _rateFactor;
                if (!ApplyVoiceSelectionLocked(synth, _language, _gender))
                {
                    synth.Dispose();
                    return;
                }

                _synth = synth;
                _player.PreOpenDevice();
                _disposeCts = new CancellationTokenSource();
                _available = true;
                var ct = _disposeCts.Token;
                _workerTask = Task.Run(() => WorkerLoopAsync(ct), ct);
            }
            catch
            {
                _available = false;
            }
        }
    }

    private bool ApplyVoiceSelectionLocked(SpeechSynthesizer synth, string language, string gender)
    {
        try
        {
            var voices = SpeechSynthesizer.AllVoices;
            if (voices.Count == 0)
                return false;

            bool wantEn = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            var targetGender = gender == "female" ? WinRtGender.Female : WinRtGender.Male;

            VoiceInformation? best = null;
            int bestScore = int.MinValue;
            foreach (var v in voices)
            {
                var vLang = v.Language ?? string.Empty;
                bool langMatch = wantEn
                    ? vLang.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                    : vLang.StartsWith("es", StringComparison.OrdinalIgnoreCase);
                if (!langMatch)
                    continue;

                int score = 100;
                if (v.Gender == targetGender) score += 200;
                if (v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase) ||
                    v.Id.Contains("Natural", StringComparison.OrdinalIgnoreCase))
                    score += 60;
                if (!wantEn && vLang.StartsWith("es-AR", StringComparison.OrdinalIgnoreCase))
                    score += 30;
                else if (!wantEn && vLang.StartsWith("es-MX", StringComparison.OrdinalIgnoreCase))
                    score += 20;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = v;
                }
            }

            if (best is null || best.Gender != targetGender)
            {
                // Keep exact-gender parity if another voice wasn't strict; fall back cleanly to any matching lang
                best ??= voices.FirstOrDefault(v =>
                    wantEn ? v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                           : v.Language.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                    ?? voices[0];
            }

            synth.Voice = best;
            _selectedVoiceName = best.DisplayName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task WorkerLoopAsync(CancellationToken disposeCt)
    {
        while (!disposeCt.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(disposeCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            string? text = null;
            bool interruptCurrent = true;
            long genAtStart;
            SpeechSynthesizer? synth;
            CancellationTokenSource uttCts;
            bool applyVoiceCfg;
            string desiredLang, desiredGender;
            double desiredRate;
            lock (_sync)
            {
                if (_disposed) break;
                synth = _synth;
                if (_pendingQueue.Count == 0 || synth is null)
                    continue;
                (text, interruptCurrent) = _pendingQueue[0];
                _pendingQueue.RemoveAt(0);
                genAtStart = Interlocked.Read(ref _utteranceGen);
                applyVoiceCfg = _dirtyVoiceConfig;
                _dirtyVoiceConfig = false;
                desiredLang = _language;
                desiredGender = _gender;
                desiredRate = _rateFactor;
                _utteranceCts?.Dispose();
                _utteranceCts = CancellationTokenSource.CreateLinkedTokenSource(disposeCt);
                uttCts = _utteranceCts;
                if (!string.IsNullOrWhiteSpace(text))
                    Interlocked.Exchange(ref _isSynthesizing, 1);
            }

            if (string.IsNullOrWhiteSpace(text))
                continue;

            try
            {
                if (applyVoiceCfg)
                {
                    lock (_voiceConfigLock)
                    {
                        try { synth.Options.SpeakingRate = desiredRate; } catch { }
                        ApplyVoiceSelectionLocked(synth, desiredLang, desiredGender);
                    }
                }

                using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(uttCts.Token).ConfigureAwait(false);
                if (uttCts.IsCancellationRequested || Interlocked.Read(ref _utteranceGen) != genAtStart)
                    continue;

                using var netStream = stream.AsStreamForRead();
                using var ms = new MemoryStream(checked((int)Math.Min(stream.Size, 4 * 1024 * 1024)));
                await netStream.CopyToAsync(ms, uttCts.Token).ConfigureAwait(false);

                var pcm24k = ConvertWavToPcm24kMono(ms.GetBuffer(), (int)ms.Length);
                if (pcm24k.Length == 0)
                    continue;

                AudioPcmUtilities.ApplyFadeOut16(pcm24k, fadeSamples: 96);
                lock (_sync)
                {
                    if (_disposed || uttCts.IsCancellationRequested || Interlocked.Read(ref _utteranceGen) != genAtStart)
                        continue;

                    if (interruptCurrent)
                    {
                        _player.InterruptAndClear();
                    }
                    else if (_player.IsActivelyPlaying)
                    {
                        // Natural ~110ms breath pause between seamlessly chained sentences without cutting current audio
                        _player.EnqueuePcm24k(new byte[5280]);
                    }
                    _player.EnqueuePcm24k(pcm24k);
                    _player.FlushBuffered();
                }
            }
            catch (OperationCanceledException)
            {
                // Barge-in / newer message dropped older synthesis cleanly
            }
            catch
            {
                // Resilient audio path
            }
            finally
            {
                Interlocked.Exchange(ref _isSynthesizing, 0);
            }
        }
    }

    internal static byte[] ConvertWavToPcm24kMono(byte[] wavBytes, int length)
    {
        if (length < 44 ||
            Encoding.ASCII.GetString(wavBytes, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(wavBytes, 8, 4) != "WAVE")
        {
            return [];
        }

        ushort channels = 1;
        uint sampleRate = 16000;
        ushort bitsPerSample = 16;
        ushort audioFormat = 1;
        int dataOffset = -1;
        int dataSize = 0;

        int pos = 12;
        while (pos + 8 <= length)
        {
            var chunkId = Encoding.ASCII.GetString(wavBytes, pos, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(pos + 4, 4));
            if (chunkSize < 0 || chunkSize > length - pos - 8)
                break;

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                var fmt = wavBytes.AsSpan(pos + 8, chunkSize);
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt[..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(14, 2));
            }
            else if (chunkId == "data")
            {
                dataOffset = pos + 8;
                dataSize = chunkSize;
                break;
            }

            pos += 8 + chunkSize + (chunkSize & 1);
        }

        if (audioFormat != 1 || bitsPerSample != 16 || channels is < 1 or > 2 ||
            sampleRate is < 8000 or > 96000 || dataOffset < 0 || dataSize <= 0)
        {
            return [];
        }

        int frameBytes = channels * 2;
        int validDataSize = Math.Min(dataSize, length - dataOffset);
        int srcFrames = validDataSize / frameBytes;
        if (srcFrames <= 0)
            return [];

        return AudioPcmUtilities.ResampleMonoPcm16(
            wavBytes.AsSpan(dataOffset, srcFrames * frameBytes),
            channels,
            sampleRate,
            24000);
    }

    internal static double ParseRateFactor(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate))
            return 1.05;
        var clean = rate.Trim().TrimEnd('%').Replace(',', '.');
        if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            return Math.Clamp(1.0 + pct / 100.0, 0.65, 1.75);
        return 1.05;
    }

    internal static int ParseSapiRateOffset(string? rate)
    {
        double factor = ParseRateFactor(rate);
        double pct = (factor - 1.0) * 100.0;
        return Math.Clamp((int)Math.Round(pct / 12.0), -4, 4);
    }

    public void Dispose()
    {
        CancellationTokenSource? dCts;
        CancellationTokenSource? uCts;
        SpeechSynthesizer? synth;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _utteranceGen);
            _pendingQueue.Clear();
            dCts = _disposeCts;
            _disposeCts = null;
            uCts = _utteranceCts;
            _utteranceCts = null;
            synth = _synth;
            _synth = null;
        }

        try { uCts?.Cancel(); } catch { }
        try { dCts?.Cancel(); } catch { }
        bool workerExited = false;
        try { workerExited = _workerTask?.Wait(TimeSpan.FromMilliseconds(1500)) ?? true; } catch { }
        if (workerExited)
        {
            uCts?.Dispose();
            dCts?.Dispose();
            _signal.Dispose();
            try { synth?.Dispose(); } catch { }
        }
        _player.Dispose();
    }
}
