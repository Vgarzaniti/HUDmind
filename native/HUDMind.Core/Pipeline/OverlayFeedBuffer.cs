using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

public sealed record OverlayFeedEntry(
    long Id,
    OverlayEntryKind Kind,
    string Speaker,
    string Text,
    UrgencyLevel Urgency,
    string GameTitle,
    long ExpiresAtUtcTicks,
    bool IsStreamingLive
);

/// <summary>
/// Cross-platform state controller for the stacked Discord-style translucent HUD overlay.
/// Handles in-place Live transcript streaming coalescing, CoachLive->CoachFinal promotion,
/// ephemeral status replacement, duplicate debounce, and per-item TTL expiration.
/// </summary>
public sealed class OverlayFeedBuffer
{
    private readonly List<OverlayFeedEntry> _entries = [];
    private long _nextId;
    private int _maxMessages = 3;

    public int MaxMessages
    {
        get => _maxMessages;
        set
        {
            _maxMessages = Math.Clamp(value, 1, 4);
            TrimToMaxLocked();
        }
    }

    public IReadOnlyList<OverlayFeedEntry> Snapshot() => [.. _entries];

    public bool IsEmpty => _entries.Count == 0;

    public void Clear() => _entries.Clear();

    public IReadOnlyList<OverlayFeedEntry> Upsert(OverlayCalloutMessage message, DateTimeOffset nowUtc)
    {
        string cleanText = (message.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(cleanText))
            return Snapshot();

        double ttlSeconds = Math.Clamp(message.DurationSeconds, 1.5, 20.0);
        long expiresTicks = nowUtc.AddSeconds(ttlSeconds).Ticks;
        string gameTitle = string.IsNullOrWhiteSpace(message.Title) ? "HUDMind" : message.Title.Trim();
        string speaker = ResolveSpeakerLabel(message);

        if (_maxMessages == 1)
        {
            long id = _entries.Count == 1 && _entries[0].Kind == OverlayEntryKind.CoachLive && message.Kind is OverlayEntryKind.CoachLive or OverlayEntryKind.CoachFinal
                ? _entries[0].Id
                : ++_nextId;
            _entries.Clear();
            _entries.Add(new OverlayFeedEntry(
                Id: id,
                Kind: message.Kind,
                Speaker: speaker,
                Text: cleanText,
                Urgency: message.Urgency,
                GameTitle: gameTitle,
                ExpiresAtUtcTicks: expiresTicks,
                IsStreamingLive: message.Kind == OverlayEntryKind.CoachLive));
            return Snapshot();
        }

        // Remove stale entries before merging a fresh message
        PruneExpiredInternal(nowUtc);

        switch (message.Kind)
        {
            case OverlayEntryKind.CoachLive:
            {
                RemoveEphemeralStatusEntries();
                int liveIdx = FindLastIndexByKind(OverlayEntryKind.CoachLive);
                if (liveIdx >= 0 && liveIdx == _entries.Count - 1)
                {
                    var prev = _entries[liveIdx];
                    _entries[liveIdx] = prev with
                    {
                        Speaker = speaker,
                        Text = cleanText,
                        Urgency = message.Urgency,
                        GameTitle = gameTitle,
                        ExpiresAtUtcTicks = expiresTicks,
                        IsStreamingLive = true
                    };
                }
                else
                {
                    if (liveIdx >= 0)
                        _entries.RemoveAt(liveIdx);
                    _entries.Add(new OverlayFeedEntry(
                        ++_nextId,
                        OverlayEntryKind.CoachLive,
                        speaker,
                        cleanText,
                        message.Urgency,
                        gameTitle,
                        expiresTicks,
                        IsStreamingLive: true));
                }
                break;
            }

            case OverlayEntryKind.CoachFinal:
            {
                RemoveEphemeralStatusEntries();
                int liveIdx = FindLastIndexByKind(OverlayEntryKind.CoachLive);
                if (liveIdx >= 0)
                {
                    var prevLive = _entries[liveIdx];
                    _entries.RemoveAt(liveIdx);
                    _entries.Add(new OverlayFeedEntry(
                        prevLive.Id,
                        OverlayEntryKind.CoachFinal,
                        speaker,
                        cleanText,
                        message.Urgency,
                        gameTitle,
                        expiresTicks,
                        IsStreamingLive: false));
                }
                else if (_entries.Count > 0 &&
                         _entries[^1].Kind == OverlayEntryKind.CoachFinal &&
                         string.Equals(_entries[^1].Text, cleanText, StringComparison.OrdinalIgnoreCase))
                {
                    var prev = _entries[^1];
                    _entries[^1] = prev with
                    {
                        Urgency = message.Urgency,
                        GameTitle = gameTitle,
                        ExpiresAtUtcTicks = expiresTicks
                    };
                }
                else
                {
                    _entries.Add(new OverlayFeedEntry(
                        ++_nextId,
                        OverlayEntryKind.CoachFinal,
                        speaker,
                        cleanText,
                        message.Urgency,
                        gameTitle,
                        expiresTicks,
                        IsStreamingLive: false));
                }
                break;
            }

            case OverlayEntryKind.Status:
            {
                if (string.Equals(speaker, "BRIEF", StringComparison.OrdinalIgnoreCase))
                {
                    _entries.RemoveAll(e =>
                        e.Kind == OverlayEntryKind.Status &&
                        (string.Equals(e.Speaker, "BRIEF", StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(e.Speaker, "BRIEF", StringComparison.OrdinalIgnoreCase)));
                }
                else
                {
                    RemoveEphemeralStatusEntries();
                }
                _entries.Add(new OverlayFeedEntry(
                    ++_nextId,
                    OverlayEntryKind.Status,
                    speaker,
                    cleanText,
                    message.Urgency,
                    gameTitle,
                    expiresTicks,
                    IsStreamingLive: false));
                break;
            }

            case OverlayEntryKind.UserMessage:
            default:
            {
                RemoveEphemeralStatusEntries();
                _entries.Add(new OverlayFeedEntry(
                    ++_nextId,
                    OverlayEntryKind.UserMessage,
                    speaker,
                    cleanText,
                    message.Urgency,
                    gameTitle,
                    expiresTicks,
                    IsStreamingLive: false));
                break;
            }
        }

        TrimToMaxLocked();
        return Snapshot();
    }

    public bool PruneExpired(DateTimeOffset nowUtc) => PruneExpiredInternal(nowUtc);

    public DateTimeOffset? GetNextExpirationUtc()
    {
        if (_entries.Count == 0)
            return null;
        long minTicks = _entries[0].ExpiresAtUtcTicks;
        for (int i = 1; i < _entries.Count; i++)
        {
            if (_entries[i].ExpiresAtUtcTicks < minTicks)
                minTicks = _entries[i].ExpiresAtUtcTicks;
        }
        return new DateTimeOffset(minTicks, TimeSpan.Zero);
    }

    private bool PruneExpiredInternal(DateTimeOffset nowUtc)
    {
        long nowTicks = nowUtc.Ticks;
        int removed = _entries.RemoveAll(e => e.ExpiresAtUtcTicks <= nowTicks);
        return removed > 0;
    }

    private void RemoveEphemeralStatusEntries() =>
        _entries.RemoveAll(e =>
            e.Kind == OverlayEntryKind.Status &&
            !string.Equals(e.Speaker, "BRIEF", StringComparison.OrdinalIgnoreCase));

    private int FindLastIndexByKind(OverlayEntryKind kind)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Kind == kind)
                return i;
        }
        return -1;
    }

    private void TrimToMaxLocked()
    {
        while (_entries.Count > _maxMessages)
            _entries.RemoveAt(0);
    }

    private static string ResolveSpeakerLabel(OverlayCalloutMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Speaker))
            return message.Speaker.Trim();
        return message.Kind switch
        {
            OverlayEntryKind.UserMessage => "Vos",
            OverlayEntryKind.Status => "HUD",
            _ => "Coach"
        };
    }
}
