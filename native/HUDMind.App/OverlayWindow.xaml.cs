using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Windows;
using Microsoft.Win32;
using MediaColor = System.Windows.Media.Color;

namespace HUDMind.App;

public partial class OverlayWindow : Window, IOverlayOutputService
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(IntPtr hwnd, int index, nint newStyle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private readonly DispatcherTimer _hideTimer;
    private MonitorBoundsRect[] _cachedWorkingAreas = [];
    private bool _screensDirty = true;
    private string _position = "top-right";
    private int _monitorIndex = 1;
    private double _targetOpacity = 0.90;
    private bool _closed;
    private double _lastActualH;
    private string _lastPosKey = string.Empty;

    public OverlayWindow()
    {
        InitializeComponent();
        OverlayWave.SetBarHeightScale(18);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            FadeOutAndHide();
        };
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_closed && Math.Abs(ActualHeight - _lastActualH) > 1.0)
                PositionOnScreen(force: true);
        };
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _screensDirty = true;
        _lastPosKey = string.Empty;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (IntPtr.Size == 8)
        {
            nint exStyle = GetWindowLongPtr64(hwnd, GwlExStyle);
            SetWindowLongPtr64(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }
        else
        {
            int exStyle = GetWindowLong32(hwnd, GwlExStyle);
            SetWindowLong32(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }
    }

    public void SetHotkeyHints(string pauseKey, string analyzeKey)
    {
        if (_closed) return;
        var p = string.IsNullOrWhiteSpace(pauseKey) ? "F9" : pauseKey.Trim().ToUpperInvariant();
        var a = string.IsNullOrWhiteSpace(analyzeKey) ? "F10" : analyzeKey.Trim().ToUpperInvariant();
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            TxtPauseChip.Text = $"{p} PAUSA";
            TxtAnalyzeChip.Text = $"{a} IA";
        });
    }

    public void Configure(
        string position,
        int width,
        double opacity,
        int monitorIndex = 1,
        double scale = 1.0,
        bool compact = false)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _position = string.IsNullOrWhiteSpace(position) ? "top-right" : position.ToLowerInvariant();
            _monitorIndex = Math.Max(1, monitorIndex);
            double s = Math.Clamp(scale, 0.75, 1.45);
            double baseW = compact ? Math.Max(300, width * 0.85) : Math.Max(width, 456);
            Width = Math.Clamp(baseW * s, 260, 760);
            _targetOpacity = Math.Clamp(opacity, 0.35, 1.0);

            var chromeVis = compact ? Visibility.Collapsed : Visibility.Visible;
            CardBorder.Padding = compact ? new Thickness(10, 7, 10, 7) : new Thickness(13, 11, 13, 11);
            IconBadge.Visibility = chromeVis;
            TitleBlock.Visibility = chromeVis;
            StatusTagBorder.Visibility = chromeVis;
            FooterDock.Visibility = chromeVis;
            TitleBlock.FontSize = 10.5 * s;
            MessageBlock.FontSize = (compact ? 13.5 : 14.5) * s;
            MessageBlock.Margin = compact ? new Thickness(0) : new Thickness(0, 2, 0, 0);
            if (!compact)
                OverlayWave.SetBarHeightScale(Math.Round(18.0 * s));

            if (IsVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = _targetOpacity;
                PositionOnScreen(force: true);
            }
        });
    }

    public void HideImmediately()
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _hideTimer.Stop();
            BeginAnimation(OpacityProperty, null);
            Hide();
        });
    }

    public void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            var visual = HudVisualPalette.ResolveUrgency(urgency);
            UrgencyBar.Background = visual.AccentBarBrush;
            CardBorder.BorderBrush = visual.BorderBrush;
            StatusTagBorder.Background = visual.BadgeBackgroundBrush;
            StatusTagBorder.BorderBrush = visual.BadgeBorderBrush;
            StatusTagText.Foreground = visual.TagForegroundBrush;
            StatusTagText.Text = visual.TagText;

            TitleBlock.Text = string.IsNullOrWhiteSpace(title)
                ? "AI COPILOT · HUDMIND"
                : $"AI COPILOT · {title.ToUpperInvariant()}";
            MessageBlock.Text = text;

            bool wasVisible = IsVisible;
            if (!wasVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                Show();
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
            }

            UpdateLayout();
            PositionOnScreen(force: !wasVisible);

            if (!wasVisible)
            {
                var anim = new DoubleAnimation(_targetOpacity, TimeSpan.FromMilliseconds(120));
                Timeline.SetDesiredFrameRate(anim, 24);
                BeginAnimation(OpacityProperty, anim);
            }
            else
            {
                // Unconditionally cancel any pending FadeOutAndHide animation before settling opacity!
                BeginAnimation(OpacityProperty, null);
                if (Math.Abs(Opacity - _targetOpacity) > 0.01)
                    Opacity = _targetOpacity;
            }

            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1.5, 15.0));
            _hideTimer.Start();
        });
    }

    private void FadeOutAndHide()
    {
        if (_closed || !IsVisible) return;
        var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(150));
        Timeline.SetDesiredFrameRate(anim, 24);
        anim.Completed += (_, _) =>
        {
            if (!_closed && !_hideTimer.IsEnabled)
            {
                BeginAnimation(OpacityProperty, null);
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void ShutdownCleanly()
    {
        _closed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hideTimer.Stop();
        try { Close(); } catch { }
    }

    private void PositionOnScreen(bool force = false)
    {
        if (_screensDirty || _cachedWorkingAreas.Length == 0)
        {
            _cachedWorkingAreas = Win32DisplayHelper.GetAllWorkingAreas();
            _screensDirty = false;
        }

        int idx = Math.Clamp(_monitorIndex - 1, 0, Math.Max(0, _cachedWorkingAreas.Length - 1));
        var waPx = _cachedWorkingAreas.Length > 0 ? _cachedWorkingAreas[idx] : new MonitorBoundsRect(0, 0, 1920, 1080);

        double h = Math.Max(84, ActualHeight);
        string posKey = $"{_monitorIndex}:{_position}:{Width:F0}:{h:F0}:{waPx.Left},{waPx.Top},{waPx.Width},{waPx.Height}";
        if (!force && posKey == _lastPosKey)
            return;
        _lastPosKey = posKey;
        _lastActualH = h;

        // Query target monitor's exact DPI via shcore.dll and place using physical pixels via SetWindowPos
        // so mixed-DPI multi-monitor setups position accurately without WPF cross-monitor DPI distortion!
        var (scaleX, scaleY) = GetTargetMonitorScale(waPx);
        double targetDpiScaleX = scaleX > 0 ? (1.0 / scaleX) : 1.0;
        double targetDpiScaleY = scaleY > 0 ? (1.0 / scaleY) : 1.0;

        int padPx = (int)Math.Round(20 * targetDpiScaleX);
        int widthPx = (int)Math.Round(Width * targetDpiScaleX);
        int heightPx = (int)Math.Round(h * targetDpiScaleY);

        int pxLeft = _position.Contains("center")
            ? waPx.Left + (waPx.Width - widthPx) / 2
            : _position.Contains("left")
                ? waPx.Left + padPx
                : waPx.Left + waPx.Width - widthPx - padPx;

        int pxTop = _position.Contains("bottom")
            ? waPx.Top + waPx.Height - heightPx - padPx
            : waPx.Top + padPx;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && IsVisible)
        {
            SetWindowPos(hwnd, HwndTopmost, pxLeft, pxTop, 0, 0, SwpNoActivate | SwpNoSize);
        }
        else
        {
            Left = pxLeft * scaleX;
            Top = pxTop * scaleY;
        }
    }

    private (double ScaleX, double ScaleY) GetTargetMonitorScale(MonitorBoundsRect waPx)
    {
        try
        {
            var pt = new POINT { X = waPx.Left + Math.Max(1, waPx.Width / 2), Y = waPx.Top + Math.Max(1, waPx.Height / 2) };
            var hMon = MonitorFromPoint(pt, MonitorDefaultToNearest);
            if (hMon != IntPtr.Zero && GetDpiForMonitor(hMon, MdtEffectiveDpi, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
                return (96.0 / dpiX, 96.0 / dpiY);
        }
        catch { }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
            return (source.CompositionTarget.TransformFromDevice.M11, source.CompositionTarget.TransformFromDevice.M22);
        return (1.0, 1.0);
    }
}
