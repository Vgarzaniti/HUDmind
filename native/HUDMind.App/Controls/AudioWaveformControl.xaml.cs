using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaRectangle = System.Windows.Shapes.Rectangle;

namespace HUDMind.App.Controls;

/// <summary>
/// Reusable pure-vector 0% GPU symmetric audio spectrum waveform control shared cleanly
/// between Voice settings and the in-game Overlay HUD.
/// </summary>
public partial class AudioWaveformControl : System.Windows.Controls.UserControl
{
    private static readonly double[] ProfileNormalized =
    [
        0.16, 0.24, 0.42, 0.28, 0.62, 0.88, 0.50, 0.72, 0.96, 0.64,
        0.44, 0.82, 1.00, 0.76, 0.54, 0.90, 0.68, 0.46, 0.74, 0.52,
        0.34, 0.56, 0.30, 0.22, 0.16
    ];

    public AudioWaveformControl()
    {
        InitializeComponent();
        BuildBars();
    }

    public bool ShowFrame
    {
        get => WaveFrame.BorderThickness.Top > 0;
        set
        {
            WaveFrame.Background = value ? HudVisualPalette.CreateFrozenBrush(0x05, 0x09, 0x0F) : System.Windows.Media.Brushes.Transparent;
            WaveFrame.BorderThickness = value ? new Thickness(1) : new Thickness(0);
            WaveFrame.Padding = value ? new Thickness(8, 5, 8, 5) : new Thickness(0);
        }
    }

    public void SetBarHeightScale(double maxHeightPx)
    {
        double clamped = Math.Clamp(maxHeightPx, 12, 54);
        BarsHost.Height = clamped;
        BuildBars();
    }

    private void BuildBars()
    {
        BarsHost.Children.Clear();
        MediaBrush fill = (TryFindResource("WaveCyanGreenBrush") as MediaBrush)
            ?? HudVisualPalette.CreateFrozenBrush(0x22, 0xD3, 0xEE);

        double hostH = double.IsNaN(BarsHost.Height) || BarsHost.Height <= 0 ? 32.0 : BarsHost.Height;
        foreach (double ratio in ProfileNormalized)
        {
            var bar = new MediaRectangle
            {
                Width = 3,
                Height = Math.Max(3.0, Math.Round(hostH * ratio)),
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = fill,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            BarsHost.Children.Add(bar);
        }
    }
}
