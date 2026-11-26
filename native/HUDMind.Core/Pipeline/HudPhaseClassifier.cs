using System.Text.RegularExpressions;

namespace HUDMind.Core.Pipeline;

/// <summary>
/// Cross-platform rule-based match phase classifier combining OCR screen bands and passive telemetry logs.
/// </summary>
public static partial class HudPhaseClassifier
{
    [GeneratedRegex(@"\b\d{1,2}:\d{2}\b", RegexOptions.Compiled)]
    private static partial Regex TimerRegex();

    [GeneratedRegex(@"^\s*[123]\s*$", RegexOptions.Compiled)]
    private static partial Regex CountdownRegex();

    private static readonly string[] MenuKeywords =
    [
        "PLAY", "JUGAR", "SETTINGS", "OPCIONES", "AJUSTES", "GARAGE", "GARAJE",
        "ITEM SHOP", "TIENDA", "MAIN MENU", "MENÚ PRINCIPAL", "MENU PRINCIPAL",
        "LOCKER", "SALIR", "QUIT GAME", "COMPETITIVE", "CASUAL", "LOBBY"
    ];

    private static readonly string[] ReplayKeywords =
    [
        "REPLAY", "REPETICIÓN", "REPETICION", "SCORED BY", "ANOTADO POR",
        "KILLCAM", "PLAY OF THE GAME", "JUGADA DESTACADA", "GOAL!", "GOL!"
    ];

    private static readonly string[] LoadingKeywords =
    [
        "LOADING", "CARGANDO", "SEARCHING", "BUSCANDO PARTIDA", "JOINING", "CONNECTING", "CONECTANDO", "MATCH FOUND"
    ];

    private static readonly string[] TrainingKeywords =
    [
        "FREE PLAY", "FREEPLAY", "ENTRENAMIENTO", "PRACTICE", "THE RANGE", "WARMUP", "CALENTAMIENTO", "ART OF WAR", "TUTORIAL"
    ];

    private static readonly string[] KickoffKeywords =
    [
        "GO!", "YA!", "FREEZETIME", "BUY PHASE", "FASE DE COMPRA", "PREP PHASE", "KICKOFF"
    ];

    private static readonly string[] LogLoadingMarkers = ["WAIT_FOR_PLAYERS", "DISCONNECT"];
    private static readonly string[] LogMenuMarkers =
    [
        "PHASE=GAMEOVER", "POST_GAME", "MENU_MAIN", "HERO_SELECTION", "STRATEGY_TIME",
        "EN MENÚ PRINCIPAL", "EN MENU PRINCIPAL", "MENU / LOBBY", "PARTIDO FINALIZADO"
    ];

    public static string ClassifyPhase(IReadOnlyDictionary<string, string> zones, string logSummary)
    {
        var hudPriorityText = string.Join(" ", zones
            .Where(kv => kv.Key is not ("MID-LEFT" or "BOT-LEFT"))
            .Select(kv => kv.Value));
        var ocrHudUpper = NormalizePunct(hudPriorityText);
        var logUpper = NormalizePunct(logSummary ?? string.Empty);
        var hudUpper = string.Concat(ocrHudUpper, " ", logUpper);
        var allUpper = NormalizePunct(string.Join(" ", zones.Values) + " " + (logSummary ?? string.Empty));

        if (LogLoadingMarkers.Any(m => logUpper.Contains(m, StringComparison.Ordinal)))
            return "LOADING";
        if (LogMenuMarkers.Any(m => logUpper.Contains(m, StringComparison.Ordinal)))
            return "MENU";

        if (ReplayKeywords.Any(k => ContainsBoundedPhrase(hudUpper, k)))
            return "REPLAY";
        if (LoadingKeywords.Any(k => ContainsBoundedPhrase(hudUpper, k)))
            return "LOADING";

        // Match timers strictly on screen OCR so DOTA2_GSI / log clocks never suppress OCR menu keywords
        bool hasTimer = TimerRegex().IsMatch(ocrHudUpper);
        int menuHits = MenuKeywords.Count(k => ContainsBoundedPhrase(allUpper, k));
        if (menuHits >= 2 && !hasTimer)
            return "MENU";

        if (TrainingKeywords.Any(k => ContainsBoundedPhrase(hudUpper, k)))
            return "TRAINING";

        if (KickoffKeywords.Any(k => ContainsBoundedPhrase(hudUpper, k)) ||
            (zones.TryGetValue("CENTER", out var centerText) && CountdownRegex().IsMatch(centerText)))
            return "KICKOFF";

        if (menuHits == 1 && !hasTimer && string.IsNullOrWhiteSpace(logSummary))
            return "MENU";

        if (hasTimer || zones.ContainsKey("BOT-RIGHT") || zones.ContainsKey("BOT-LEFT") || !string.IsNullOrWhiteSpace(logSummary))
            return "PLAY";

        return "UNKNOWN";
    }

    private static bool ContainsBoundedPhrase(string upperText, string keyword)
    {
        int idx = 0;
        while ((idx = upperText.IndexOf(keyword, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(upperText[idx - 1]);
            int after = idx + keyword.Length;
            bool rightOk = after >= upperText.Length || !char.IsLetterOrDigit(upperText[after]);
            if (leftOk && rightOk)
                return true;
            idx = after;
        }
        return false;
    }

    private static string NormalizePunct(string s) =>
        s.Replace('¡', ' ').Replace('¿', ' ').ToUpperInvariant();
}
