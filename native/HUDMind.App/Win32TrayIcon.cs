using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HUDMind.App;

/// <summary>
/// Zero-NuGet, 0% GPU Win32 Notification Area (System Tray) icon using Shell_NotifyIconW
/// without pulling System.Windows.Forms (avoids WFO0003 DPI manifest conflicts).
/// </summary>
public sealed class Win32TrayIcon : IDisposable
{
    private const int WmTrayCallback = 0x0441;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmNull = 0x0000;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNoNotify = 0x0080;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private static readonly IntPtr IdiApplication = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private readonly Action _onRestore;
    private readonly Action _onTogglePause;
    private readonly Action _onExit;
    private readonly Func<bool> _isPaused;
    private readonly Func<bool> _isRunning;

    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _ownsIcon;
    private bool _added;
    private bool _disposed;
    private string _tooltip = "HUDMind Coach (0% GPU)";

    public Win32TrayIcon(
        Action onRestore,
        Action onTogglePause,
        Action onExit,
        Func<bool> isPaused,
        Func<bool> isRunning)
    {
        _onRestore = onRestore;
        _onTogglePause = onTogglePause;
        _onExit = onExit;
        _isPaused = isPaused;
        _isRunning = isRunning;
    }

    public void Attach(Window owner)
    {
        if (_disposed || _hwnd != IntPtr.Zero) return;
        var interop = new WindowInteropHelper(owner);
        var handle = interop.EnsureHandle();
        _hwnd = handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);
        LoadBestIcon();
    }

    public void Show(string? tooltip = null)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        if (!string.IsNullOrWhiteSpace(tooltip))
            _tooltip = TruncateTip(tooltip);

        var nid = BuildData(NifMessage | NifIcon | NifTip, _tooltip);
        if (_added)
        {
            Shell_NotifyIconW(NimModify, ref nid);
        }
        else
        {
            _added = Shell_NotifyIconW(NimAdd, ref nid);
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        _tooltip = TruncateTip(tooltip);
        if (!_added || _disposed || _hwnd == IntPtr.Zero) return;
        var nid = BuildData(NifTip, _tooltip);
        Shell_NotifyIconW(NimModify, ref nid);
    }

    public void Hide()
    {
        if (!_added || _hwnd == IntPtr.Zero) return;
        var nid = BuildData(0, string.Empty);
        Shell_NotifyIconW(NimDelete, ref nid);
        _added = false;
    }

    private NOTIFYICONDATAW BuildData(uint flags, string tip) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WmTrayCallback,
        hIcon = _hIcon,
        szTip = tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmTrayCallback || _disposed)
            return IntPtr.Zero;

        int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
        if (mouseMsg is WmLButtonUp or WmLButtonDblClk)
        {
            handled = true;
            _onRestore();
        }
        else if (mouseMsg == WmRButtonUp)
        {
            handled = true;
            ShowContextMenu();
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_hwnd == IntPtr.Zero || _disposed) return;
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(menu, MfString, (UIntPtr)1001, "⚡ Abrir HUDMind");
            if (_isRunning())
            {
                string pauseLabel = _isPaused() ? "▶ Reanudar Coach" : "⏸ Pausar Coach";
                AppendMenuW(menu, MfString, (UIntPtr)1002, pauseLabel);
            }
            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenuW(menu, MfString, (UIntPtr)1003, "✕ Salir completamente");

            SetForegroundWindow(_hwnd);
            GetCursorPos(out var pt);
            uint cmd = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (cmd == 1001) _onRestore();
            else if (cmd == 1002) _onTogglePause();
            else if (cmd == 1003) _onExit();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void LoadBestIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                if (ExtractIconExW(exePath, 0, out IntPtr large, out IntPtr small, 1) > 0)
                {
                    if (small != IntPtr.Zero)
                    {
                        if (large != IntPtr.Zero) DestroyIcon(large);
                        _hIcon = small;
                        _ownsIcon = true;
                        return;
                    }
                    if (large != IntPtr.Zero)
                    {
                        _hIcon = large;
                        _ownsIcon = true;
                        return;
                    }
                }
            }

            var icoAsset = Path.Combine(AppContext.BaseDirectory, "Assets", "hudmind.ico");
            if (File.Exists(icoAsset))
            {
                IntPtr h = LoadImageW(IntPtr.Zero, icoAsset, ImageIcon, 16, 16, LrLoadFromFile | LrDefaultSize);
                if (h != IntPtr.Zero)
                {
                    _hIcon = h;
                    _ownsIcon = true;
                    return;
                }
            }
        }
        catch { }

        _hIcon = LoadIcon(IntPtr.Zero, IdiApplication);
        _ownsIcon = false;
    }

    private static string TruncateTip(string tip) =>
        tip.Length <= 126 ? tip : tip[..126];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        if (_ownsIcon && _hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
