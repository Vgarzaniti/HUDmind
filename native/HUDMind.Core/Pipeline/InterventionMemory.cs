using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

public sealed partial class InterventionMemory
{
    private const int MaxMatchEvents = 120;

    private sealed record MemoryEntry(DateTimeOffset TimestampUtc, UrgencyLevel Urgency, string Text, HashSet<string> Keywords);
    private sealed record VisualEvent(DateTimeOffset TimestampUtc, string Phase, string Summary);
    private sealed record ChatExchange(DateTimeOffset TimestampUtc, string UserText, string CoachReply);
    private sealed record MatchSessionEvent(DateTimeOffset TimestampUtc, string GameId, string DisplayName, string Phase, string Summary, string Advice);

    private readonly object _sync = new();
    private readonly LinkedList<MemoryEntry> _entries = new();
    private readonly LinkedList<VisualEvent> _timeline = new();
    private readonly LinkedList<ChatExchange> _chatHistory = new();
    private readonly List<MatchSessionEvent> _matchEvents = [];
    private string _lastMatchGameName = string.Empty;
    private readonly int _maxItems;
    private readonly TimeSpan _ttl;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "la", "el", "los", "las", "en", "un", "una", "por", "para", "con", "que", "tu", "tus",
        "te", "se", "al", "del", "es", "no", "si", "y", "o", "mi", "mis", "su", "sus", "lo", "le",
        "hay", "muy", "bien", "mal", "mas", "ahora", "solo", "esta", "este", "tenes", "tienes", "vas",
        "the", "to", "a", "an", "in", "on", "at", "of", "for", "with", "your", "you", "and", "or", "is", "now"
    };

    [GeneratedRegex(@"[a-z0-9]{1,}", RegexOptions.Compiled)]
    private static partial Regex WordRegex();

    public InterventionMemory(int maxItems = 12, double ttlSeconds = 75.0)
    {
        _maxItems = Math.Max(1, maxItems);
        _ttl = TimeSpan.FromSeconds(Math.Max(10.0, ttlSeconds));
    }

    public void Clear(bool clearMatchLog = false)
    {
        lock (_sync)
        {
            _entries.Clear();
            _timeline.Clear();
            _chatHistory.Clear();
            if (clearMatchLog)
            {
                _matchEvents.Clear();
                _lastMatchGameName = string.Empty;
            }
        }
    }

    public int MatchEventCount
    {
        get { lock (_sync) return _matchEvents.Count; }
    }

    public string LastMatchGameName
    {
        get { lock (_sync) return _lastMatchGameName; }
    }

    /// <summary>
    /// Records every coaching advice across the full match (never expires on 75s anti-spam TTL)
    /// so the player can request an end-of-match coaching digest at any time.
    /// </summary>
    public void RecordMatchEvent(
        string gameId,
        string displayName,
        string phase,
        string summary,
        string advice,
        DateTimeOffset? nowUtc = null)
    {
        var cleanAdvice = SanitizeOneLine(advice, 120);
        if (string.IsNullOrWhiteSpace(cleanAdvice))
            return;

        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        var cleanGame = string.IsNullOrWhiteSpace(displayName) ? "Videojuego" : SanitizeOneLine(displayName, 48);
        var cleanPhase = string.IsNullOrWhiteSpace(phase) ? "PLAY" : SanitizeOneLine(phase, 24).ToUpperInvariant();
        var cleanSummary = SanitizeOneLine(summary, 80);

        lock (_sync)
        {
            _lastMatchGameName = cleanGame;
            _matchEvents.Add(new MatchSessionEvent(ts, gameId ?? string.Empty, cleanGame, cleanPhase, cleanSummary, cleanAdvice));
            if (_matchEvents.Count > MaxMatchEvents)
                _matchEvents.RemoveAt(0);
        }
    }

    public (string GameName, string Digest, int Count) BuildMatchReviewDigest(int maxEvents = 36, bool isEn = false)
    {
        lock (_sync)
        {
            if (_matchEvents.Count == 0)
                return (_lastMatchGameName, string.Empty, 0);

            int total = _matchEvents.Count;
            var slice = total <= maxEvents ? _matchEvents : _matchEvents.Skip(total - maxEvents).ToList();
            var sb = new StringBuilder(Math.Min(1800, slice.Count * 55));
            foreach (var ev in slice)
            {
                if (sb.Length >= 1650) break;
                var localTime = ev.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                sb.Append(localTime).Append(" [").Append(ev.Phase).Append("] ");
                if (!string.IsNullOrWhiteSpace(ev.Summary))
                    sb.Append(ev.Summary).Append(" -> ");
                sb.Append(ev.Advice).Append('\n');
            }
            return (_lastMatchGameName, sb.ToString().TrimEnd(), total);
        }
    }

    public void RecordChatTurn(string userText, string coachReply, DateTimeOffset? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(userText) && string.IsNullOrWhiteSpace(coachReply))
            return;

        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        var u = SanitizeOneLine(userText, 120);
        var c = SanitizeOneLine(coachReply, 140);
        lock (_sync)
        {
            PruneLocked(ts);
            _chatHistory.AddLast(new ChatExchange(ts, u, c));
            while (_chatHistory.Count > 4)
                _chatHistory.RemoveFirst();
        }
    }

    public string FormatChatHistory(DateTimeOffset? nowUtc = null, int maxTurns = 3, bool isEn = false)
    {
        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            PruneLocked(ts);
            if (_chatHistory.Count == 0)
                return string.Empty;

            return string.Join(" | ", _chatHistory.TakeLast(maxTurns).Select(ch =>
                isEn ? $"Player: {ch.UserText} / Coach: {ch.CoachReply}"
                     : $"Jugador: {ch.UserText} / Coach: {ch.CoachReply}"));
        }
    }

    /// <summary>
    /// Records every grounded visual observation (even during REPLAY/KICKOFF/silence)
    /// so the AI understands match progression across ANY game.
    /// </summary>
    public void RecordObservation(string phase, string visualSummary, DateTimeOffset? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(visualSummary) && string.IsNullOrWhiteSpace(phase))
            return;

        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        var cleanPhase = string.IsNullOrWhiteSpace(phase) ? "PLAY" : phase.Trim().ToUpperInvariant();
        var cleanSum = SanitizeOneLine(visualSummary, 100);

        lock (_sync)
        {
            PruneLocked(ts);
            _timeline.AddLast(new VisualEvent(ts, cleanPhase, cleanSum));
            while (_timeline.Count > 6)
                _timeline.RemoveFirst();
        }
    }

    public string FormatVisualTimeline(DateTimeOffset? nowUtc = null, int maxEvents = 4, bool isEn = false)
    {
        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            PruneLocked(ts);
            if (_timeline.Count == 0)
                return string.Empty;

            return string.Join(" ; ", _timeline.TakeLast(maxEvents).Select(ev =>
            {
                int ago = Math.Max(1, (int)Math.Round((ts - ev.TimestampUtc).TotalSeconds));
                return isEn
                    ? $"{ago}s ago [{ev.Phase}] {ev.Summary}"
                    : $"hace {ago}s [{ev.Phase}] {ev.Summary}";
            }));
        }
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

    public bool IsRepetitive(CoachResponse candidate, DateTimeOffset? nowUtc = null, double overlapThreshold = 0.68)
    {
        if (!candidate.ShouldIntervene || string.IsNullOrWhiteSpace(candidate.OverlayText))
            return false;

        var ts = nowUtc ?? DateTimeOffset.UtcNow;
        var candidateText = candidate.OverlayText.Trim();
        var kw = ExtractKeywords(candidateText);
        if (kw.Count == 0)
            return false;

        lock (_sync)
        {
            PruneLocked(ts);
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Text, candidateText, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (entry.Keywords.Count == 0)
                    continue;

                int shared = 0;
                foreach (var w in kw)
                {
                    if (entry.Keywords.Contains(w))
                        shared++;
                }

                // Jaccard + symmetric Dice protection: short tactical calls ("A" vs "B") never falsely collide!
                int union = kw.Count + entry.Keywords.Count - shared;
                double jaccard = union > 0 ? shared / (double)union : 0.0;
                double overlap = shared / (double)Math.Min(kw.Count, entry.Keywords.Count);
                if (jaccard >= 0.55 || (shared >= 3 && overlap >= overlapThreshold))
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
        while (_timeline.First is not null && (nowUtc - _timeline.First.Value.TimestampUtc) > TimeSpan.FromSeconds(90))
            _timeline.RemoveFirst();
        while (_chatHistory.First is not null && (nowUtc - _chatHistory.First.Value.TimestampUtc) > TimeSpan.FromSeconds(180))
            _chatHistory.RemoveFirst();
    }

    private static string SanitizeOneLine(string? raw, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var clean = string.Join(' ', raw.Replace('|', '/').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        return clean.Length <= maxLen ? clean : clean[..maxLen];
    }

    private static string NormalizeToken(ReadOnlySpan<char> rawToken)
    {
        var decomposed = rawToken.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        var norm = sb.ToString();
        // Preserve 1-char tactical markers only if uppercase/bomba/rank/digit in original text, skip 1-char noise letters except digits/a/b/c
        return norm;
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        // Preserve uppercase single-letter tactical designators (e.g. Bombsite "A", "B", "C") BEFORE lowercasing!
        var tacticalPreFold = Regex.Replace(text, @"\b([A-C])\b", "#$1");
        var folded = NormalizeToken(tacticalPreFold.AsSpan());
        foreach (var match in WordRegex().EnumerateMatches(folded.AsSpan()))
        {
            var token = folded.Substring(match.Index, match.Length);
            if (match.Index > 0 && folded[match.Index - 1] == '#')
                token = "#" + token;
            if (token.Length == 1 && !char.IsDigit(token[0]))
                continue;
            if (!StopWords.Contains(token))
                set.Add(token);
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
        return GetWaitUntilReady(cooldownSeconds, minIntervalSeconds, nowUtc) <= TimeSpan.Zero;
    }

    public TimeSpan GetWaitUntilReady(double cooldownSeconds, double minIntervalSeconds, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (_busy)
                return TimeSpan.FromMilliseconds(250);

            var remCd = TimeSpan.FromSeconds(cooldownSeconds) - (now - _lastDeliveryUtc);
            var remMin = TimeSpan.FromSeconds(minIntervalSeconds) - (now - _lastInferenceUtc);
            var maxWait = remCd > remMin ? remCd : remMin;
            return maxWait > TimeSpan.Zero ? maxWait : TimeSpan.Zero;
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
