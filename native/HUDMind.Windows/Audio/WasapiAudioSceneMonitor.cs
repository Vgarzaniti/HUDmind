using System.Diagnostics;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// 100% passive, zero-NuGet Windows Core Audio (WASAPI COM) peak meter observing non-HUDMind
/// playback sessions (games, Discord, videos) strictly read-only via Windows Audio Mixer metadata.
/// Never hooks, reads game memory, or modifies third-party audio volumes.
/// </summary>
public sealed class WasapiAudioSceneMonitor : IAudioSceneMonitor
{
    private const int eRender = 0;
    private const int eMultimedia = 1;
    private const uint CLSCTX_ALL = 0x17;
    private const int AudioSessionStateActive = 1;
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    private readonly object _stateLock = new();
    private readonly AutoResetEvent _wakeSignal = new(false);
    private readonly ManualResetEventSlim _firstSampleEvent = new(false);
    private readonly int _selfPid = Environment.ProcessId;
    private Thread? _mtaWorker;
    private long _lastSampleTick;
    private long _lastDemandTick;
    private volatile AudioSceneSnapshot _cached = AudioSceneSnapshot.Quiet;
    private volatile bool _disposed;

    private static readonly long MinCacheTicks = (long)(0.045 * Stopwatch.Frequency);
    private static readonly long ActiveWindowTicks = (long)(1.5 * Stopwatch.Frequency);

    public AudioSceneSnapshot SampleOtherAudio()
    {
        if (_disposed)
            return AudioSceneSnapshot.Quiet;

        long nowTick = Stopwatch.GetTimestamp();
        long lastTick = Volatile.Read(ref _lastSampleTick);
        if (lastTick > 0 && (nowTick - lastTick) < MinCacheTicks)
            return _cached;

        EnsureWorkerStarted();
        Volatile.Write(ref _lastDemandTick, nowTick);
        try { _wakeSignal.Set(); } catch (ObjectDisposedException) { }

        // Keep WPF UI STA threads strictly free of COM activation/RPC stalls; non-STA callers may wait once for initial MTA read.
        if (lastTick == 0 && !_disposed && Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            try { _firstSampleEvent.Wait(120); } catch (ObjectDisposedException) { }
        }
        return _disposed ? AudioSceneSnapshot.Quiet : _cached;
    }

    private void EnsureWorkerStarted()
    {
        if (_mtaWorker is not null || _disposed) return;
        lock (_stateLock)
        {
            if (_mtaWorker is not null || _disposed) return;
            var worker = new Thread(MtaWorkerLoop)
            {
                IsBackground = true,
                Name = "HUDMind-WasapiSceneMonitor"
            };
            worker.SetApartmentState(ApartmentState.MTA);
            _mtaWorker = worker;
            worker.Start();
        }
    }

    private void MtaWorkerLoop()
    {
        try
        {
            while (!_disposed)
            {
                var snap = ReadPeakSnapshotSafely();
                _cached = snap;
                Volatile.Write(ref _lastSampleTick, Stopwatch.GetTimestamp());
                try { _firstSampleEvent.Set(); } catch (ObjectDisposedException) { }

                if (_disposed) break;

                long demandAge = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastDemandTick);
                int waitMs = demandAge < ActiveWindowTicks ? 45 : Timeout.Infinite;
                _wakeSignal.WaitOne(waitMs);
            }
        }
        catch (ObjectDisposedException) { }
    }

    private AudioSceneSnapshot ReadPeakSnapshotSafely()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? managerObj = null;
        IAudioSessionEnumerator? sessions = null;

        try
        {
            var enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator, throwOnError: false);
            if (enumType is null)
                return AudioSceneSnapshot.Quiet;

            enumerator = Activator.CreateInstance(enumType) as IMMDeviceEnumerator;
            if (enumerator is null)
                return AudioSceneSnapshot.Quiet;

            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out device) != 0 || device is null)
                return AudioSceneSnapshot.Quiet;

            var iidMgr = IID_IAudioSessionManager2;
            if (device.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out managerObj) != 0 ||
                managerObj is not IAudioSessionManager2 mgr)
            {
                return AudioSceneSnapshot.Quiet;
            }

            if (mgr.GetSessionEnumerator(out sessions) != 0 || sessions is null)
                return AudioSceneSnapshot.Quiet;

            if (sessions.GetCount(out int count) != 0 || count <= 0)
                return new AudioSceneSnapshot(0.0, false, DateTimeOffset.UtcNow);

            double maxOtherPeak = 0.0;
            int limit = Math.Min(count, 48);
            for (int i = 0; i < limit; i++)
            {
                IAudioSessionControl? ctrl = null;
                try
                {
                    if (sessions.GetSession(i, out ctrl) != 0 || ctrl is null)
                        continue;
                    if (ctrl.GetState(out int state) != 0 || state != AudioSessionStateActive)
                        continue;

                    if (ctrl is IAudioSessionControl2 ctrl2 &&
                        ctrl2.GetProcessId(out uint pid) == 0 &&
                        pid == (uint)_selfPid)
                    {
                        continue; // Exclude HUDMind itself
                    }

                    if (ctrl is IAudioMeterInformation meter &&
                        meter.GetPeakValue(out float peak) == 0 &&
                        peak > maxOtherPeak &&
                        !float.IsNaN(peak))
                    {
                        maxOtherPeak = Math.Clamp(peak, 0.0, 1.0);
                    }
                }
                finally
                {
                    ReleaseCom(ctrl);
                }
            }

            return new AudioSceneSnapshot(
                maxOtherPeak,
                maxOtherPeak >= 0.42,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            // Headless CI runners or disabled Windows Audio service degrade cleanly without noise
            return AudioSceneSnapshot.Quiet;
        }
        finally
        {
            ReleaseCom(sessions);
            ReleaseCom(managerObj);
            ReleaseCom(device);
            ReleaseCom(enumerator);
        }
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            try { Marshal.ReleaseComObject(obj); } catch { }
        }
    }

    public void Dispose()
    {
        Thread? worker;
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            worker = _mtaWorker;
        }

        try { _wakeSignal.Set(); } catch (ObjectDisposedException) { }
        bool joined = worker?.Join(TimeSpan.FromMilliseconds(1500)) ?? true;
        if (joined)
        {
            try { _wakeSignal.Dispose(); } catch { }
            try { _firstSampleEvent.Dispose(); } catch { }
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, [MarshalAs(UnmanagedType.IUnknown)] out object ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, [MarshalAs(UnmanagedType.IUnknown)] out object ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid AudioSessionGuid, uint StreamFlags, [MarshalAs(UnmanagedType.IUnknown)] out object SessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid AudioSessionGuid, uint StreamFlags, [MarshalAs(UnmanagedType.IUnknown)] out object AudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr SessionNotification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr SessionNotification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionCount, out IAudioSessionControl Session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
        [PreserveSig] int GetMeteringChannelCount(out uint pnChannels);
        [PreserveSig] int GetChannelsPeakValues(uint u32ChannelCount, [Out, MarshalAs(UnmanagedType.LPArray)] float[] afPeakValues);
        [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    }
}
