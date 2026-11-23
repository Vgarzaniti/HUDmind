using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

public sealed class MatchMemoryEntry
{
    [JsonPropertyName("match_id")]
    public string MatchId { get; set; } = string.Empty;

    [JsonPropertyName("recorded_at_utc")]
    public DateTimeOffset RecordedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("game_id")]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyName("game_display_name")]
    public string GameDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("interventions_count")]
    public int InterventionsCount { get; set; }

    [JsonPropertyName("habits")]
    public List<string> Habits { get; set; } = [];

    [JsonPropertyName("next_drill")]
    public string NextDrill { get; set; } = string.Empty;

    [JsonPropertyName("is_ai_reviewed")]
    public bool IsAiReviewed { get; set; }
}

public sealed class MasteredHabitRecord
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("mastered_at_utc")]
    public DateTimeOffset MasteredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlayerGameMemory
{
    [JsonPropertyName("game_id")]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("window_size")]
    public int WindowSize { get; set; } = PlayerMemoryStore.DefaultWindowSize;

    [JsonPropertyName("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("latest_review_file")]
    public string LatestReviewFile { get; set; } = string.Empty;

    [JsonPropertyName("mastered_habits")]
    public List<MasteredHabitRecord> MasteredHabits { get; set; } = [];

    [JsonPropertyName("matches")]
    public List<MatchMemoryEntry> Matches { get; set; } = [];
}

public sealed record RecurringHabitStat(
    string Summary,
    int Occurrences,
    int WindowMatchCount,
    DateTimeOffset LastSeenUtc
);

/// <summary>
/// Pure rolling N-match player memory engine that retains only the newest N sessions per game,
/// clusters repeated habits across those N matches, and evaporates old patterns once they age out.
/// </summary>
public static class PlayerMemoryStore
{
    public const int MinWindowSize = 3;
    public const int DefaultWindowSize = 10;
    public const int MaxWindowSize = 30;

    public static int ClampWindowSize(int windowSize) =>
        Math.Clamp(windowSize <= 0 ? DefaultWindowSize : windowSize, MinWindowSize, MaxWindowSize);

    public static MatchMemoryEntry BuildEntryFromSessionSnapshot(
        MatchSessionSnapshot snapshot,
        DateTimeOffset? nowUtc = null)
    {
        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        var habits = new List<string>(3);
        foreach (var raw in snapshot.AdvicesSample)
        {
            var clean = SanitizeHabit(raw, 96);
            if (string.IsNullOrWhiteSpace(clean))
                continue;
            var kw = InterventionMemory.ExtractKeywords(clean);
            if (habits.Any(h => InterventionMemory.AreKeywordSetsSimilar(InterventionMemory.ExtractKeywords(h), kw, 0.55)))
                continue;
            habits.Add(clean);
            if (habits.Count >= 3)
                break;
        }

        return new MatchMemoryEntry
        {
            MatchId = string.IsNullOrWhiteSpace(snapshot.MatchId) ? Guid.NewGuid().ToString("N")[..12] : snapshot.MatchId.Trim(),
            RecordedAtUtc = ts,
            GameId = snapshot.GameId ?? string.Empty,
            GameDisplayName = CoachingPersonaBuilder.SanitizeField(snapshot.DisplayName, 48),
            InterventionsCount = Math.Max(0, snapshot.InterventionsCount),
            Habits = habits,
            NextDrill = habits.FirstOrDefault() ?? string.Empty,
            IsAiReviewed = false
        };
    }

    public static MatchMemoryEntry BuildEntryFromAiReview(
        string? matchId,
        string? gameId,
        string? gameDisplayName,
        int interventionsCount,
        string reviewText,
        DateTimeOffset? nowUtc = null)
    {
        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        var (habits, drill) = ParseReviewHabitsAndDrill(reviewText);
        if (habits.Count == 0 && !string.IsNullOrWhiteSpace(drill))
            habits.Add(drill);

        return new MatchMemoryEntry
        {
            MatchId = string.IsNullOrWhiteSpace(matchId) ? Guid.NewGuid().ToString("N")[..12] : matchId.Trim(),
            RecordedAtUtc = ts,
            GameId = gameId ?? string.Empty,
            GameDisplayName = CoachingPersonaBuilder.SanitizeField(gameDisplayName, 48),
            InterventionsCount = Math.Max(1, interventionsCount),
            Habits = habits,
            NextDrill = drill,
            IsAiReviewed = true
        };
    }

    /// <summary>
    /// Inserts or upgrades a match entry and evicts oldest matches beyond <paramref name="windowSize"/>.
    /// </summary>
    public static void ApplyRollingMatch(
        PlayerGameMemory memory,
        MatchMemoryEntry rawEntry,
        int windowSize = DefaultWindowSize)
    {
        int clamped = ClampWindowSize(windowSize);
        var entry = SanitizeEntry(rawEntry);
        if (entry.Habits.Count == 0 && string.IsNullOrWhiteSpace(entry.NextDrill) && entry.InterventionsCount <= 0)
            return;

        var beforeRecurring = ComputeRecurringHabits(memory, minOccurrences: 2);

        memory.WindowSize = clamped;
        memory.UpdatedAtUtc = entry.RecordedAtUtc;
        if (!string.IsNullOrWhiteSpace(entry.GameId))
            memory.GameId = entry.GameId;
        if (!string.IsNullOrWhiteSpace(entry.GameDisplayName))
            memory.DisplayName = entry.GameDisplayName;

        int existingIdx = !string.IsNullOrWhiteSpace(entry.MatchId)
            ? memory.Matches.FindIndex(m => string.Equals(m.MatchId, entry.MatchId, StringComparison.OrdinalIgnoreCase))
            : -1;

        if (existingIdx >= 0)
        {
            // Preserve prior habits if manual review somehow lacked them, otherwise upgrade in place
            var prev = memory.Matches[existingIdx];
            if (entry.Habits.Count == 0 && prev.Habits.Count > 0)
                entry.Habits = prev.Habits;
            if (entry.InterventionsCount <= 0 && prev.InterventionsCount > 0)
                entry.InterventionsCount = prev.InterventionsCount;
            memory.Matches[existingIdx] = entry;
        }
        else
        {
            memory.Matches.Add(entry);
        }

        memory.Matches = memory.Matches
            .OrderBy(m => m.RecordedAtUtc)
            .ToList();

        while (memory.Matches.Count > clamped)
            memory.Matches.RemoveAt(0);

        UpdateHabitGraduation(memory, beforeRecurring, entry);
    }

    private static void UpdateHabitGraduation(
        PlayerGameMemory memory,
        List<RecurringHabitStat> beforeRecurring,
        MatchMemoryEntry latestEntry)
    {
        var afterRecurring = ComputeRecurringHabits(memory, minOccurrences: 2);
        var latestKeywords = latestEntry.Habits
            .Select(InterventionMemory.ExtractKeywords)
            .Where(k => k.Count > 0)
            .ToList();

        // Revoke graduation if a recurring mistake comes back or appears in the latest match
        memory.MasteredHabits.RemoveAll(m =>
        {
            var mk = InterventionMemory.ExtractKeywords(m.Summary);
            if (mk.Count == 0) return true;
            return afterRecurring.Any(r => InterventionMemory.AreKeywordSetsSimilar(mk, InterventionMemory.ExtractKeywords(r.Summary), 0.50)) ||
                   latestKeywords.Any(lk => InterventionMemory.AreKeywordSetsSimilar(mk, lk, 0.50));
        });

        // Promote a previously recurring habit to mastered once older matches age out and latest match is clean
        foreach (var prev in beforeRecurring)
        {
            var pk = InterventionMemory.ExtractKeywords(prev.Summary);
            if (pk.Count == 0) continue;

            bool stillRecurring = afterRecurring.Any(r => InterventionMemory.AreKeywordSetsSimilar(pk, InterventionMemory.ExtractKeywords(r.Summary), 0.50));
            bool seenInLatest = latestKeywords.Any(lk => InterventionMemory.AreKeywordSetsSimilar(pk, lk, 0.50));
            bool alreadyMastered = memory.MasteredHabits.Any(m => InterventionMemory.AreKeywordSetsSimilar(pk, InterventionMemory.ExtractKeywords(m.Summary), 0.50));

            if (!stillRecurring && !seenInLatest && !alreadyMastered)
            {
                memory.MasteredHabits.Add(new MasteredHabitRecord
                {
                    Summary = prev.Summary,
                    MasteredAtUtc = latestEntry.RecordedAtUtc
                });
            }
        }

        if (memory.MasteredHabits.Count > 5)
            memory.MasteredHabits = memory.MasteredHabits.OrderByDescending(m => m.MasteredAtUtc).Take(5).ToList();
    }

    /// <summary>
    /// Computes recurring habits across ONLY the active rolling window of matches.
    /// </summary>
    public static List<RecurringHabitStat> ComputeRecurringHabits(
        PlayerGameMemory? memory,
        int minOccurrences = 2)
    {
        if (memory is null || memory.Matches.Count == 0)
            return [];

        int windowMatches = memory.Matches.Count;
        var clusters = new List<(HashSet<string> Keywords, string BestText, HashSet<int> MatchIndices, DateTimeOffset LastSeenUtc)>();

        for (int matchIdx = 0; matchIdx < memory.Matches.Count; matchIdx++)
        {
            var match = memory.Matches[matchIdx];
            foreach (var rawHabit in match.Habits)
            {
                var clean = SanitizeHabit(rawHabit, 96);
                if (string.IsNullOrWhiteSpace(clean))
                    continue;

                var kw = InterventionMemory.ExtractKeywords(clean);
                if (kw.Count == 0)
                    continue;

                int targetIdx = -1;
                for (int c = 0; c < clusters.Count; c++)
                {
                    if (InterventionMemory.AreKeywordSetsSimilar(clusters[c].Keywords, kw, overlapThreshold: 0.50))
                    {
                        targetIdx = c;
                        break;
                    }
                }

                if (targetIdx < 0)
                {
                    clusters.Add((kw, clean, [matchIdx], match.RecordedAtUtc));
                }
                else
                {
                    var cur = clusters[targetIdx];
                    cur.MatchIndices.Add(matchIdx);
                    if (match.RecordedAtUtc >= cur.LastSeenUtc)
                        clusters[targetIdx] = (cur.Keywords, clean, cur.MatchIndices, match.RecordedAtUtc);
                }
            }
        }

        return clusters
            .Where(c => c.MatchIndices.Count >= minOccurrences)
            .OrderByDescending(c => c.MatchIndices.Count)
            .ThenByDescending(c => c.LastSeenUtc)
            .Select(c => new RecurringHabitStat(c.BestText, c.MatchIndices.Count, windowMatches, c.LastSeenUtc))
            .ToList();
    }

    public static string FormatForCoachPrompt(
        PlayerGameMemory? memory,
        bool isEn = false,
        int maxChars = 320)
    {
        if (memory is null || memory.Matches.Count == 0)
            return string.Empty;

        int n = memory.Matches.Count;
        var recurring = ComputeRecurringHabits(memory, minOccurrences: 2);
        var latestDrill = memory.Matches
            .LastOrDefault(m => !string.IsNullOrWhiteSpace(m.NextDrill))
            ?.NextDrill ?? string.Empty;

        var parts = new List<string>(3);
        if (recurring.Count > 0)
        {
            var topPatterns = string.Join("; ", recurring.Take(2).Select(r => $"x{r.Occurrences}/{n} '{r.Summary}'"));
            parts.Add(isEn
                ? $"Last {n} matches recurring: {topPatterns}"
                : $"Últimas {n} partidas repetido: {topPatterns}");
        }
        else
        {
            var latestHabit = memory.Matches[^1].Habits.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(latestHabit))
            {
                parts.Add(isEn
                    ? $"Last match watch-out: {latestHabit}"
                    : $"Último partido a cuidar: {latestHabit}");
            }
        }

        if (!string.IsNullOrWhiteSpace(latestDrill) &&
            !parts.Any(p => p.Contains(latestDrill, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(isEn ? $"Active drill: {latestDrill}" : $"Foco activo: {latestDrill}");
        }

        if (memory.MasteredHabits.Count > 0)
        {
            var latestMastered = memory.MasteredHabits[^1].Summary;
            parts.Add(isEn ? $"Improved lately: {latestMastered}" : $"Ya superado: {latestMastered}");
        }

        if (parts.Count == 0)
            return string.Empty;

        return CoachingPersonaBuilder.SanitizeField(
            SecretRedactor.Redact(string.Join(" | ", parts)),
            Math.Clamp(maxChars, 80, 420));
    }

    public static string FormatForUiSummary(PlayerGameMemory? memory)
    {
        if (memory is null || memory.Matches.Count == 0)
            return "🧠 Memoria del coach: sin partidas registradas aún (se actualiza sola al jugar).";

        int n = memory.Matches.Count;
        int cap = ClampWindowSize(memory.WindowSize);
        var recurring = ComputeRecurringHabits(memory, minOccurrences: 2);
        string masteredTag = memory.MasteredHabits.Count > 0
            ? $" · 🏆 Superado: {memory.MasteredHabits[^1].Summary}"
            : string.Empty;

        if (recurring.Count > 0)
        {
            var top = string.Join(" · ", recurring.Take(2).Select(r => $"x{r.Occurrences} {r.Summary}"));
            return $"🧠 Memoria ({n}/{cap} partidas): repetido {top}{masteredTag}";
        }

        var latest = memory.Matches[^1];
        var hint = !string.IsNullOrWhiteSpace(latest.NextDrill)
            ? latest.NextDrill
            : (latest.Habits.FirstOrDefault() ?? $"{latest.InterventionsCount} consejos guardados");
        return $"🧠 Memoria ({n}/{cap} partidas): último foco — {hint}{masteredTag}";
    }

    public static string RenderGameMemoryMarkdown(
        PlayerGameMemory memory,
        string? customRulesRelPath = null,
        string? latestReviewRelPath = null)
    {
        var gameTitle = string.IsNullOrWhiteSpace(memory.DisplayName) ? memory.GameId : memory.DisplayName;
        int cap = ClampWindowSize(memory.WindowSize);
        var recurring = ComputeRecurringHabits(memory, minOccurrences: 2);

        var sb = new StringBuilder(1024);
        sb.Append("# 🧠 Memoria del Coach — ").AppendLine(gameTitle);
        sb.AppendLine();
        sb.Append("- **Ventana rodante:** ").Append(memory.Matches.Count).Append('/').Append(cap).AppendLine(" partidas (se borra automáticamente la más vieja)");
        sb.Append("- **Última actualización:** ").AppendLine(memory.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sb.Append("- **Documentos vinculados:** [Personalidad / Soul](../../config/coach_soul.md) · [Perfil Global](../../config/player_profile.md)");
        if (!string.IsNullOrWhiteSpace(customRulesRelPath))
            sb.Append(" · [Reglas del Juego](").Append(customRulesRelPath).Append(')');
        if (!string.IsNullOrWhiteSpace(latestReviewRelPath))
            sb.Append(" · [Último Resumen](").Append(latestReviewRelPath).Append(')');
        sb.AppendLine();
        sb.AppendLine("- **Índice global:** [INDEX.md](INDEX.md)");
        sb.AppendLine();

        sb.AppendLine("## 🔁 Patrones repetidos en la ventana actual");
        if (recurring.Count == 0)
        {
            sb.AppendLine("_Todavía no hay patrones repetidos (2+ partidas) en esta ventana._");
        }
        else
        {
            foreach (var r in recurring)
                sb.Append("- **x").Append(r.Occurrences).Append('/').Append(r.WindowMatchCount).Append("**: ").AppendLine(r.Summary);
        }
        sb.AppendLine();

        sb.AppendLine("## 🏆 Hábitos superados recientemente");
        if (memory.MasteredHabits.Count == 0)
        {
            sb.AppendLine("_Ningún patrón graduado todavía (cuando un hábito deja de repetirse y sale de la ventana, aparece acá)._");
        }
        else
        {
            foreach (var m in memory.MasteredHabits)
                sb.Append("- ✅ ").Append(m.Summary).Append(" _(").Append(m.MasteredAtUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).AppendLine(")_");
        }
        sb.AppendLine();

        sb.AppendLine("## 📋 Últimas partidas registradas");
        foreach (var match in memory.Matches.AsEnumerable().Reverse())
        {
            var stamp = match.RecordedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var tag = match.IsAiReviewed ? "Resumen IA" : "Sesión auto";
            sb.Append("### ").Append(stamp).Append(" · ").Append(tag).Append(" (").Append(match.InterventionsCount).AppendLine(" consejos)");
            foreach (var h in match.Habits)
                sb.Append("- ").AppendLine(h);
            if (!string.IsNullOrWhiteSpace(match.NextDrill))
                sb.Append("- **Foco / Ejercicio:** ").AppendLine(match.NextDrill);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string RenderGlobalIndexMarkdown(IEnumerable<PlayerGameMemory> memories)
    {
        var list = memories
            .Where(m => m.Matches.Count > 0)
            .OrderByDescending(m => m.UpdatedAtUtc)
            .ToList();

        var sb = new StringBuilder(768);
        sb.AppendLine("# 🗂 Índice de Memoria del Coach (`HUDMind`)");
        sb.AppendLine();
        sb.AppendLine("- [🎭 Personalidad del Coach (`coach_soul.md`)](../../config/coach_soul.md)");
        sb.AppendLine("- [🧭 Perfil Global del Jugador (`player_profile.md`)](../../config/player_profile.md)");
        sb.AppendLine();

        if (list.Count == 0)
        {
            sb.AppendLine("_Todavía no hay partidas guardadas en memoria._");
            return sb.ToString();
        }

        sb.AppendLine("| Juego | Ventana | Repetidos | Superados | Archivo |");
        sb.AppendLine("| :--- | :---: | :---: | :---: | :--- |");
        foreach (var m in list)
        {
            var name = string.IsNullOrWhiteSpace(m.DisplayName) ? m.GameId : m.DisplayName;
            int recCount = ComputeRecurringHabits(m, 2).Count;
            sb.Append("| ").Append(name)
              .Append(" | ").Append(m.Matches.Count).Append('/').Append(ClampWindowSize(m.WindowSize))
              .Append(" | ").Append(recCount)
              .Append(" | ").Append(m.MasteredHabits.Count)
              .Append(" | [").Append(m.GameId).Append(".md](").Append(m.GameId).AppendLine(".md) |");
        }

        return sb.ToString();
    }

    private static (List<string> Habits, string Drill) ParseReviewHabitsAndDrill(string? reviewText)
    {
        var habits = new List<string>(3);
        var drill = string.Empty;
        if (string.IsNullOrWhiteSpace(reviewText))
            return (habits, drill);

        int section = 0;
        foreach (var rawLine in reviewText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Replace("**", string.Empty, StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("1)", StringComparison.Ordinal) ||
                line.StartsWith("1.", StringComparison.Ordinal))
            {
                section = 1;
                var tail = StripListPrefix(line[2..]);
                if (!IsSectionHeadingOnly(tail) && habits.Count < 3)
                    TryAddUniqueHabit(habits, tail);
                continue;
            }
            if (line.StartsWith("2)", StringComparison.Ordinal) ||
                line.StartsWith("2.", StringComparison.Ordinal))
            {
                section = 2;
                continue;
            }
            if (line.StartsWith("3)", StringComparison.Ordinal) ||
                line.StartsWith("3.", StringComparison.Ordinal))
            {
                section = 3;
                var tail = StripListPrefix(line[2..]);
                if (!IsSectionHeadingOnly(tail) && string.IsNullOrWhiteSpace(drill))
                    drill = SanitizeHabit(tail, 130);
                continue;
            }

            if (section == 1 && habits.Count < 3)
            {
                TryAddUniqueHabit(habits, StripListPrefix(line));
            }
            else if (section == 3 && string.IsNullOrWhiteSpace(drill))
            {
                drill = SanitizeHabit(StripListPrefix(line), 130);
            }
        }

        return (habits, drill);
    }

    private static void TryAddUniqueHabit(List<string> habits, string candidate)
    {
        var clean = SanitizeHabit(candidate, 96);
        if (string.IsNullOrWhiteSpace(clean))
            return;
        var kw = InterventionMemory.ExtractKeywords(clean);
        if (habits.Any(h => InterventionMemory.AreKeywordSetsSimilar(InterventionMemory.ExtractKeywords(h), kw, 0.55)))
            return;
        habits.Add(clean);
    }

    private static string StripListPrefix(string s) =>
        s.TrimStart('-', '*', '•', ' ', '\t', ':');

    private static bool IsSectionHeadingOnly(string s) =>
        s.Length < 10 ||
        s.EndsWith(':') ||
        s.StartsWith("Top 3", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("1 ejercicio", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("1 concrete", StringComparison.OrdinalIgnoreCase);

    private static MatchMemoryEntry SanitizeEntry(MatchMemoryEntry raw) => new()
    {
        MatchId = string.IsNullOrWhiteSpace(raw.MatchId) ? Guid.NewGuid().ToString("N")[..12] : CoachingPersonaBuilder.SanitizeField(raw.MatchId, 32),
        RecordedAtUtc = raw.RecordedAtUtc == default ? DateTimeOffset.UtcNow : raw.RecordedAtUtc,
        GameId = CoachingPersonaBuilder.SanitizeField(raw.GameId, 48),
        GameDisplayName = CoachingPersonaBuilder.SanitizeField(raw.GameDisplayName, 48),
        InterventionsCount = Math.Max(0, raw.InterventionsCount),
        Habits = raw.Habits
            .Select(h => SanitizeHabit(h, 96))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Take(3)
            .ToList(),
        NextDrill = SanitizeHabit(raw.NextDrill, 130),
        IsAiReviewed = raw.IsAiReviewed
    };

    private static string SanitizeHabit(string? raw, int maxLen) =>
        CoachingPersonaBuilder.SanitizeField(SecretRedactor.Redact(raw ?? string.Empty), maxLen);
}
