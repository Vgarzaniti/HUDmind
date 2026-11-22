using System.Runtime.InteropServices;

namespace HUDMind.Windows;

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEX
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
internal struct WAVEHDR
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

/// <summary>
/// Zero-NuGet Win32 `winmm.dll` audio endpoint enumeration (`WAVE_MAPPER` fallback safe, preserving true WinMM IDs).
/// </summary>
public static class Win32AudioDevices
{
    public const int WaveMapper = -1;
    internal const uint WhdrDone = 0x00000001;
    internal const int WavErrStillPlaying = 33;

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

    public static int ResolveInputDeviceId(string? deviceName) =>
        ResolveByName(GetInputDevices(), deviceName);

    public static int ResolveOutputDeviceId(string? deviceName) =>
        ResolveByName(GetOutputDevices(), deviceName);

    private static int ResolveByName(List<(int Id, string Name)> devices, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return WaveMapper;
        var target = deviceName.Trim();
        foreach (var (id, name) in devices)
        {
            if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return WaveMapper;
    }
}
