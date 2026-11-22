using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using HUDMind.Core.Engine;
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
    private readonly SemaphoreSlim _signal = new(0, 1);

    private SpeechSynthesizer? _synth;
    private Task? _workerTask;
    private CancellationTokenSource? _disposeCts;
    private CancellationTokenSource? _utteranceCts;

    private string? _pendingText;
    private string _language = "es";
    private string _gender = "male";
    private double _rateFactor = 1.15;
    private string _selectedVoiceName = "WinRT";
    private bool _initAttempted;
    private bool _available;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _available;
        }
    }

    public string EngineName => _available ? $"winrt-natural({_selectedVoiceName})" : "winrt-unavailable";

    public void WarmUp(string language, double volume, string gender = "male", string style = "neural")
    {
        if (_disposed) return;
        UpdateSettings(language, volume, gender, style);
        EnsureInitialized();
    }

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
            if (changed && _synth is not null)
                ApplyVoiceSelectionLocked(_synth, _language, _gender);
        }
    }

    public void SetOutputDevice(string? deviceName) => _player.SetDevice(deviceName);

    public void SetSpeechRate(string? rate)
    {
        double factor = ParseRateFactor(rate);
        lock (_sync)
        {
            _rateFactor = factor;
            if (_synth is not null)
                _synth.Options.SpeakingRate = factor;
        }
    }

    public double SpeakInstant(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return 0;

        EnsureInitialized();
        if (!_available)
            return 0;

        long t0 = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            if (_disposed || !_available) return 0;
            _pendingText = text.Trim();
            try { _utteranceCts?.Cancel(); } catch { }
            _player.InterruptAndClear();
        }
        try { _signal.Release(); } catch (SemaphoreFullException) { }
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    public void StopSpeaking()
    {
        lock (_sync)
        {
            _pendingText = null;
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

            string? text;
            SpeechSynthesizer? synth;
            CancellationTokenSource uttCts;
            lock (_sync)
            {
                text = _pendingText;
                _pendingText = null;
                synth = _synth;
                _utteranceCts?.Dispose();
                _utteranceCts = CancellationTokenSource.CreateLinkedTokenSource(disposeCt);
                uttCts = _utteranceCts;
            }

            if (string.IsNullOrWhiteSpace(text) || synth is null)
                continue;

            try
            {
                using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(uttCts.Token).ConfigureAwait(false);
                if (uttCts.IsCancellationRequested)
                    continue;

                using var netStream = stream.AsStreamForRead();
                using var ms = new MemoryStream(checked((int)Math.Min(stream.Size, 4 * 1024 * 1024)));
                await netStream.CopyToAsync(ms, uttCts.Token).ConfigureAwait(false);

                var pcm24k = ConvertWavToPcm24kMono(ms.GetBuffer(), (int)ms.Length);
                if (!uttCts.IsCancellationRequested && pcm24k.Length > 0)
                {
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
            if (chunkSize < 0 || pos + 8 + chunkSize > length)
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
        int srcFrames = dataSize / frameBytes;
        if (srcFrames <= 0)
            return [];

        // Fast exact copy if already 24kHz mono
        if (sampleRate == 24000 && channels == 1)
        {
            var exact = new byte[srcFrames * 2];
            Buffer.BlockCopy(wavBytes, dataOffset, exact, 0, exact.Length);
            return exact;
        }

        int dstFrames = Math.Max(1, (int)((long)srcFrames * 24000 / sampleRate));
        var dst = new byte[dstFrames * 2];
        double ratio = (double)sampleRate / 24000.0;

        for (int i = 0; i < dstFrames; i++)
        {
            double srcIdx = i * ratio;
            int idx0 = Math.Min((int)srcIdx, srcFrames - 1);
            int idx1 = Math.Min(idx0 + 1, srcFrames - 1);
            double frac = srcIdx - idx0;

            short s0 = ReadMonoSample(wavBytes, dataOffset, idx0, channels);
            short s1 = ReadMonoSample(wavBytes, dataOffset, idx1, channels);
            int interp = (int)Math.Round(s0 + (s1 - s0) * frac);
            short clamped = (short)Math.Clamp(interp, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(dst.AsSpan(i * 2, 2), clamped);
        }

        return dst;
    }

    private static short ReadMonoSample(byte[] wavBytes, int dataOffset, int frameIndex, int channels)
    {
        int byteOff = dataOffset + frameIndex * channels * 2;
        short left = BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(byteOff, 2));
        if (channels == 1)
            return left;
        short right = BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(byteOff + 2, 2));
        return (short)((left + right) / 2);
    }

    internal static double ParseRateFactor(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate))
            return 1.15;
        var clean = rate.Trim().TrimEnd('%');
        if (double.TryParse(clean, out double pct))
            return Math.Clamp(1.0 + pct / 100.0, 0.65, 1.75);
        return 1.15;
    }

    public void Dispose()
    {
        CancellationTokenSource? dCts;
        SpeechSynthesizer? synth;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingText = null;
            dCts = _disposeCts;
            _disposeCts = null;
            synth = _synth;
            _synth = null;
            try { _utteranceCts?.Cancel(); } catch { }
            _utteranceCts?.Dispose();
            _utteranceCts = null;
        }

        try { dCts?.Cancel(); } catch { }
        try { _workerTask?.Wait(TimeSpan.FromMilliseconds(400)); } catch { }
        dCts?.Dispose();
        synth?.Dispose();
        _player.Dispose();
        _signal.Dispose();
    }
}
