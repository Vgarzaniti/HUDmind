using System.Text.Json.Serialization;

namespace HUDMind.Core.Pipeline;

public sealed class GameLeakCounters
{
    [JsonPropertyName("current_date")]
    public string CurrentDate { get; set; } = string.Empty;

    [JsonPropertyName("previous_date")]
    public string PreviousDate { get; set; } = string.Empty;

    [JsonPropertyName("today")]
    public Dictionary<string, int> Today { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("previous")]
    public Dictionary<string, int> Previous { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("lifetime")]
    public Dictionary<string, int> Lifetime { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PlayerLeakReport
{
    [JsonPropertyName("games")]
    public Dictionary<string, GameLeakCounters> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Deterministic cross-session habit/mistake ("leak") counter comparing Today vs Previous session/day
/// so coaching tracks real measurable reduction in repeated errors instead of generic chat history.
/// </summary>
public static class PlayerLeakTracker
{
    private static readonly IReadOnlyDictionary<string, (string Es, string En)> Labels =
        new Dictionary<string, (string Es, string En)>(StringComparer.OrdinalIgnoreCase)
        {
            ["cs2_no_armor"] = ("Rondas sin chaleco", "Rounds without armor"),
            ["cs2_no_kit"] = ("Sin defuse kit con economía", "Missing defuse kit with bank"),
            ["lol_unspent_gold_death"] = ("Muertes con >1300g sin gastar", "Deaths holding >1300g unspent"),
            ["rl_low_boost"] = ("Rotaciones con <=15% boost", "Plays caught <=15% boost"),
            ["tilt_streak"] = ("Rachas anti-tilt (3+ muertes)", "Anti-tilt death streaks (3+)"),
            ["dota_low_mana_combat"] = ("Peleas con maná crítico", "Critical-mana engagements")
        };

    public static string GetLabel(string key, bool isEn = false) =>
        Labels.TryGetValue(key, out var pair)
            ? (isEn ? pair.En : pair.Es)
            : key;

    public static void RecordLeak(
        PlayerLeakReport report,
        string? gameId,
        string? leakKey,
        DateOnly? todayOverride = null)
    {
        if (report is null || string.IsNullOrWhiteSpace(leakKey))
            return;

        string g = string.IsNullOrWhiteSpace(gameId) ? "universal" : gameId.Trim().ToLowerInvariant();
        string k = leakKey.Trim().ToLowerInvariant();
        string todayIso = (todayOverride ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd");

        report.Games ??= new Dictionary<string, GameLeakCounters>(StringComparer.OrdinalIgnoreCase);
        if (!report.Games.TryGetValue(g, out var counters))
        {
            counters = new GameLeakCounters { CurrentDate = todayIso };
            report.Games[g] = counters;
        }

        RollDateIfNeeded(counters, todayIso);

        counters.Today[k] = counters.Today.GetValueOrDefault(k) + 1;
        counters.Lifetime[k] = counters.Lifetime.GetValueOrDefault(k) + 1;
    }

    public static void RollDateIfNeeded(GameLeakCounters counters, string todayIso)
    {
        if (string.IsNullOrEmpty(counters.CurrentDate))
        {
            counters.CurrentDate = todayIso;
            return;
        }

        if (string.Equals(counters.CurrentDate, todayIso, StringComparison.Ordinal))
            return;

        if (counters.Today.Count > 0)
        {
            counters.PreviousDate = counters.CurrentDate;
            counters.Previous = new Dictionary<string, int>(counters.Today, StringComparer.OrdinalIgnoreCase);
            counters.Today = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        counters.CurrentDate = todayIso;
    }

    public static string FormatComparisonSummary(
        PlayerLeakReport report,
        string? gameId,
        bool isEn = false,
        DateOnly? todayOverride = null,
        int maxItems = 3)
    {
        if (report is null || report.Games.Count == 0)
            return string.Empty;

        string g = string.IsNullOrWhiteSpace(gameId) ? "universal" : gameId.Trim().ToLowerInvariant();
        if (!report.Games.TryGetValue(g, out var counters))
            return string.Empty;

        string todayIso = (todayOverride ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd");
        RollDateIfNeeded(counters, todayIso);

        var allKeys = counters.Today.Keys
            .Concat(counters.Previous.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(k => counters.Today.GetValueOrDefault(k) + counters.Previous.GetValueOrDefault(k))
            .Take(Math.Clamp(maxItems, 1, 5))
            .ToList();

        if (allKeys.Count == 0)
            return string.Empty;

        var parts = new List<string>(allKeys.Count);
        foreach (var key in allKeys)
        {
            int cur = counters.Today.GetValueOrDefault(key);
            int prev = counters.Previous.GetValueOrDefault(key);
            string lbl = GetLabel(key, isEn);
            string item = counters.Previous.Count > 0
                ? (isEn ? $"{lbl}: {cur} today (prev {prev})" : $"{lbl}: {cur} hoy (ant {prev})")
                : (isEn ? $"{lbl}: {cur} today" : $"{lbl}: {cur} hoy");
            parts.Add(item);
        }

        return string.Join(" · ", parts);
    }
}
