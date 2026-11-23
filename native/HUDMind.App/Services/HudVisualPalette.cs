using System.Windows.Media;
using HUDMind.Core.Models;
using MediaColor = System.Windows.Media.Color;

namespace HUDMind.App;

public enum HudRunState
{
    Idle,
    Active,
    Paused
}

public sealed record HudUrgencyVisual(
    SolidColorBrush BorderBrush,
    SolidColorBrush AccentBarBrush,
    SolidColorBrush BadgeBackgroundBrush,
    SolidColorBrush BadgeBorderBrush,
    SolidColorBrush TagForegroundBrush,
    string TagText
);

public sealed record HudRunStateVisual(
    SolidColorBrush DotBrush,
    SolidColorBrush PillBackgroundBrush,
    SolidColorBrush PillBorderBrush,
    SolidColorBrush TextBrush,
    SolidColorBrush PrimaryButtonBackgroundBrush,
    SolidColorBrush PrimaryButtonBorderBrush,
    string PrimaryButtonLabel
);

/// <summary>
/// Centralized Flyweight / Strategy registry of frozen WPF brushes for the cyber-tactical HUD aesthetic
/// (0% GPU software rendering, zero per-frame brush allocations across Overlay and MainWindow).
/// </summary>
public static class HudVisualPalette
{
    public static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b, byte a = 255)
    {
        var br = new SolidColorBrush(MediaColor.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    private static readonly HudUrgencyVisual HighUrgency = new(
        BorderBrush: CreateFrozenBrush(0xEF, 0x44, 0x44, 0xD8),
        AccentBarBrush: CreateFrozenBrush(0xEF, 0x44, 0x44),
        BadgeBackgroundBrush: CreateFrozenBrush(0x3B, 0x11, 0x18),
        BadgeBorderBrush: CreateFrozenBrush(0xEF, 0x44, 0x44),
        TagForegroundBrush: CreateFrozenBrush(0xFC, 0xA5, 0xA5),
        TagText: "URGENTE");

    private static readonly HudUrgencyVisual MediumUrgency = new(
        BorderBrush: CreateFrozenBrush(0x22, 0xD3, 0xEE, 0xCC),
        AccentBarBrush: CreateFrozenBrush(0xF5, 0x9E, 0x0B),
        BadgeBackgroundBrush: CreateFrozenBrush(0x0D, 0x2C, 0x3A),
        BadgeBorderBrush: CreateFrozenBrush(0x22, 0xD3, 0xEE),
        TagForegroundBrush: CreateFrozenBrush(0xFC, 0xD3, 0x4D),
        TagText: "TÁCTICO");

    private static readonly HudUrgencyVisual LowUrgency = new(
        BorderBrush: CreateFrozenBrush(0x22, 0xD3, 0xEE, 0xCC),
        AccentBarBrush: CreateFrozenBrush(0x10, 0xB9, 0x81),
        BadgeBackgroundBrush: CreateFrozenBrush(0x0A, 0x2B, 0x33),
        BadgeBorderBrush: CreateFrozenBrush(0x10, 0xB9, 0x81),
        TagForegroundBrush: CreateFrozenBrush(0x6E, 0xE7, 0xB7),
        TagText: "ACTIVO");

    private static readonly HudRunStateVisual IdleRunState = new(
        DotBrush: CreateFrozenBrush(0x64, 0x74, 0x8B),
        PillBackgroundBrush: CreateFrozenBrush(0x0B, 0x13, 0x1E),
        PillBorderBrush: CreateFrozenBrush(0x1A, 0x2C, 0x40),
        TextBrush: CreateFrozenBrush(0x94, 0xA3, 0xB8),
        PrimaryButtonBackgroundBrush: CreateFrozenBrush(0x08, 0x6F, 0x83),
        PrimaryButtonBorderBrush: CreateFrozenBrush(0x22, 0xD3, 0xEE),
        PrimaryButtonLabel: "▶ Activar Coach");

    private static readonly HudRunStateVisual ActiveRunState = new(
        DotBrush: CreateFrozenBrush(0x10, 0xB9, 0x81),
        PillBackgroundBrush: CreateFrozenBrush(0x05, 0x2E, 0x24),
        PillBorderBrush: CreateFrozenBrush(0x10, 0xB9, 0x81, 0xCC),
        TextBrush: CreateFrozenBrush(0x6E, 0xE7, 0xB7),
        PrimaryButtonBackgroundBrush: CreateFrozenBrush(0x7F, 0x1D, 0x1D),
        PrimaryButtonBorderBrush: CreateFrozenBrush(0xEF, 0x44, 0x44),
        PrimaryButtonLabel: "⏹ Detener Coach");

    private static readonly HudRunStateVisual PausedRunState = new(
        DotBrush: CreateFrozenBrush(0xF5, 0x9E, 0x0B),
        PillBackgroundBrush: CreateFrozenBrush(0x2B, 0x1E, 0x09),
        PillBorderBrush: CreateFrozenBrush(0xF5, 0x9E, 0x0B, 0xCC),
        TextBrush: CreateFrozenBrush(0xFC, 0xD3, 0x4D),
        PrimaryButtonBackgroundBrush: CreateFrozenBrush(0x7F, 0x1D, 0x1D),
        PrimaryButtonBorderBrush: CreateFrozenBrush(0xEF, 0x44, 0x44),
        PrimaryButtonLabel: "⏹ Detener Coach");

    public static HudUrgencyVisual ResolveUrgency(UrgencyLevel urgency) => urgency switch
    {
        UrgencyLevel.High => HighUrgency,
        UrgencyLevel.Medium => MediumUrgency,
        _ => LowUrgency
    };

    public static HudRunStateVisual ResolveRunState(HudRunState state) => state switch
    {
        HudRunState.Active => ActiveRunState,
        HudRunState.Paused => PausedRunState,
        _ => IdleRunState
    };
}
