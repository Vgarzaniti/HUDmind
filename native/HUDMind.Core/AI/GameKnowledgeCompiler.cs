using System.Text;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;

namespace HUDMind.Core.AI;

/// <summary>
/// Compiles concise, query-aware game rules &amp; mechanical checklists from bundled game
/// `knowledge_base.md` and `system_prompt.md` files so the AI uses real game-specific
/// expertise instead of generic advice.
/// </summary>
public static class GameKnowledgeCompiler
{
    public static string CompileForPlay(
        GameProfile? profile,
        string? coachMode,
        string? phaseHint = null,
        int maxChars = 460)
    {
        if (profile is null)
            return string.Empty;

        var queryHints = new List<string>(4);
        if (string.Equals(coachMode, "training", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(phaseHint, "TRAINING", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(phaseHint, "FREEPLAY", StringComparison.OrdinalIgnoreCase))
        {
            queryHints.Add("entrenamiento training mecánicas aerial dribbling flick flip reset recovery crosshair burst");
        }
        else
        {
            queryHints.Add("predictivo anticipar rotación boost radar economía último hombre segundo palo timing");
        }

        if (!string.IsNullOrWhiteSpace(phaseHint))
            queryHints.Add(phaseHint);

        return ExtractBestSections(profile, string.Join(' ', queryHints), maxChars);
    }

    public static string CompileForQuestion(
        GameProfile? profile,
        string userQuestion,
        int maxChars = 760)
    {
        if (profile is null)
            return string.Empty;

        return ExtractBestSections(profile, userQuestion, maxChars);
    }

    private static string ExtractBestSections(GameProfile profile, string queryText, int maxChars)
    {
        var blocks = new List<(string Text, int BaseBonus)>();

        if (!string.IsNullOrWhiteSpace(profile.TacticalSummary))
            blocks.Add((CoachingPersonaBuilder.SanitizeField(profile.TacticalSummary, 260), 2));
        else if (!string.IsNullOrWhiteSpace(profile.Manifest.Notes))
            blocks.Add((CoachingPersonaBuilder.SanitizeField(profile.Manifest.Notes, 220), 1));

        if (!string.IsNullOrWhiteSpace(profile.KnowledgeGuide))
        {
            foreach (var rawSec in profile.KnowledgeGuide.Split("## ", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = rawSec
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim().TrimStart('#', '-', '*', '•', ' ').Replace("**", string.Empty, StringComparison.Ordinal))
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("Lista Negra", StringComparison.OrdinalIgnoreCase))
                    .Select(l => CoachingPersonaBuilder.SanitizeField(l, 380))
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();

                if (lines.Count == 0)
                    continue;

                var heading = lines[0];
                var body = string.Join(" ; ", lines.Skip(1).Take(6));
                var combined = string.IsNullOrWhiteSpace(body) ? heading : $"{heading}: {body}";
                blocks.Add((combined, 0));
            }
        }

        if (blocks.Count == 0)
            return string.Empty;

        var qTokens = InterventionMemory.ExtractKeywords(queryText);
        var scored = blocks
            .Select((b, idx) =>
            {
                var bTokens = InterventionMemory.ExtractKeywords(b.Text);
                int overlap = qTokens.Count == 0 ? 0 : bTokens.Count(t => qTokens.Contains(t));
                int score = overlap * 10 + b.BaseBonus + (idx == 0 ? 1 : 0);
                return (b.Text, Score: score, Order: idx);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Order)
            .ToList();

        var sb = new StringBuilder(Math.Min(maxChars, 820));
        foreach (var item in scored)
        {
            if (sb.Length >= maxChars - 24)
                break;

            int perBlockBudget = Math.Min(maxChars >= 650 ? 540 : 320, maxChars - sb.Length);
            string snippet = CoachingPersonaBuilder.SanitizeField(item.Text, perBlockBudget);
            if (string.IsNullOrWhiteSpace(snippet))
                continue;

            if (sb.Length > 0)
                sb.Append(" | ");
            sb.Append(snippet);
        }

        var result = sb.ToString();
        return result.Length <= maxChars ? result : result[..maxChars];
    }
}
