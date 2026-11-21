using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

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

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

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
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public string? GetForegroundExecutable()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return null;

        var hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(512);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(hProc, 0, sb, ref size))
                    return Path.GetFileName(sb.ToString());
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
