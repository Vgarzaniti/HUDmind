using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;
using HUDMind.Core.Pipeline;

namespace HUDMind.Windows;

/// <summary>
/// Zero-NuGet Win32 `winmm.dll` streaming 24kHz 16-bit mono PCM player for Live Native Audio &amp; WinRT Voice.
/// Uses adaptive startup jitter pre-buffering (160ms prime -> 80ms steady chunks), 4-5ms anti-click edge fades,
/// 48ms trailing silence padding, odd-byte PCM alignment, and lock-free monotonic timing for Acoustic Echo Suppression.
/// </summary>
public sealed class Win32StreamingPcmPlayer : IStreamingPcmPlayer
{
    private const int BytesPerSecond24k16 = 48000;
    private const int StartupPrebufferBytes = 7680;   // 160ms initial jitter pre-buffer when starting from silence
    private const int SteadyCoalesceBytes = 3840;    // 80ms smooth streaming granularity once DMA queue is primed
    private const int TailSilencePaddingBytes = 2304; // 48ms zero-tail padding so DAC drivers do not clip final syllables

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr phwo, int uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    private sealed class ActiveBuffer
    {
        public IntPtr HeaderPtr;
        public IntPtr DataPtr;
    }

    private readonly object _sync = new();
    private readonly Queue<ActiveBuffer> _inFlight = new();
    private readonly MemoryStream _coalesce = new(16384);
    private IntPtr _hWaveOut;
    private string _targetDeviceName = string.Empty;
    private double _volume = 0.85;
    private long _playbackEndsAtTick;
    private bool _needsStreamPrime = true;
    private bool _streamEmittedAudio;
    private bool _disposed;

    private static readonly long EchoHangoverTicks = (long)(0.48 * Stopwatch.Frequency);

    public void SetDevice(string? deviceName)
    {
        var next = (deviceName ?? string.Empty).Trim();
        lock (_sync)
        {
            if (_disposed || string.Equals(_targetDeviceName, next, StringComparison.Ordinal))
                return;
            _targetDeviceName = next;
            CloseWaveOutLocked();
        }
    }

    /// <summary>
    /// Lock-free monotonic check: true while audio is audibly playing (plus 480ms acoustic echo hangover).
    /// </summary>
    public bool IsPlayingAudio
    {
        get
        {
            if (Volatile.Read(ref _volume) <= 0.01)
                return false;
            long endTick = Volatile.Read(ref _playbackEndsAtTick);
            return endTick > 0 && Stopwatch.GetTimestamp() < endTick + EchoHangoverTicks;
        }
    }

    public void SetVolume(double volume) =>
        Volatile.Write(ref _volume, Math.Clamp(volume, 0.0, 1.0));

    private bool EnsureOpenLocked()
    {
        if (_disposed) return false;
        if (_hWaveOut != IntPtr.Zero) return true;

        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = 1,
            nSamplesPerSec = 24000,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = BytesPerSecond24k16,
            cbSize = 0
        };
        int devId = Win32AudioDevices.ResolveOutputDeviceId(_targetDeviceName);
        int res = waveOutOpen(out _hWaveOut, devId, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (res != 0 && devId != Win32AudioDevices.WaveMapper)
            res = waveOutOpen(out _hWaveOut, Win32AudioDevices.WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        return res == 0 && _hWaveOut != IntPtr.Zero;
    }

    public void EnqueuePcm24k(byte[] pcmBytes)
    {
        if (pcmBytes.Length == 0) return;
        lock (_sync)
        {
            if (_disposed || !EnsureOpenLocked())
                return;

            ReclaimCompletedLocked();
            if (_inFlight.Count == 0 && Stopwatch.GetTimestamp() >= Volatile.Read(ref _playbackEndsAtTick))
            {
                _needsStreamPrime = true;
                _streamEmittedAudio = false;
            }

            _coalesce.Write(pcmBytes, 0, pcmBytes.Length);
            int threshold = _needsStreamPrime ? StartupPrebufferBytes : SteadyCoalesceBytes;
            if (_coalesce.Length >= threshold)
            {
                bool priming = _needsStreamPrime;
                if (WriteCoalescedLocked(applyFadeIn: priming, applyFadeOut: false, trailingZeroBytes: 0))
                {
                    _needsStreamPrime = false;
                    _streamEmittedAudio = true;
                }
            }
        }
    }

    public void FlushBuffered()
    {
        lock (_sync)
        {
            if (_disposed) return;
            ReclaimCompletedLocked();
            if (_coalesce.Length >= 2 && EnsureOpenLocked())
            {
                WriteCoalescedLocked(
                    applyFadeIn: _needsStreamPrime,
                    applyFadeOut: true,
                    trailingZeroBytes: TailSilencePaddingBytes);
            }
            else if (_streamEmittedAudio && EnsureOpenLocked())
            {
                SubmitSilencePaddingLocked(TailSilencePaddingBytes);
            }

            _coalesce.SetLength(0);
            _needsStreamPrime = true;
            _streamEmittedAudio = false;
        }
    }

    private bool WriteCoalescedLocked(bool applyFadeIn, bool applyFadeOut, int trailingZeroBytes)
    {
        int total = (int)_coalesce.Length;
        int len = total & ~1;
        if (len < 2) return false;

        byte[] bufAll = _coalesce.GetBuffer();
        int allocBytes = len + trailingZeroBytes;
        byte[] raw = ArrayPool<byte>.Shared.Rent(allocBytes);
        try
        {
            Buffer.BlockCopy(bufAll, 0, raw, 0, len);
            if (trailingZeroBytes == 0 && total > len)
            {
                byte odd = bufAll[len];
                _coalesce.SetLength(0);
                _coalesce.WriteByte(odd);
            }
            else
            {
                _coalesce.SetLength(0);
            }

            ReclaimCompletedLocked();

            var pcmSpan = raw.AsSpan(0, len);
            if (applyFadeIn)
                AudioPcmUtilities.ApplyFadeIn16(pcmSpan, fadeSamples: 120);
            if (applyFadeOut)
                AudioPcmUtilities.ApplyFadeOut16(pcmSpan, fadeSamples: 96);
            if (trailingZeroBytes > 0)
                raw.AsSpan(len, trailingZeroBytes).Clear();

            double vol = Volatile.Read(ref _volume);
            if (vol < 0.99)
                ApplyVolumeInPlace(raw, len, vol);

            return SubmitWaveOutBufferLocked(raw, allocBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private void SubmitSilencePaddingLocked(int silenceBytes)
    {
        if (silenceBytes < 2 || _hWaveOut == IntPtr.Zero)
            return;

        byte[] zeros = ArrayPool<byte>.Shared.Rent(silenceBytes);
        try
        {
            zeros.AsSpan(0, silenceBytes).Clear();
            _ = SubmitWaveOutBufferLocked(zeros, silenceBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private bool SubmitWaveOutBufferLocked(byte[] managedBytes, int byteCount)
    {
        if (byteCount < 2 || _hWaveOut == IntPtr.Zero)
            return false;

        IntPtr dataPtr = IntPtr.Zero;
        IntPtr hdrPtr = IntPtr.Zero;
        try
        {
            dataPtr = Marshal.AllocHGlobal(byteCount);
            Marshal.Copy(managedBytes, 0, dataPtr, byteCount);

            uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
            var hdr = new WAVEHDR { lpData = dataPtr, dwBufferLength = (uint)byteCount };
            hdrPtr = Marshal.AllocHGlobal((int)hdrSize);
            Marshal.StructureToPtr(hdr, hdrPtr, false);

            if (waveOutPrepareHeader(_hWaveOut, hdrPtr, hdrSize) != 0)
                return false;

            if (waveOutWrite(_hWaveOut, hdrPtr, hdrSize) != 0)
            {
                _ = waveOutUnprepareHeader(_hWaveOut, hdrPtr, hdrSize);
                Marshal.FreeHGlobal(hdrPtr);
                Marshal.FreeHGlobal(dataPtr);
                hdrPtr = IntPtr.Zero;
                dataPtr = IntPtr.Zero;
                _ = waveOutReset(_hWaveOut);
                ReclaimAllLocked(maxSpinMs: 40);
                _ = waveOutClose(_hWaveOut);
                _hWaveOut = IntPtr.Zero;
                return false;
            }

            _inFlight.Enqueue(new ActiveBuffer { HeaderPtr = hdrPtr, DataPtr = dataPtr });
            hdrPtr = IntPtr.Zero;
            dataPtr = IntPtr.Zero;

            long nowTick = Stopwatch.GetTimestamp();
            long curEnd = Volatile.Read(ref _playbackEndsAtTick);
            long baseTick = curEnd > nowTick ? curEnd : nowTick;
            long durTicks = (long)((byteCount / (double)BytesPerSecond24k16) * Stopwatch.Frequency);
            Volatile.Write(ref _playbackEndsAtTick, baseTick + durTicks);
            return true;
        }
        finally
        {
            if (hdrPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdrPtr);
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
        }
    }

    public void InterruptAndClear()
    {
        lock (_sync)
        {
            Volatile.Write(ref _playbackEndsAtTick, 0);
            _needsStreamPrime = true;
            _streamEmittedAudio = false;
            if (_disposed) return;
            _coalesce.SetLength(0);
            if (_hWaveOut == IntPtr.Zero) return;
            _ = waveOutReset(_hWaveOut);
            ReclaimAllLocked(maxSpinMs: 45);
        }
    }

    private void ReclaimCompletedLocked()
    {
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        while (_inFlight.Count > 0)
        {
            var head = _inFlight.Peek();
            var hdr = Marshal.PtrToStructure<WAVEHDR>(head.HeaderPtr);
            if ((hdr.dwFlags & Win32AudioDevices.WhdrDone) == 0)
                break;

            int mm = waveOutUnprepareHeader(_hWaveOut, head.HeaderPtr, hdrSize);
            if (mm == Win32AudioDevices.WavErrStillPlaying)
                break;

            _inFlight.Dequeue();
            Marshal.FreeHGlobal(head.HeaderPtr);
            Marshal.FreeHGlobal(head.DataPtr);
        }
    }

    private void ReclaimAllLocked(int maxSpinMs)
    {
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        long t0 = Stopwatch.GetTimestamp();

        while (_inFlight.Count > 0)
        {
            var buf = _inFlight.Peek();
            int mm = waveOutUnprepareHeader(_hWaveOut, buf.HeaderPtr, hdrSize);
            if (mm == Win32AudioDevices.WavErrStillPlaying)
            {
                if (Stopwatch.GetElapsedTime(t0).TotalMilliseconds >= maxSpinMs)
                {
                    _inFlight.Dequeue();
                    continue;
                }
                Thread.Sleep(4);
                continue;
            }

            _inFlight.Dequeue();
            Marshal.FreeHGlobal(buf.HeaderPtr);
            Marshal.FreeHGlobal(buf.DataPtr);
        }
    }

    private static void ApplyVolumeInPlace(byte[] pcm16, int len, double volume)
    {
        int sampleCount = len / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            int scaled = Math.Clamp((int)Math.Round(sample * volume), short.MinValue, short.MaxValue);
            pcm16[i * 2] = (byte)(scaled & 0xFF);
            pcm16[i * 2 + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    private void CloseWaveOutLocked()
    {
        Volatile.Write(ref _playbackEndsAtTick, 0);
        _needsStreamPrime = true;
        _streamEmittedAudio = false;
        if (!_disposed)
            _coalesce.SetLength(0);
        if (_hWaveOut != IntPtr.Zero)
        {
            _ = waveOutReset(_hWaveOut);
            ReclaimAllLocked(maxSpinMs: 60);
            _ = waveOutClose(_hWaveOut);
            _hWaveOut = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            CloseWaveOutLocked();
            _disposed = true;
            _coalesce.Dispose();
        }
    }
}
