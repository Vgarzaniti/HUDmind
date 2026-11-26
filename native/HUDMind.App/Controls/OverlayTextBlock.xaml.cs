using System.Windows;

namespace HUDMind.App.Controls;

/// <summary>
/// Pure 0% GPU crisp vector-shadowed HUD text block designed for AllowsTransparency="True" windows.
/// Guarantees full glyph legibility over bright or dark games even when card backdrop opacity is 0%.
/// </summary>
public partial class OverlayTextBlock : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(OverlayTextBlock),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping),
        typeof(TextWrapping),
        typeof(OverlayTextBlock),
        new FrameworkPropertyMetadata(TextWrapping.Wrap));

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming),
        typeof(TextTrimming),
        typeof(OverlayTextBlock),
        new FrameworkPropertyMetadata(TextTrimming.None));

    public static readonly DependencyProperty StrongHaloProperty = DependencyProperty.Register(
        nameof(StrongHalo),
        typeof(bool),
        typeof(OverlayTextBlock),
        new FrameworkPropertyMetadata(false, OnStrongHaloChanged));

    public OverlayTextBlock()
    {
        InitializeComponent();
        ApplyShadowBrushes(StrongHalo);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    public bool StrongHalo
    {
        get => (bool)GetValue(StrongHaloProperty);
        set => SetValue(StrongHaloProperty, value);
    }

    private static void OnStrongHaloChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OverlayTextBlock ctrl)
            ctrl.ApplyShadowBrushes(e.NewValue is true);
    }

    private void ApplyShadowBrushes(bool strongHalo)
    {
        HaloRimText.Foreground = HudVisualPalette.HaloRimDark;
        HaloRimText.Visibility = strongHalo ? Visibility.Visible : Visibility.Collapsed;
        ShadowText.Foreground = strongHalo ? HudVisualPalette.ShadowStrongDark : HudVisualPalette.ShadowSoftDark;
    }
}
