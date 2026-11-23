using System.Text;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;

namespace HUDMind.Core.AI;

public enum CoachContextDepth
{
    RealtimePlay,
    AnalyticalReflection
}

public sealed record AssembledCoachContext(
    string CoachSoulSummary,
    string PlayerProfileSummary,
    string RollingMemorySummary
);

/// <summary>
/// Central Markdown context compiler that links `coach_soul.md`, `player_profile.md`,
/// game `custom_rules.md`, and rolling `player_memory` into deduplicated, token-budgeted
/// instructions adapted to play vs reflection depth.
/// </summary>
public static class CoachContextAssembler
{
    public const string DefaultCoachSoulMarkdown =
        """
        # Personalidad del Coach (coach_soul.md)
        - Sinceridad sin humo: decí la posta con respeto; nunca adules jugadas malas ni tires frases vacías.
        - Honestidad visual: si algo no se ve claro en pantalla o está tapado, admitilo en vez de adivinar.
        - Anti-Tilt: si vengo de varias muertes seguidas, calmá el ritmo y proponé un paso simple de reset.
        """;

    public const string DefaultPlayerProfileMarkdown =
        """
        # Perfil Global del Jugador (player_profile.md)
        <!-- Escribí acá hábitos o metas transversales a todos tus juegos (máx. 700 caracteres) -->
        """;

    public static AssembledCoachContext Assemble(
        UserConfig config,
        GameProfile? profile,
        PlayerGameMemory? gameMemory,
        string? rawSoulMarkdown,
        string? rawPlayerProfileMarkdown,
        bool isEn,
        CoachContextDepth depth = CoachContextDepth.RealtimePlay,
        string fallbackPostMatchFocus = "")
    {
        bool reflection = depth == CoachContextDepth.AnalyticalReflection;
        int soulCap = reflection ? 340 : 185;
        int profileCap = reflection ? 280 : 160;
        int memCap = reflection ? 380 : 310;

        string rollingMemory = PlayerMemoryStore.FormatForCoachPrompt(gameMemory, isEn: isEn, maxChars: memCap);
        if (string.IsNullOrWhiteSpace(rollingMemory) && !string.IsNullOrWhiteSpace(fallbackPostMatchFocus))
            rollingMemory = CoachingPersonaBuilder.SanitizeField(SecretRedactor.Redact(fallbackPostMatchFocus), memCap);

        var dedupePool = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(profile?.CustomRules))
            dedupePool.Add(profile.CustomRules);
        if (!string.IsNullOrWhiteSpace(rollingMemory))
            dedupePool.Add(rollingMemory);

        string soulSummary = ExtractCompactMarkdownBullets(rawSoulMarkdown, soulCap);
        string playerSummary = ExtractCompactMarkdownBullets(rawPlayerProfileMarkdown, profileCap, dedupePool);

        return new AssembledCoachContext(soulSummary, playerSummary, rollingMemory);
    }

    public static void ApplyToConfig(UserConfig target, AssembledCoachContext assembled)
    {
        target.CoachSoulSummary = assembled.CoachSoulSummary;
        target.PlayerProfileSummary = assembled.PlayerProfileSummary;
        target.PreviousMatchFocus = assembled.RollingMemorySummary;
    }

    public static string ExtractCompactMarkdownBullets(
        string? markdown,
        int maxChars,
        IReadOnlyCollection<string>? dedupeAgainstTexts = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var dedupeKeywords = (dedupeAgainstTexts ?? [])
            .Select(InterventionMemory.ExtractKeywords)
            .Where(k => k.Count > 0)
            .ToList();

        var selected = new List<string>(6);
        foreach (var rawLine in markdown.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                trimmed.StartsWith('#') ||
                trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            var cleanLine = trimmed.TrimStart('-', '*', '•', ' ', '\t').Replace("**", string.Empty, StringComparison.Ordinal);
            if (CoachContentGuard.ContainsUnsafeInstruction(cleanLine))
                continue;
            var safe = CoachingPersonaBuilder.SanitizeField(SecretRedactor.Redact(cleanLine), 140);
            if (string.IsNullOrWhiteSpace(safe))
                continue;

            var kw = InterventionMemory.ExtractKeywords(safe);
            if (kw.Count > 0 && dedupeKeywords.Any(d => InterventionMemory.AreKeywordSetsSimilar(d, kw, 0.65)))
                continue;

            if (kw.Count > 0 && selected.Any(s => InterventionMemory.AreKeywordSetsSimilar(InterventionMemory.ExtractKeywords(s), kw, 0.65)))
                continue;

            selected.Add(safe);
        }

        if (selected.Count == 0)
            return string.Empty;

        var joined = string.Join(" ; ", selected);
        return CoachingPersonaBuilder.SanitizeField(joined, Math.Clamp(maxChars, 60, 480));
    }

    public static string BuildAntiTiltDirective(bool isEn, int consecutiveDeaths = 0)
    {
        string streak = consecutiveDeaths > 0 ? $" ({consecutiveDeaths})" : string.Empty;
        return isEn
            ? $"ANTI-TILT ACTIVE{streak}: Player hit repeated deaths. Drop pressure, stay calm, avoid scolding, and give 1 simple stabilizing reset step."
            : $"MODO ANTI-TILT{streak}: El jugador viene de varias muertes seguidas. Bajá un cambio, no lo retes y dale 1 solo paso simple de reset mental.";
    }
}
