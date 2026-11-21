using System.Text.RegularExpressions;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

public sealed partial class InterventionMemory
{
    private sealed record MemoryEntry(DateTimeOffset TimestampUtc, UrgencyLevel Urgency, string Text, HashSet<string> Keywords);

    private readonly object _sync = new();
    private readonly LinkedList<MemoryEntry> _entries = new();
    private readonly int _maxItems;
    private readonly TimeSpan _ttl;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "la", "el", "los", "las", "en", "un", "una", "por", "para", "con", "que", "tu", "tus",
        "te", "se", "al", "del", "es", "no", "si", "y", "o", "the", "to", "a", "in", "on", "your", "and"
    };

    [GeneratedRegex(@"[a-zA-ZáéíóúñÁÉÍÓÚÑ0-9]{3,}", RegexOptions.Compiled)]
    private static partial Regex WordRegex();

    public InterventionMemory(int maxItems = 12, double ttlSeconds = 420.0)
    {
        _maxItems = Math.Max(1, maxItems);
        _ttl = TimeSpan.FromSeconds(Math.Max(10.0, ttlSeconds));
    }

    public void Clear()
    {
        lock (_sync)
            _entries.Clear();
    }

    public void Record(CoachResponse response, DateTimeOffset? nowUtc = null)
    {
        if (!response.ShouldIntervene || string.IsNullOrWhiteSpace(response.OverlayText))
            return;

        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        var kw = ExtractKeywords(response.OverlayText);

        lock (_sync)
        {
            PruneLocked(ts);
            _entries.AddLast(new MemoryEntry(ts, response.Urgency, response.OverlayText.Trim(), kw));
            while (_entries.Count > _maxItems)
                _entries.RemoveFirst();
        }
    }

    public bool IsRepetitive(CoachResponse candidate, DateTimeOffset? nowUtc = null, double overlapThreshold = 0.62)
    {
        if (!candidate.ShouldIntervene || string.IsNullOrWhiteSpace(candidate.OverlayText))
            return false;

        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        var kw = ExtractKeywords(candidate.OverlayText);
        if (kw.Count == 0)
            return false;

        lock (_sync)
        {
            PruneLocked(ts);
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Text, candidate.OverlayText.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;

                if (entry.Keywords.Count == 0)
                    continue;

                int shared = kw.Count(w => entry.Keywords.Contains(w));
                double denom = Math.Min(kw.Count, entry.Keywords.Count);
                if (denom > 0 && (shared / denom) >= overlapThreshold)
                    return true;
            }
        }
        return false;
    }

    public string FormatCompactHistory(DateTimeOffset? nowUtc = null, int maxLines = 4)
    {
        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            PruneLocked(ts);
            if (_entries.Count == 0)
                return string.Empty;

            return string.Join(
                " ; ",
                _entries.TakeLast(maxLines).Select(e => e.Text)
            );
        }
    }

    private void PruneLocked(DateTimeOffset nowUtc)
    {
        while (_entries.First is not null && (nowUtc - _entries.First.Value.TimestampUtc) > _ttl)
            _entries.RemoveFirst();
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in WordRegex().Matches(text))
        {
            if (!StopWords.Contains(m.Value))
                set.Add(m.Value);
        }
        return set;
    }
}

public sealed class InterventionStateMachine
{
    private readonly object _sync = new();
    private bool _busy;
    private DateTimeOffset _lastInferenceUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDeliveryUtc = DateTimeOffset.MinValue;

    public bool IsBusy
    {
        get { lock (_sync) return _busy; }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _busy = false;
            _lastInferenceUtc = DateTimeOffset.MinValue;
            _lastDeliveryUtc = DateTimeOffset.MinValue;
        }
    }

    public bool CanAnalyze(double cooldownSeconds, double minIntervalSeconds, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (_busy)
                return false;
            if ((now - _lastDeliveryUtc).TotalSeconds < cooldownSeconds)
                return false;
            if ((now - _lastInferenceUtc).TotalSeconds < minIntervalSeconds)
                return false;
            return true;
        }
    }

    public bool TryBeginAnalysis()
    {
        lock (_sync)
        {
            if (_busy)
                return false;
            _busy = true;
            return true;
        }
    }

    public void EndAnalysis(bool delivered, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            _busy = false;
            _lastInferenceUtc = now;
            if (delivered)
                _lastDeliveryUtc = now;
        }
    }
}
