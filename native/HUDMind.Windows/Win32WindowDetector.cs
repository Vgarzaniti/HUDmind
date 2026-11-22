using System.Diagnostics;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Anti-cheat friendly foreground process detector: caches (hwnd, pid) -&gt; executableName under a lock
/// so OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) runs once per focus switch, and remembers the last
/// external game client rect so typing in HUDMind chat captures the game window instead of HUDMind itself.
/// </summary>
public sealed class Win32WindowDetector : IForegroundWindowDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly int SelfPid = Environment.ProcessId;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private readonly object _lock = new();
    private IntPtr _cachedHwnd;
    private uint _cachedPid;
    private string? _cachedExe;
    private IntPtr _lastExternalGameHwnd;
    private (int Left, int Top, int Width, int Height)? _lastExternalRect;

    public unsafe string? GetForegroundExecutable()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return null;

        lock (_lock)
        {
            if (hwnd == _cachedHwnd && pid == _cachedPid)
                return _cachedExe;
        }

        string? resolved = ResolveExecutableName(pid);

        lock (_lock)
        {
            _cachedHwnd = hwnd;
            _cachedPid = pid;
            _cachedExe = resolved;
        }
        return resolved;
    }

    public (int Left, int Top, int Width, int Height)? GetForegroundClientRect()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return GetLastExternalRectLocked();

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)SelfPid)
        {
            // When user clicks HUDMind's chat box, capture the external game they were playing (not HUDMind!)
            IntPtr extHwnd;
            lock (_lock) extHwnd = _lastExternalGameHwnd;
            if (extHwnd != IntPtr.Zero && TryGetWindowClientRect(extHwnd, out var extRect))
            {
                lock (_lock) _lastExternalRect = extRect;
                return extRect;
            }
            return GetLastExternalRectLocked();
        }

        if (!TryGetWindowClientRect(hwnd, out var rect))
            return null;

        lock (_lock)
        {
            _lastExternalGameHwnd = hwnd;
            _lastExternalRect = rect;
        }
        return rect;
    }

    private (int Left, int Top, int Width, int Height)? GetLastExternalRectLocked()
    {
        lock (_lock) return _lastExternalRect;
    }

    private static bool TryGetWindowClientRect(IntPtr hwnd, out (int Left, int Top, int Width, int Height) result)
    {
        result = default;
        if (!GetClientRect(hwnd, out var rect))
            return false;

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w < 320 || h < 240)
            return false;

        var pt = new POINT { X = rect.Left, Y = rect.Top };
        if (!ClientToScreen(hwnd, ref pt))
            return false;

        result = (pt.X, pt.Y, w, h);
        return true;
    }

    private static unsafe string? ResolveExecutableName(uint pid)
    {
        var hProc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProc != IntPtr.Zero)
        {
            try
            {
                char* buf = stackalloc char[512];
                uint size = 512;
                if (QueryFullProcessImageName(hProc, 0, buf, ref size) && size > 0)
                    return Path.GetFileName(new ReadOnlySpan<char>(buf, (int)size)).ToString();
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return $"{proc.ProcessName}.exe";
        }
        catch
        {
            return null;
        }
    }
}

public readonly record struct MonitorBoundsRect(int Left, int Top, int Width, int Height);

/// <summary>
/// Pure Win32 physical monitor enumerator so WPF UI needs zero WinForms dependency
/// and works cleanly with PE-manifest PerMonitorV2 DPI awareness.
/// </summary>
public static class Win32DisplayHelper
{
    private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public static MonitorBoundsRect[] GetAllWorkingAreas()
    {
        var list = new List<MonitorBoundsRect>(4);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                list.Add(new MonitorBoundsRect(
                    mi.rcWork.Left,
                    mi.rcWork.Top,
                    mi.rcWork.Right - mi.rcWork.Left,
                    mi.rcWork.Bottom - mi.rcWork.Top));
            }
            return true;
        }, IntPtr.Zero);
        return list.Count > 0 ? list.ToArray() : [new MonitorBoundsRect(0, 0, 1920, 1080)];
    }
}
