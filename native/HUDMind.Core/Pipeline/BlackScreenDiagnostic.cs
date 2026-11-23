using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

/// <summary>
/// Tracks consecutive pitch-black capture frames across both Live and Classic orchestrators
/// and emits a one-time actionable hint when a game is likely running in Exclusive Fullscreen.
/// </summary>
public sealed class BlackScreenDiagnostic
{
    private const int ConsecutiveThreshold = 6;
    private bool _warnedForStreak;

    public void Reset() => _warnedForStreak = false;

    public void Observe(
        IScreenCaptureService capture,
        GameProfile? activeProfile,
        bool testMode,
        Action<string>? onLog,
        IOverlayOutputService? overlay = null,
        bool enableOverlay = false)
    {
        if (testMode || activeProfile is null)
        {
            _warnedForStreak = false;
            return;
        }

        int blackCount = capture.ConsecutiveBlackFrames;
        if (blackCount == 0)
        {
            _warnedForStreak = false;
            return;
        }

        if (blackCount >= ConsecutiveThreshold && !_warnedForStreak)
        {
            _warnedForStreak = true;
            var name = activeProfile.Manifest.DisplayName;
            onLog?.Invoke($"⚠ Captura en negro consecutiva detectada en '{name}': si jugás en Pantalla Completa Exclusiva, cambialo a 'Ventana sin bordes' (Borderless) para que HUDMind vea el HUD.");
            if (enableOverlay)
                overlay?.ShowCallout("⚠ Captura negra: usá Ventana sin bordes (Borderless)", UrgencyLevel.Medium, name, 4.5);
        }
    }
}
