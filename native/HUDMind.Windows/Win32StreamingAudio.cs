using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Zero-NuGet Win32 `winmm.dll` audio endpoint enumeration (`WAVE_MAPPER` fallback safe).
/// </summary>
public static class Win32AudioDevices
{
    public const int WaveMapper = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct WAVEOUTCAPSW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct WAVEINCAPSW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int waveOutGetDevCapsW(UIntPtr uDeviceID, out WAVEOUTCAPSW pwoc, uint cbwoc);

    [DllImport("winmm.dll")]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int waveInGetDevCapsW(UIntPtr uDeviceID, out WAVEINCAPSW pwic, uint cbwic);

    private static List<(int Id, string Name)> GetInputDevices()
    {
        uint count = waveInGetNumDevs();
        var list = new List<(int Id, string Name)>((int)Math.Min(count, 32));
        uint size = (uint)Marshal.SizeOf<WAVEINCAPSW>();
        for (uint i = 0; i < count && i < 32; i++)
        {
            if (waveInGetDevCapsW((UIntPtr)i, out var caps, size) == 0 && !string.IsNullOrWhiteSpace(caps.szPname))
                list.Add(((int)i, caps.szPname.Trim()));
        }
        return list;
    }

    private static List<(int Id, string Name)> GetOutputDevices()
    {
        uint count = waveOutGetNumDevs();
        var list = new List<(int Id, string Name)>((int)Math.Min(count, 32));
        uint size = (uint)Marshal.SizeOf<WAVEOUTCAPSW>();
        for (uint i = 0; i < count && i < 32; i++)
        {
            if (waveOutGetDevCapsW((UIntPtr)i, out var caps, size) == 0 && !string.IsNullOrWhiteSpace(caps.szPname))
                list.Add(((int)i, caps.szPname.Trim()));
        }
        return list;
    }

    public static string[] GetInputDeviceNames() => [.. GetInputDevices().Select(d => d.Name)];

    public static string[] GetOutputDeviceNames() => [.. GetOutputDevices().Select(d => d.Name)];

    public static int ResolveInputDeviceId(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return WaveMapper;
        var target = deviceName.Trim();
        foreach (var (id, name) in GetInputDevices())
        {
            if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return WaveMapper;
    }

    public static int ResolveOutputDeviceId(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return WaveMapper;
        var target = deviceName.Trim();
        foreach (var (id, name) in GetOutputDevices())
        {
            if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return WaveMapper;
    }
}

/// <summary>
/// Zero-NuGet Win32 `winmm.dll` streaming 24kHz 16-bit mono PCM player for Live Native Audio.
/// Preserves odd-byte PCM alignment across chunks, safely verifies waveOutUnprepareHeader
/// to prevent Use-After-Free on DMA drivers, and uses lock-free monotonic timing for Acoustic Echo Suppression.
/// </summary>
public sealed class Win32StreamingPcmPlayer : IStreamingPcmPlayer
{
    private const int WaveMapper = -1;
    private const uint WhdrDone = 0x00000001;
    private const int WavErrStillPlaying = 33;
    private const int BytesPerSecond24k16 = 48000;
    private const int MinCoalesceBytes = 1920; // 40ms smooth audio write granularity

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
    private readonly MemoryStream _coalesce = new(4096);
    private IntPtr _hWaveOut;
    private string _targetDeviceName = string.Empty;
    private double _volume = 0.85;
    private long _playbackEndsAtTick;
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
            wFormatTag = 1, // WAVE_FORMAT_PCM
            nChannels = 1,
            nSamplesPerSec = 24000,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = BytesPerSecond24k16,
            cbSize = 0
        };
        int devId = Win32AudioDevices.ResolveOutputDeviceId(_targetDeviceName);
        int res = waveOutOpen(out _hWaveOut, devId, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (res != 0 && devId != WaveMapper)
            res = waveOutOpen(out _hWaveOut, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        return res == 0 && _hWaveOut != IntPtr.Zero;
    }

    public void EnqueuePcm24k(byte[] pcmBytes)
    {
        if (pcmBytes.Length == 0) return;
        lock (_sync)
        {
            if (_disposed || !EnsureOpenLocked())
                return;

            _coalesce.Write(pcmBytes, 0, pcmBytes.Length);
            if (_coalesce.Length >= MinCoalesceBytes)
                WriteCoalescedLocked();
        }
    }

    public void FlushBuffered()
    {
        lock (_sync)
        {
            if (_disposed) return;
            ReclaimCompletedLocked();
            if (_coalesce.Length >= 2 && EnsureOpenLocked())
                WriteCoalescedLocked();
            // Drop any trailing orphan odd byte at end-of-turn so next utterance starts clean
            if (_coalesce.Length == 1)
                _coalesce.SetLength(0);
        }
    }

    private void WriteCoalescedLocked()
    {
        int total = (int)_coalesce.Length;
        int len = total & ~1; // Enforce 16-bit sample alignment while preserving trailing odd byte!
        if (len < 2) return;

        byte[] bufAll = _coalesce.GetBuffer();
        byte[] raw = ArrayPool<byte>.Shared.Rent(len);
        IntPtr dataPtr = IntPtr.Zero;
        IntPtr hdrPtr = IntPtr.Zero;
        try
        {
            Buffer.BlockCopy(bufAll, 0, raw, 0, len);
            if (total > len)
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

            double vol = Volatile.Read(ref _volume);
            dataPtr = Marshal.AllocHGlobal(len);
            if (vol < 0.99)
                ApplyVolumeInPlace(raw, len, vol);
            Marshal.Copy(raw, 0, dataPtr, len);

            uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
            var hdr = new WAVEHDR
            {
                lpData = dataPtr,
                dwBufferLength = (uint)len
            };
            hdrPtr = Marshal.AllocHGlobal((int)hdrSize);
            Marshal.StructureToPtr(hdr, hdrPtr, false);

            if (waveOutPrepareHeader(_hWaveOut, hdrPtr, hdrSize) != 0)
            {
                Marshal.FreeHGlobal(hdrPtr);
                Marshal.FreeHGlobal(dataPtr);
                hdrPtr = IntPtr.Zero;
                dataPtr = IntPtr.Zero;
                return;
            }

            if (waveOutWrite(_hWaveOut, hdrPtr, hdrSize) != 0)
            {
                _ = waveOutUnprepareHeader(_hWaveOut, hdrPtr, hdrSize);
                Marshal.FreeHGlobal(hdrPtr);
                Marshal.FreeHGlobal(dataPtr);
                hdrPtr = IntPtr.Zero;
                dataPtr = IntPtr.Zero;
                // Audio endpoint probably disconnected/switched; reset handle so next chunk reopens active mapper!
                _ = waveOutReset(_hWaveOut);
                ReclaimAllLocked(maxSpinMs: 40);
                _ = waveOutClose(_hWaveOut);
                _hWaveOut = IntPtr.Zero;
                return;
            }

            _inFlight.Enqueue(new ActiveBuffer { HeaderPtr = hdrPtr, DataPtr = dataPtr });
            hdrPtr = IntPtr.Zero;
            dataPtr = IntPtr.Zero;

            long nowTick = Stopwatch.GetTimestamp();
            long curEnd = Volatile.Read(ref _playbackEndsAtTick);
            long baseTick = curEnd > nowTick ? curEnd : nowTick;
            long durTicks = (long)((len / (double)BytesPerSecond24k16) * Stopwatch.Frequency);
            Volatile.Write(ref _playbackEndsAtTick, baseTick + durTicks);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            if (hdrPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdrPtr);
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
        }
    }

    public void InterruptAndClear()
    {
        lock (_sync)
        {
            Volatile.Write(ref _playbackEndsAtTick, 0);
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
            if ((hdr.dwFlags & WhdrDone) == 0)
                break;

            int mm = waveOutUnprepareHeader(_hWaveOut, head.HeaderPtr, hdrSize);
            if (mm == WavErrStillPlaying)
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
            if (mm == WavErrStillPlaying)
            {
                if (Stopwatch.GetElapsedTime(t0).TotalMilliseconds >= maxSpinMs)
                {
                    // Drop queue tracking rather than risking native Use-After-Free on an active DMA buffer
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

/// <summary>
/// Zero-NuGet Win32 `winmm.dll` 16kHz 16-bit mono PCM microphone capture for live two-way conversation.
/// Uses chronological ring indexing, lock-guarded native pointer reads, MMRESULT validation, and clean UAF-free shutdown.
/// </summary>
public sealed class Win32StreamingMicCapture : IStreamingMicCapture
{
    private const int WaveMapper = -1;
    private const uint WhdrDone = 0x00000001;
    private const int WavErrStillPlaying = 33;
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

                // Ensure any stale driver handle/buffers from a prior error exit are freed before reopening!
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
                if ((openRes != 0 || _hWaveIn == IntPtr.Zero) && devId != WaveMapper)
                    openRes = waveInOpen(out _hWaveIn, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);

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
                        if ((hdr.dwFlags & WhdrDone) == 0)
                            continue;

                        any = true;
                        int recorded = (int)Math.Min(hdr.dwBytesRecorded, (uint)ChunkBytes);
                        if (recorded > 0)
                        {
                            copy = new byte[recorded];
                            Marshal.Copy(hdr.lpData, copy, 0, recorded);
                        }

                        hdr.dwFlags &= ~WhdrDone;
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
            int prevGen;
            lock (_sync)
            {
                prevGen = _captureGen;
                Interlocked.Increment(ref _captureGen);
                _running = false;
                if (_hWaveIn != IntPtr.Zero)
                {
                    // Interrupt driver waits immediately so PumpLoop exits cleanly!
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
            _ = prevGen;
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
                    if (mm == WavErrStillPlaying)
                    {
                        Thread.Sleep(15);
                        mm = waveInUnprepareHeader(_hWaveIn, _hdrPtrs[i], hdrSize);
                    }
                    stillPlaying = mm == WavErrStillPlaying;
                    if (!stillPlaying)
                        Marshal.FreeHGlobal(_hdrPtrs[i]);
                    _hdrPtrs[i] = IntPtr.Zero;
                }
                if (_dataPtrs[i] != IntPtr.Zero)
                {
                    // Avoid Use-After-Free on DMA data buffer if waveInUnprepareHeader returned STILLPLAYING!
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
