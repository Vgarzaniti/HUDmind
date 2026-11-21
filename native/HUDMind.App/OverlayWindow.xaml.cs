using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private readonly DispatcherTimer _hideTimer;
    private string _position = "top-right";

    public OverlayWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    public void Configure(string position, int width, double opacity)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _position = string.IsNullOrWhiteSpace(position) ? "top-right" : position.ToLowerInvariant();
            Width = Math.Clamp(width, 280, 680);
            Opacity = Math.Clamp(opacity, 0.35, 1.0);
            PositionOnScreen();
        });
    }

    public void ShowCallout(string text, UrgencyLevel urgency, string title, double durationSeconds)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var accent = urgency switch
            {
                UrgencyLevel.High => MediaColor.FromRgb(0xEF, 0x44, 0x44),
                UrgencyLevel.Medium => MediaColor.FromRgb(0xF5, 0x9E, 0x0B),
                _ => MediaColor.FromRgb(0x38, 0xBD, 0xF8)
            };
            var brush = new SolidColorBrush(accent);
            UrgencyBar.Background = brush;
            CardBorder.BorderBrush = brush;

            TitleBlock.Text = string.IsNullOrWhiteSpace(title) ? "HUDMind · Coach" : $"HUDMind · {title}";
            MessageBlock.Text = text;
            PositionOnScreen();

            if (!IsVisible)
                Show();

            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1.5, 15.0));
            _hideTimer.Start();
        });
    }

    private void PositionOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        const double pad = 20;
        Left = _position.Contains("left") ? wa.Left + pad : wa.Right - Width - pad;
        Top = _position.Contains("bottom") ? wa.Bottom - Math.Max(110, ActualHeight) - pad : wa.Top + pad;
    }
}
