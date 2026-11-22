using System.Diagnostics;
using System.Runtime.InteropServices;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Anti-cheat friendly foreground process detector: caches (hwnd, pid) -&gt; executableName so
/// OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) is only called once per window focus switch.
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

    private IntPtr _cachedHwnd;
    private uint _cachedPid;
    private string? _cachedExe;

    public unsafe string? GetForegroundExecutable()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return null;

        if (hwnd == _cachedHwnd && pid == _cachedPid && _cachedExe is not null)
            return _cachedExe;

        string? resolved = null;
        var hProc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProc != IntPtr.Zero)
        {
            try
            {
                char* buf = stackalloc char[512];
                uint size = 512;
                if (QueryFullProcessImageName(hProc, 0, buf, ref size) && size > 0)
                    resolved = Path.GetFileName(new ReadOnlySpan<char>(buf, (int)size)).ToString();
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        if (resolved is null)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                resolved = $"{proc.ProcessName}.exe";
            }
            catch
            {
                return null;
            }
        }

        _cachedHwnd = hwnd;
        _cachedPid = pid;
        _cachedExe = resolved;
        return resolved;
    }

    public (int Left, int Top, int Width, int Height)? GetForegroundClientRect()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var rect))
            return null;

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w < 320 || h < 240)
            return null;

        var pt = new POINT { X = rect.Left, Y = rect.Top };
        if (!ClientToScreen(hwnd, ref pt))
            return null;

        return (pt.X, pt.Y, w, h);
    }
}
