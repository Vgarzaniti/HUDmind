using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using MediaColor = System.Windows.Media.Color;

namespace HUDMind.App;

public partial class OverlayWindow : Window, IOverlayOutputService
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(IntPtr hwnd, int index, nint newStyle);

    private static readonly SolidColorBrush HighBrush = CreateFrozenBrush(0xEF, 0x44, 0x44);
    private static readonly SolidColorBrush MedBrush = CreateFrozenBrush(0xF5, 0x9E, 0x0B);
    private static readonly SolidColorBrush LowBrush = CreateFrozenBrush(0x38, 0xBD, 0xF8);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(MediaColor.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private readonly DispatcherTimer _hideTimer;
    private string _position = "top-right";
    private int _monitorIndex = 1;
    private double _targetOpacity = 0.90;
    private bool _closed;

    public OverlayWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            FadeOutAndHide();
        };
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_closed)
                PositionOnScreen();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        nint exStyle = GetWindowLongPtr64(hwnd, GwlExStyle);
        SetWindowLongPtr64(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    public void Configure(string position, int width, double opacity, int monitorIndex = 1)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _position = string.IsNullOrWhiteSpace(position) ? "top-right" : position.ToLowerInvariant();
            _monitorIndex = Math.Max(1, monitorIndex);
            Width = Math.Clamp(width, 280, 680);
            _targetOpacity = Math.Clamp(opacity, 0.35, 1.0);
            if (IsVisible)
            {
                Opacity = _targetOpacity;
                PositionOnScreen();
            }
        });
    }

    public void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            var brush = urgency switch
            {
                UrgencyLevel.High => HighBrush,
                UrgencyLevel.Medium => MedBrush,
                _ => LowBrush
            };
            UrgencyBar.Background = brush;
            CardBorder.BorderBrush = brush;

            TitleBlock.Text = string.IsNullOrWhiteSpace(title) ? "HUDMind · Coach" : $"HUDMind · {title}";
            MessageBlock.Text = text;

            if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }

            UpdateLayout();
            PositionOnScreen();

            BeginAnimation(OpacityProperty, new DoubleAnimation(_targetOpacity, TimeSpan.FromMilliseconds(140)));

            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1.5, 15.0));
            _hideTimer.Start();
        });
    }

    private void FadeOutAndHide()
    {
        if (_closed || !IsVisible) return;
        var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180));
        anim.Completed += (_, _) =>
        {
            if (!_closed && !_hideTimer.IsEnabled)
                Hide();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void ShutdownCleanly()
    {
        _closed = true;
        _hideTimer.Stop();
        try { Close(); } catch { /* already closed */ }
    }

    private void PositionOnScreen()
    {
        var screens = Screen.AllScreens;
        int idx = Math.Clamp(_monitorIndex - 1, 0, Math.Max(0, screens.Length - 1));
        var waPx = screens.Length > 0 ? screens[idx].WorkingArea : new System.Drawing.Rectangle(0, 0, 1920, 1080);

        // Convert physical monitor pixels to WPF DIPs cleanly under PerMonitorV2 DPI
        double scaleX = 1.0, scaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            scaleX = source.CompositionTarget.TransformFromDevice.M11;
            scaleY = source.CompositionTarget.TransformFromDevice.M22;
        }

        double waLeft = waPx.Left * scaleX;
        double waTop = waPx.Top * scaleY;
        double waW = waPx.Width * scaleX;
        double waH = waPx.Height * scaleY;

        const double pad = 20;
        double h = Math.Max(84, ActualHeight);

        if (_position.Contains("center"))
            Left = waLeft + (waW - Width) / 2.0;
        else if (_position.Contains("left"))
            Left = waLeft + pad;
        else
            Left = waLeft + waW - Width - pad;

        Top = _position.Contains("bottom")
            ? waTop + waH - h - pad
            : waTop + pad;
    }
}
