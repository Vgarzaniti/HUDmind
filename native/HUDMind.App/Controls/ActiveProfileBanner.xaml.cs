using System.Globalization;
using System.Windows.Media;

namespace HUDMind.App.Controls;

/// <summary>
/// Modular tactical dashboard header card displaying the active game profile,
/// context/memory link state, AI response latency, and voice channel status.
/// </summary>
public partial class ActiveProfileBanner : System.Windows.Controls.UserControl
{
    private static readonly SolidColorBrush ContextActiveBg = HudVisualPalette.CreateFrozenBrush(0x05, 0x2E, 0x24);
    private static readonly SolidColorBrush ContextActiveBorder = HudVisualPalette.CreateFrozenBrush(0x10, 0xB9, 0x81);
    private static readonly SolidColorBrush ContextActiveFg = HudVisualPalette.CreateFrozenBrush(0x6E, 0xE7, 0xB7);

    private static readonly SolidColorBrush ContextIdleBg = HudVisualPalette.CreateFrozenBrush(0x0B, 0x16, 0x23);
    private static readonly SolidColorBrush ContextIdleBorder = HudVisualPalette.CreateFrozenBrush(0x1E, 0x33, 0x48);
    private static readonly SolidColorBrush ContextIdleFg = HudVisualPalette.CreateFrozenBrush(0x94, 0xA3, 0xB8);

    public ActiveProfileBanner()
    {
        InitializeComponent();
    }

    public void UpdateHeaders(bool isLive)
    {
        LblAiCardTitle.Text = isLive ? "RESPUESTA EN VIVO" : "VELOCIDAD DE RESPUESTA";
        LblVoiceCardTitle.Text = isLive ? "VOZ Y MICRÓFONO" : "VOZ DEL COACH";
    }

    public void Reset(bool isTextOnly, string metaSubtitle)
    {
        StatActiveGame.Text = "Esperando juego";
        TxtProfileMeta.Text = metaSubtitle;
        StatAiMs.Text = "—";
        StatVoiceMs.Text = isTextOnly ? "Silencio" : "Listo";
        StatInterventions.Text = "0";
        SetContextStatus("Memoria .md: LISTO", isHighlighted: false);
    }

    public void UpdateMetrics(
        string activeGame,
        string metaSubtitle,
        double lastAiMs,
        string voiceStatus,
        long interventions,
        string contextStatusText,
        bool contextActive)
    {
        StatActiveGame.Text = string.IsNullOrWhiteSpace(activeGame) || activeGame == "-"
            ? "Detectando..."
            : activeGame;
        TxtProfileMeta.Text = metaSubtitle;
        StatAiMs.Text = lastAiMs <= 0
            ? "—"
            : (lastAiMs >= 1000 ? $"{lastAiMs / 1000.0:0.0} s" : $"{lastAiMs:F0} ms");
        StatVoiceMs.Text = voiceStatus;
        StatInterventions.Text = interventions.ToString(CultureInfo.InvariantCulture);
        SetContextStatus(contextStatusText, contextActive);
    }

    public void SetContextStatus(string label, bool isHighlighted)
    {
        TxtContextStatus.Text = label;
        ContextPillBorder.Background = isHighlighted ? ContextActiveBg : ContextIdleBg;
        ContextPillBorder.BorderBrush = isHighlighted ? ContextActiveBorder : ContextIdleBorder;
        TxtContextStatus.Foreground = isHighlighted ? ContextActiveFg : ContextIdleFg;
    }
}
