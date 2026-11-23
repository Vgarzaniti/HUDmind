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
        if (recurring.Count > 0)
        {
            var top = string.Join(" · ", recurring.Take(2).Select(r => $"x{r.Occurrences} {r.Summary}"));
            return $"🧠 Memoria ({n}/{cap} partidas): repetido {top}";
        }

        var latest = memory.Matches[^1];
        var hint = !string.IsNullOrWhiteSpace(latest.NextDrill)
            ? latest.NextDrill
            : (latest.Habits.FirstOrDefault() ?? $"{latest.InterventionsCount} consejos guardados");
        return $"🧠 Memoria ({n}/{cap} partidas): último foco — {hint}";
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
