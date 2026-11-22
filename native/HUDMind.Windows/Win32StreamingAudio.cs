using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Zero-NuGet Win32 `winmm.dll` streaming 24kHz 16-bit mono PCM player for Gemini Live Native-Audio.
/// Plays audio chunks immediately as they arrive over WebSocket (&lt;10ms latency, 0% GPU) and supports
/// instant barge-in interruption via waveOutReset().
/// </summary>
public sealed class Win32StreamingPcmPlayer : IStreamingPcmPlayer
{
    private const int WaveMapper = -1;
    private const uint WhdrDone = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

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
    private IntPtr _hWaveOut;
    private double _volume = 0.85;
    private bool _disposed;

    public void SetVolume(double volume)
    {
        lock (_sync)
            _volume = Math.Clamp(volume, 0.0, 1.0);
    }

    private bool EnsureOpenLocked()
    {
        if (_disposed) return false;
        if (_hWaveOut != IntPtr.Zero) return true;

        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, // WAVE_FORMAT_PCM
            nChannels = 1,
            nSamplesPerSec = 24000,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = 48000,
            cbSize = 0
        };
        int res = waveOutOpen(out _hWaveOut, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        return res == 0 && _hWaveOut != IntPtr.Zero;
    }

    public void EnqueuePcm24k(byte[] pcmBytes)
    {
        if (pcmBytes.Length < 2) return;
        lock (_sync)
        {
            if (_disposed || !EnsureOpenLocked())
                return;

            ReclaimCompletedLocked();

            byte[] scaled = ApplyVolume(pcmBytes, _volume);
            IntPtr dataPtr = Marshal.AllocHGlobal(scaled.Length);
            Marshal.Copy(scaled, 0, dataPtr, scaled.Length);

            var hdr = new WAVEHDR
            {
                lpData = dataPtr,
                dwBufferLength = (uint)scaled.Length
            };
            IntPtr hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            Marshal.StructureToPtr(hdr, hdrPtr, false);

            uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
            if (waveOutPrepareHeader(_hWaveOut, hdrPtr, hdrSize) != 0 ||
                waveOutWrite(_hWaveOut, hdrPtr, hdrSize) != 0)
            {
                Marshal.FreeHGlobal(hdrPtr);
                Marshal.FreeHGlobal(dataPtr);
                return;
            }

            _inFlight.Enqueue(new ActiveBuffer { HeaderPtr = hdrPtr, DataPtr = dataPtr });
        }
    }

    public void InterruptAndClear()
    {
        lock (_sync)
        {
            if (_hWaveOut == IntPtr.Zero) return;
            _ = waveOutReset(_hWaveOut);
            ReclaimAllLocked();
        }
    }

    private void ReclaimCompletedLocked()
    {
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        while (_inFlight.Count > 0)
        {
            var head = _inFlight.Peek();
            var hdr = Marshal.PtrToStructure<WAVEHDR>(head.HeaderPtr);
            if ((hdr.dwFlags & WhdrDone) == 0)
                break;

            _inFlight.Dequeue();
            _ = waveOutUnprepareHeader(_hWaveOut, head.HeaderPtr, hdrSize);
            Marshal.FreeHGlobal(head.HeaderPtr);
            Marshal.FreeHGlobal(head.DataPtr);
        }
    }

    private void ReclaimAllLocked()
    {
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        while (_inFlight.Count > 0)
        {
            var buf = _inFlight.Dequeue();
            _ = waveOutUnprepareHeader(_hWaveOut, buf.HeaderPtr, hdrSize);
            Marshal.FreeHGlobal(buf.HeaderPtr);
            Marshal.FreeHGlobal(buf.DataPtr);
        }
    }

    private static byte[] ApplyVolume(byte[] pcm16, double volume)
    {
        if (volume >= 0.99)
            return pcm16;
        var outBytes = new byte[pcm16.Length];
        int sampleCount = pcm16.Length / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            int scaled = Math.Clamp((int)(sample * volume), short.MinValue, short.MaxValue);
            outBytes[i * 2] = (byte)(scaled & 0xFF);
            outBytes[i * 2 + 1] = (byte)((scaled >> 8) & 0xFF);
        }
        return outBytes;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_hWaveOut != IntPtr.Zero)
            {
                _ = waveOutReset(_hWaveOut);
                ReclaimAllLocked();
                _ = waveOutClose(_hWaveOut);
                _hWaveOut = IntPtr.Zero;
            }
        }
    }
}

/// <summary>
/// Zero-NuGet Win32 `winmm.dll` 16kHz 16-bit mono PCM microphone capture for live two-way conversation.
/// Streams 100ms chunks (3,200 bytes) with a clean background polling loop and graceful no-mic fallback.
/// </summary>
public sealed class Win32StreamingMicCapture : IStreamingMicCapture
{
    private const int WaveMapper = -1;
    private const uint WhdrDone = 0x00000001;
    private const int NumBuffers = 4;
    private const int ChunkBytes = 3200; // 100ms at 16kHz 16-bit mono

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hwi);

    private readonly object _sync = new();
    private IntPtr _hWaveIn;
    private readonly IntPtr[] _hdrPtrs = new IntPtr[NumBuffers];
    private readonly IntPtr[] _dataPtrs = new IntPtr[NumBuffers];
    private Thread? _pumpThread;
    private volatile bool _running;

    public bool IsCapturing => _running;

    public bool TryStart(Action<byte[]> onPcm16kChunk, Action<string>? onError = null)
    {
        lock (_sync)
        {
            if (_running) return true;

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1,
                nChannels = 1,
                nSamplesPerSec = 16000,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = 32000,
                cbSize = 0
            };

            if (waveInOpen(out _hWaveIn, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0 || _hWaveIn == IntPtr.Zero)
            {
                onError?.Invoke("No se detectó micrófono disponible (continuando solo con visión + voz del coach).");
                return false;
            }

            uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
            for (int i = 0; i < NumBuffers; i++)
            {
                _dataPtrs[i] = Marshal.AllocHGlobal(ChunkBytes);
                var hdr = new WAVEHDR { lpData = _dataPtrs[i], dwBufferLength = ChunkBytes };
                _hdrPtrs[i] = Marshal.AllocHGlobal((int)hdrSize);
                Marshal.StructureToPtr(hdr, _hdrPtrs[i], false);
                _ = waveInPrepareHeader(_hWaveIn, _hdrPtrs[i], hdrSize);
                _ = waveInAddBuffer(_hWaveIn, _hdrPtrs[i], hdrSize);
            }

            if (waveInStart(_hWaveIn) != 0)
            {
                CleanupLocked();
                onError?.Invoke("No se pudo iniciar captura de micrófono.");
                return false;
            }

            _running = true;
            _pumpThread = new Thread(() => PumpLoop(onPcm16kChunk))
            {
                IsBackground = true,
                Name = "HUDMind-Mic-16k"
            };
            _pumpThread.Start();
            return true;
        }
    }

    private void PumpLoop(Action<byte[]> onChunk)
    {
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        while (_running)
        {
            bool any = false;
            for (int i = 0; i < NumBuffers && _running; i++)
            {
                IntPtr hp = _hdrPtrs[i];
                if (hp == IntPtr.Zero) continue;
                var hdr = Marshal.PtrToStructure<WAVEHDR>(hp);
                if ((hdr.dwFlags & WhdrDone) != 0)
                {
                    any = true;
                    int recorded = (int)Math.Min(hdr.dwBytesRecorded, (uint)ChunkBytes);
                    if (recorded > 0)
                    {
                        var copy = new byte[recorded];
                        Marshal.Copy(hdr.lpData, copy, 0, recorded);
                        try { onChunk(copy); } catch { /* resilient callback */ }
                    }

                    lock (_sync)
                    {
                        if (_running && _hWaveIn != IntPtr.Zero)
                        {
                            hdr.dwFlags &= ~WhdrDone;
                            hdr.dwBytesRecorded = 0;
                            Marshal.StructureToPtr(hdr, hp, false);
                            _ = waveInAddBuffer(_hWaveIn, hp, hdrSize);
                        }
                    }
                }
            }
            if (!any)
                Thread.Sleep(25);
        }
    }

    public void Stop()
    {
        Thread? toJoin;
        lock (_sync)
        {
            _running = false;
            toJoin = _pumpThread;
            _pumpThread = null;
        }
        toJoin?.Join(TimeSpan.FromMilliseconds(500));
        lock (_sync)
            CleanupLocked();
    }

    private void CleanupLocked()
    {
        if (_hWaveIn != IntPtr.Zero)
        {
            _ = waveInStop(_hWaveIn);
            _ = waveInReset(_hWaveIn);
            uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
            for (int i = 0; i < NumBuffers; i++)
            {
                if (_hdrPtrs[i] != IntPtr.Zero)
                {
                    _ = waveInUnprepareHeader(_hWaveIn, _hdrPtrs[i], hdrSize);
                    Marshal.FreeHGlobal(_hdrPtrs[i]);
                    _hdrPtrs[i] = IntPtr.Zero;
                }
                if (_dataPtrs[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_dataPtrs[i]);
                    _dataPtrs[i] = IntPtr.Zero;
                }
            }
            _ = waveInClose(_hWaveIn);
            _hWaveIn = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();
}
