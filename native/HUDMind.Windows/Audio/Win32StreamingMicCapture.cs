using System.Runtime.InteropServices;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Zero-NuGet Win32 `winmm.dll` 16kHz 16-bit mono PCM microphone capture for live voice input.
/// Uses strict chronological ring-buffer scanning, serialized device switching, generation fencing,
/// and DMA-safe buffer cleanup without native UAF risks.
/// </summary>
public sealed class Win32StreamingMicCapture : IStreamingMicCapture
{
    private const int NumBuffers = 4;
    private const int ChunkBytes = 3200; // 100ms at 16kHz 16-bit mono

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

    private readonly object _opLock = new();
    private readonly object _sync = new();
    private IntPtr _hWaveIn;
    private readonly IntPtr[] _hdrPtrs = new IntPtr[NumBuffers];
    private readonly IntPtr[] _dataPtrs = new IntPtr[NumBuffers];
    private int _headIdx;
    private int _captureGen;
    private string _targetDeviceName = string.Empty;
    private Thread? _pumpThread;
    private Action<byte[]>? _lastOnChunk;
    private Action<string>? _onError;
    private volatile bool _running;
    private bool _disposed;

    public bool IsCapturing => _running;

    public void SetDevice(string? deviceName)
    {
        var next = (deviceName ?? string.Empty).Trim();
        lock (_opLock)
        {
            if (_disposed) return;

            Action<byte[]>? restartCb = null;
            Action<string>? restartErr = null;
            bool wasRunning = false;

            lock (_sync)
            {
                if (_disposed || string.Equals(_targetDeviceName, next, StringComparison.Ordinal))
                    return;
                _targetDeviceName = next;
                if (_running && _lastOnChunk is not null)
                {
                    wasRunning = true;
                    restartCb = _lastOnChunk;
                    restartErr = _onError;
                }
            }

            if (wasRunning && restartCb is not null)
            {
                Stop();
                if (!_disposed)
                    TryStart(restartCb, restartErr);
            }
        }
    }

    public bool TryStart(Action<byte[]> onPcm16kChunk, Action<string>? onError = null)
    {
        lock (_opLock)
        {
            int myGen;
            lock (_sync)
            {
                if (_disposed) return false;
                if (_running) return true;

                CleanupLocked();

                _lastOnChunk = onPcm16kChunk;
                _onError = onError;
                _headIdx = 0;

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

                int devId = Win32AudioDevices.ResolveInputDeviceId(_targetDeviceName);
                int openRes = waveInOpen(out _hWaveIn, devId, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
                if ((openRes != 0 || _hWaveIn == IntPtr.Zero) && devId != Win32AudioDevices.WaveMapper)
                    openRes = waveInOpen(out _hWaveIn, Win32AudioDevices.WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);

                if (openRes != 0 || _hWaveIn == IntPtr.Zero)
                {
                    _hWaveIn = IntPtr.Zero;
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
                    if (waveInPrepareHeader(_hWaveIn, _hdrPtrs[i], hdrSize) != 0 ||
                        waveInAddBuffer(_hWaveIn, _hdrPtrs[i], hdrSize) != 0)
                    {
                        CleanupLocked();
                        onError?.Invoke("No se pudo preparar buffer de captura de micrófono.");
                        return false;
                    }
                }

                if (waveInStart(_hWaveIn) != 0)
                {
                    CleanupLocked();
                    onError?.Invoke("No se pudo iniciar captura de micrófono.");
                    return false;
                }

                myGen = Interlocked.Increment(ref _captureGen);
                _running = true;
                _pumpThread = new Thread(() => PumpLoop(onPcm16kChunk, myGen))
                {
                    IsBackground = true,
                    Name = "HUDMind-Mic-16k"
                };
                _pumpThread.Start();
                return true;
            }
        }
    }

    private void PumpLoop(Action<byte[]> onChunk, int myGen)
    {
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        try
        {
            while (_running && Volatile.Read(ref _captureGen) == myGen)
            {
                bool any = false;
                int idx;
                lock (_sync)
                    idx = _headIdx;

                for (int k = 0; k < NumBuffers && _running && Volatile.Read(ref _captureGen) == myGen; k++, idx = (idx + 1) % NumBuffers)
                {
                    byte[]? copy = null;
                    string? errMsg = null;

                    lock (_sync)
                    {
                        if (!_running || Volatile.Read(ref _captureGen) != myGen || _hWaveIn == IntPtr.Zero)
                            break;

                        IntPtr hp = _hdrPtrs[idx];
                        if (hp == IntPtr.Zero) continue;

                        var hdr = Marshal.PtrToStructure<WAVEHDR>(hp);
                        if ((hdr.dwFlags & Win32AudioDevices.WhdrDone) == 0)
                            continue;

                        any = true;
                        int recorded = (int)Math.Min(hdr.dwBytesRecorded, (uint)ChunkBytes);
                        if (recorded > 0)
                        {
                            copy = new byte[recorded];
                            Marshal.Copy(hdr.lpData, copy, 0, recorded);
                        }

                        hdr.dwFlags &= ~Win32AudioDevices.WhdrDone;
                        hdr.dwBytesRecorded = 0;
                        Marshal.StructureToPtr(hdr, hp, false);
                        int mm = waveInAddBuffer(_hWaveIn, hp, hdrSize);
                        if (mm != 0)
                        {
                            _running = false;
                            errMsg = $"Micrófono desconectado o interrumpido (WinMM {mm}).";
                        }
                        else
                        {
                            _headIdx = (idx + 1) % NumBuffers;
                        }
                    }

                    if (errMsg is not null)
                    {
                        try { _onError?.Invoke(errMsg); } catch { }
                        break;
                    }

                    if (copy is not null)
                    {
                        try { onChunk(copy); } catch { /* resilient callback */ }
                    }
                }

                if (!any && _running && Volatile.Read(ref _captureGen) == myGen)
                    Thread.Sleep(20);
            }
        }
        finally
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _captureGen) == myGen)
                {
                    _running = false;
                    CleanupLocked();
                }
            }
        }
    }

    public void Stop()
    {
        lock (_opLock)
        {
            Thread? toJoin;
            lock (_sync)
            {
                Interlocked.Increment(ref _captureGen);
                _running = false;
                if (_hWaveIn != IntPtr.Zero)
                {
                    _ = waveInStop(_hWaveIn);
                    _ = waveInReset(_hWaveIn);
                }
                toJoin = _pumpThread;
                _pumpThread = null;
            }

            if (toJoin is { IsAlive: true })
                toJoin.Join(TimeSpan.FromSeconds(2));

            lock (_sync)
            {
                if (toJoin is not { IsAlive: true })
                    CleanupLocked();
            }
        }
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
                bool stillPlaying = false;
                if (_hdrPtrs[i] != IntPtr.Zero)
                {
                    int mm = waveInUnprepareHeader(_hWaveIn, _hdrPtrs[i], hdrSize);
                    if (mm == Win32AudioDevices.WavErrStillPlaying)
                    {
                        Thread.Sleep(15);
                        mm = waveInUnprepareHeader(_hWaveIn, _hdrPtrs[i], hdrSize);
                    }
                    stillPlaying = mm == Win32AudioDevices.WavErrStillPlaying;
                    if (!stillPlaying)
                        Marshal.FreeHGlobal(_hdrPtrs[i]);
                    _hdrPtrs[i] = IntPtr.Zero;
                }
                if (_dataPtrs[i] != IntPtr.Zero)
                {
                    if (!stillPlaying)
                        Marshal.FreeHGlobal(_dataPtrs[i]);
                    _dataPtrs[i] = IntPtr.Zero;
                }
            }
            _ = waveInClose(_hWaveIn);
            _hWaveIn = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        lock (_opLock)
        {
            lock (_sync) _disposed = true;
            Stop();
        }
    }
}
