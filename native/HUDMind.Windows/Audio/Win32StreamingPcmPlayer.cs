using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;
using HUDMind.Core.Pipeline;

namespace HUDMind.Windows;

/// <summary>
/// Zero-NuGet Win32 `winmm.dll` streaming 24kHz 16-bit mono PCM player for Live Native Audio &amp; WinRT Voice.
/// Uses adaptive startup jitter pre-buffering (300ms prime -> 100ms steady chunks), 40ms DAC lead-in,
/// 4ms tail-sample retention for guaranteed end-of-turn fade-out, 48ms trailing silence padding,
/// leak-safe stale WAVEHDR reclamation, and hardware pre-open support.
/// </summary>
public sealed class Win32StreamingPcmPlayer : IStreamingPcmPlayer
{
    private const int BytesPerSecond24k16 = 48000;
    private const int StartupPrebufferBytes = 14400;   // 300ms solid initial jitter pre-buffer from silence
    private const int SteadyCoalesceBytes = 4800;      // 100ms steady DMA chunks (fast recovery without long starvation)
    private const int MaxWaveHeaderBytes = 19200;      // 400ms max DMA buffer per WAVEHDR to avoid driver glitches
    private const int LeadInSilenceBytes = 1920;       // 40ms silent DAC/USB/Bluetooth wake-up before syllable 1
    private const int TailHoldbackBytes = 192;         // 4ms overlap retained so ONLY FlushBuffered fades out end-of-turn
    private const int TailSilencePaddingBytes = 2304;  // 48ms zero-tail padding so DAC drivers do not clip final syllables

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
        public long VirtualDoneTick;
    }

    private readonly object _sync = new();
    private readonly Queue<ActiveBuffer> _inFlight = new();
    private readonly Queue<ActiveBuffer> _staleInFlight = new();
    private readonly MemoryStream _coalesce = new(16384);
    private readonly Action<int>? _onSliceSubmitted;
    private readonly bool _headlessVirtualMode;

    private IntPtr _hWaveOut;
    private bool _virtualOpened;
    private long _virtualPlaybackEndsTick;
    private string _targetDeviceName = string.Empty;
    private double _volume = 0.85;
    private long _playbackEndsAtTick;
    private bool _needsStreamPrime = true;
    private bool _streamEmittedAudio;
    private bool _disposed;

    private static readonly long EchoHangoverTicks = (long)(0.48 * Stopwatch.Frequency);
    private static readonly long IdleReprimeGapTicks = (long)(0.95 * Stopwatch.Frequency);

    public Win32StreamingPcmPlayer() : this(null, false) { }

    internal Win32StreamingPcmPlayer(Action<int>? onSliceSubmitted = null, bool headlessVirtualMode = false)
    {
        _onSliceSubmitted = onSliceSubmitted;
        _headlessVirtualMode = headlessVirtualMode;
    }

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
    /// True while audio is audibly playing PLUS 480ms acoustic echo hangover for Live AEC mic shielding.
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

    /// <summary>
    /// True strictly while PCM samples are actively scheduled to play on DAC hardware (without the 480ms AEC tail).
    /// </summary>
    public bool IsActivelyPlaying
    {
        get
        {
            if (Volatile.Read(ref _volume) <= 0.01)
                return false;
            long endTick = Volatile.Read(ref _playbackEndsAtTick);
            return endTick > 0 && Stopwatch.GetTimestamp() < endTick;
        }
    }

    public void SetVolume(double volume) =>
        Volatile.Write(ref _volume, Math.Clamp(volume, 0.0, 1.0));

    public void PreOpenDevice()
    {
        lock (_sync)
            _ = EnsureOpenLocked();
    }

    private bool EnsureOpenLocked()
    {
        if (_disposed) return false;
        if (_headlessVirtualMode)
        {
            _virtualOpened = true;
            return true;
        }
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
            long nowTick = Stopwatch.GetTimestamp();
            long endTick = Volatile.Read(ref _playbackEndsAtTick);

            // Re-prime ONLY if starting a fresh stream or truly idle > 950ms past playback end.
            if (_inFlight.Count == 0 && endTick > 0 && nowTick >= endTick + IdleReprimeGapTicks)
            {
                _needsStreamPrime = true;
                _streamEmittedAudio = false;
            }

            bool recoveredFromMidTurnGap = !_needsStreamPrime && _streamEmittedAudio && _inFlight.Count == 0;
            _coalesce.Write(pcmBytes, 0, pcmBytes.Length);

            int threshold = _needsStreamPrime
                ? StartupPrebufferBytes + TailHoldbackBytes
                : SteadyCoalesceBytes + TailHoldbackBytes;
            if (_coalesce.Length >= threshold)
            {
                bool priming = _needsStreamPrime;
                if (WriteCoalescedLocked(
                    applyFadeIn: priming || recoveredFromMidTurnGap,
                    gentleRecoveryFade: recoveredFromMidTurnGap,
                    prependLeadIn: priming && !_streamEmittedAudio,
                    applyFadeOut: false,
                    holdbackTailBytes: TailHoldbackBytes,
                    trailingZeroBytes: 0))
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
                    gentleRecoveryFade: false,
                    prependLeadIn: _needsStreamPrime && !_streamEmittedAudio,
                    applyFadeOut: true,
                    holdbackTailBytes: 0,
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

    private bool WriteCoalescedLocked(
        bool applyFadeIn,
        bool gentleRecoveryFade,
        bool prependLeadIn,
        bool applyFadeOut,
        int holdbackTailBytes,
        int trailingZeroBytes)
    {
        int total = (int)_coalesce.Length;
        int evenTotal = total & ~1;
        int len = evenTotal - holdbackTailBytes;
        if (len < 2)
        {
            if (holdbackTailBytes == 0 && evenTotal >= 2)
                len = evenTotal;
            else
                return false;
        }

        byte[] bufAll = _coalesce.GetBuffer();
        int carryoverStart = len;
        int carryoverLen = total - carryoverStart;

        int leadBytes = prependLeadIn ? LeadInSilenceBytes : 0;
        int allocBytes = leadBytes + len + trailingZeroBytes;
        byte[] raw = ArrayPool<byte>.Shared.Rent(allocBytes);
        try
        {
            if (leadBytes > 0)
                raw.AsSpan(0, leadBytes).Clear();

            Buffer.BlockCopy(bufAll, 0, raw, leadBytes, len);
            if (carryoverLen > 0)
            {
                Buffer.BlockCopy(bufAll, carryoverStart, bufAll, 0, carryoverLen);
                _coalesce.SetLength(carryoverLen);
                _coalesce.Position = carryoverLen;
            }
            else
            {
                _coalesce.SetLength(0);
            }

            ReclaimCompletedLocked();

            var pcmSpan = raw.AsSpan(leadBytes, len);
            if (applyFadeIn)
                AudioPcmUtilities.ApplyFadeIn16(pcmSpan, fadeSamples: gentleRecoveryFade ? 36 : 96);
            if (applyFadeOut)
                AudioPcmUtilities.ApplyFadeOut16(pcmSpan, fadeSamples: 96);
            if (trailingZeroBytes > 0)
                raw.AsSpan(leadBytes + len, trailingZeroBytes).Clear();

            double vol = Volatile.Read(ref _volume);
            if (vol < 0.99)
                ApplyVolumeInPlace(raw, leadBytes, len, vol);

            int offset = 0;
            bool anySubmitted = false;
            while (offset < allocBytes)
            {
                int remaining = allocBytes - offset;
                int sliceLen = remaining <= MaxWaveHeaderBytes ? remaining : MaxWaveHeaderBytes;
                sliceLen &= ~1;
                if (sliceLen < 2) break;

                if (!SubmitWaveOutBufferSliceLocked(raw, offset, sliceLen))
                    break;
                anySubmitted = true;
                offset += sliceLen;
            }
            return anySubmitted && (_headlessVirtualMode || _hWaveOut != IntPtr.Zero);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private void SubmitSilencePaddingLocked(int silenceBytes)
    {
        byte[] zeros = ArrayPool<byte>.Shared.Rent(silenceBytes);
        try
        {
            zeros.AsSpan(0, silenceBytes).Clear();
            _ = SubmitWaveOutBufferSliceLocked(zeros, 0, silenceBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private bool SubmitWaveOutBufferSliceLocked(byte[] source, int sourceOffset, int byteCount)
    {
        if (byteCount < 2) return false;

        if (_headlessVirtualMode)
        {
            if (!_virtualOpened) return false;
            long vNow = Stopwatch.GetTimestamp();
            long vBase = _virtualPlaybackEndsTick > vNow ? _virtualPlaybackEndsTick : vNow;
            long vDur = (long)((byteCount / (double)BytesPerSecond24k16) * Stopwatch.Frequency);
            long vDone = vBase + vDur;
            _virtualPlaybackEndsTick = vDone;
            _inFlight.Enqueue(new ActiveBuffer { VirtualDoneTick = vDone });
            long prevEnd = Volatile.Read(ref _playbackEndsAtTick);
            long cBase = prevEnd > vNow ? prevEnd : vNow;
            Volatile.Write(ref _playbackEndsAtTick, cBase + vDur);
            _onSliceSubmitted?.Invoke(byteCount);
            return true;
        }

        if (_hWaveOut == IntPtr.Zero) return false;
        IntPtr dataPtr = IntPtr.Zero;
        IntPtr hdrPtr = IntPtr.Zero;

        try
        {
            dataPtr = Marshal.AllocHGlobal(byteCount);
            Marshal.Copy(source, sourceOffset, dataPtr, byteCount);

            uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
            var hdr = new WAVEHDR
            {
                lpData = dataPtr,
                dwBufferLength = (uint)byteCount
            };
            hdrPtr = Marshal.AllocHGlobal((int)hdrSize);
            Marshal.StructureToPtr(hdr, hdrPtr, false);

            if (waveOutPrepareHeader(_hWaveOut, hdrPtr, hdrSize) != 0)
            {
                Marshal.FreeHGlobal(hdrPtr);
                Marshal.FreeHGlobal(dataPtr);
                hdrPtr = IntPtr.Zero;
                dataPtr = IntPtr.Zero;
                CloseWaveOutLocked();
                return false;
            }

            if (waveOutWrite(_hWaveOut, hdrPtr, hdrSize) != 0)
            {
                int unprep = waveOutUnprepareHeader(_hWaveOut, hdrPtr, hdrSize);
                if (unprep == Win32AudioDevices.WavErrStillPlaying)
                {
                    _staleInFlight.Enqueue(new ActiveBuffer { HeaderPtr = hdrPtr, DataPtr = dataPtr });
                }
                else
                {
                    Marshal.FreeHGlobal(hdrPtr);
                    Marshal.FreeHGlobal(dataPtr);
                }
                hdrPtr = IntPtr.Zero;
                dataPtr = IntPtr.Zero;
                CloseWaveOutLocked();
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
            _onSliceSubmitted?.Invoke(byteCount);
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
            _virtualPlaybackEndsTick = 0;
            _needsStreamPrime = true;
            _streamEmittedAudio = false;
            if (_disposed) return;
            _coalesce.SetLength(0);
            if (_headlessVirtualMode)
            {
                _inFlight.Clear();
                return;
            }
            if (_hWaveOut == IntPtr.Zero) return;
            _ = waveOutReset(_hWaveOut);
            ReclaimAllLocked(maxSpinMs: 50, keepBusyInStaleQueue: true);
        }
    }

    internal void SimulateInFlightDrainForTest()
    {
        lock (_sync)
        {
            if (!_headlessVirtualMode) return;
            _inFlight.Clear();
            _virtualPlaybackEndsTick = Stopwatch.GetTimestamp();
        }
    }

    private void ReclaimCompletedLocked()
    {
        if (_headlessVirtualMode)
        {
            long now = Stopwatch.GetTimestamp();
            while (_inFlight.Count > 0 && _inFlight.Peek().VirtualDoneTick <= now)
                _inFlight.Dequeue();
            return;
        }

        if (_hWaveOut == IntPtr.Zero) return;
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        ReclaimQueueCompletedLocked(_staleInFlight, hdrSize, checkDoneFlag: false);
        ReclaimQueueCompletedLocked(_inFlight, hdrSize, checkDoneFlag: true);
    }

    private void ReclaimQueueCompletedLocked(Queue<ActiveBuffer> queue, uint hdrSize, bool checkDoneFlag)
    {
        while (queue.Count > 0)
        {
            var head = queue.Peek();
            if (checkDoneFlag)
            {
                var hdr = Marshal.PtrToStructure<WAVEHDR>(head.HeaderPtr);
                if ((hdr.dwFlags & Win32AudioDevices.WhdrDone) == 0)
                    break;
            }

            int mm = waveOutUnprepareHeader(_hWaveOut, head.HeaderPtr, hdrSize);
            if (mm == Win32AudioDevices.WavErrStillPlaying)
                break;

            queue.Dequeue();
            Marshal.FreeHGlobal(head.HeaderPtr);
            Marshal.FreeHGlobal(head.DataPtr);
        }
    }

    private bool ReclaimAllLocked(int maxSpinMs, bool keepBusyInStaleQueue)
    {
        if (_headlessVirtualMode)
        {
            _inFlight.Clear();
            return true;
        }

        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        long t0 = Stopwatch.GetTimestamp();
        bool allReclaimed = true;

        DrainQueueWithSpinLocked(_staleInFlight, hdrSize, t0, maxSpinMs, keepBusyInStaleQueue, ref allReclaimed);
        DrainQueueWithSpinLocked(_inFlight, hdrSize, t0, maxSpinMs, keepBusyInStaleQueue, ref allReclaimed);
        return allReclaimed;
    }

    private void DrainQueueWithSpinLocked(
        Queue<ActiveBuffer> queue,
        uint hdrSize,
        long t0,
        int maxSpinMs,
        bool keepBusyInStaleQueue,
        ref bool allReclaimed)
    {
        while (queue.Count > 0)
        {
            var buf = queue.Peek();
            int mm = waveOutUnprepareHeader(_hWaveOut, buf.HeaderPtr, hdrSize);
            if (mm == Win32AudioDevices.WavErrStillPlaying)
            {
                if (Stopwatch.GetElapsedTime(t0).TotalMilliseconds >= maxSpinMs)
                {
                    allReclaimed = false;
                    if (keepBusyInStaleQueue)
                    {
                        if (!ReferenceEquals(queue, _staleInFlight))
                            _staleInFlight.Enqueue(queue.Dequeue());
                        else
                            break;
                    }
                    else
                    {
                        break;
                    }
                    continue;
                }
                Thread.Sleep(4);
                continue;
            }

            queue.Dequeue();
            Marshal.FreeHGlobal(buf.HeaderPtr);
            Marshal.FreeHGlobal(buf.DataPtr);
        }
    }

    private static void ApplyVolumeInPlace(byte[] pcm16, int startOffset, int len, double volume)
    {
        int sampleCount = len / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            int idx = startOffset + i * 2;
            short sample = (short)(pcm16[idx] | (pcm16[idx + 1] << 8));
            int scaled = Math.Clamp((int)Math.Round(sample * volume), short.MinValue, short.MaxValue);
            pcm16[idx] = (byte)(scaled & 0xFF);
            pcm16[idx + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    private void CloseWaveOutLocked()
    {
        Volatile.Write(ref _playbackEndsAtTick, 0);
        _virtualPlaybackEndsTick = 0;
        _virtualOpened = false;
        _needsStreamPrime = true;
        _streamEmittedAudio = false;
        if (!_disposed)
            _coalesce.SetLength(0);
        if (_hWaveOut != IntPtr.Zero)
        {
            _ = waveOutReset(_hWaveOut);
            bool clean = ReclaimAllLocked(maxSpinMs: 140, keepBusyInStaleQueue: false);
            int closeRes = waveOutClose(_hWaveOut);
            if (clean && closeRes == 0)
            {
                _hWaveOut = IntPtr.Zero;
            }
            else if (closeRes == 0)
            {
                while (_staleInFlight.Count > 0)
                {
                    var leftover = _staleInFlight.Dequeue();
                    Marshal.FreeHGlobal(leftover.HeaderPtr);
                    Marshal.FreeHGlobal(leftover.DataPtr);
                }
                while (_inFlight.Count > 0)
                {
                    var leftover = _inFlight.Dequeue();
                    Marshal.FreeHGlobal(leftover.HeaderPtr);
                    Marshal.FreeHGlobal(leftover.DataPtr);
                }
                _hWaveOut = IntPtr.Zero;
            }
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
