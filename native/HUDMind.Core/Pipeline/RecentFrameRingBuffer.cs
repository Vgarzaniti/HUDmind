using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

/// <summary>
/// Thread-safe circular buffer holding the last few distinct frames (~0.25s spacing)
/// so interactive questions and predictive tactical reads inspect real temporal motion
/// rather than a single isolated post-action snapshot.
/// </summary>
public sealed class RecentFrameRingBuffer
{
    private readonly object _sync = new();
    private readonly List<TemporalFrameSample> _frames = [];
    private readonly int _maxFrames;
    private readonly double _minSpacingMs;

    public RecentFrameRingBuffer(int maxFrames = 4, double minSpacingMs = 240.0)
    {
        _maxFrames = Math.Clamp(maxFrames, 2, 8);
        _minSpacingMs = Math.Clamp(minSpacingMs, 80.0, 1500.0);
    }

    public int Count
    {
        get { lock (_sync) return _frames.Count; }
    }

    public void Record(CapturedFrameData frame, bool forceReplaceTailIfTooClose = false)
    {
        if (frame.JpegBytes is not { Length: > 0 })
            return;

        lock (_sync)
        {
            if (_frames.Count > 0)
            {
                var last = _frames[^1];
                double elapsedMs = (frame.CapturedAtUtc - last.CapturedAtUtc).TotalMilliseconds;
                if (elapsedMs < _minSpacingMs)
                {
                    if (forceReplaceTailIfTooClose)
                        _frames[^1] = new TemporalFrameSample(frame.JpegBytes, frame.CapturedAtUtc, frame.PerceptualHash);
                    return;
                }
            }

            _frames.Add(new TemporalFrameSample(frame.JpegBytes, frame.CapturedAtUtc, frame.PerceptualHash));
            while (_frames.Count > _maxFrames)
                _frames.RemoveAt(0);
        }
    }

    /// <summary>
    /// Returns chronological frames within <paramref name="maxAgeSeconds"/> (oldest -> newest).
    /// </summary>
    public List<TemporalFrameSample> SnapshotRecent(DateTimeOffset nowUtc, double maxAgeSeconds = 5.0, int maxCount = 4)
    {
        lock (_sync)
        {
            if (_frames.Count == 0)
                return [];

            var minUtc = nowUtc.AddSeconds(-Math.Max(0.5, maxAgeSeconds));
            return _frames
                .Where(f => f.CapturedAtUtc >= minUtc)
                .TakeLast(Math.Clamp(maxCount, 1, _maxFrames))
                .ToList();
        }
    }

    /// <summary>
    /// Records <paramref name="latestFrame"/> and composes a clean timestamped 2x1 (2 frames) or 2x2 (3-4 frames)
    /// temporal sequence JPEG when multiple motion frames are available; otherwise returns the single frame bytes.
    /// </summary>
    public (byte[]? JpegBytes, int PanelCount) BuildTemporalPayload(
        ITemporalStoryboardComposer? composer,
        CapturedFrameData? latestFrame,
        int maxFrames,
        double maxAgeSeconds,
        int jpegQuality)
    {
        if (latestFrame is null || latestFrame.JpegBytes is not { Length: > 0 })
            return (null, 0);

        Record(latestFrame, forceReplaceTailIfTooClose: true);
        if (composer is null || maxFrames <= 1)
            return (latestFrame.JpegBytes, 1);

        var seq = SnapshotRecent(latestFrame.CapturedAtUtc, maxAgeSeconds, maxFrames);
        if (seq.Count < 2)
            return (latestFrame.JpegBytes, 1);

        int targetW = 1440;
        int targetH = seq.Count >= 3 ? 810 : 405;
        var composed = composer.ComposeSequenceJpeg(seq, targetW, targetH, Math.Clamp(jpegQuality, 70, 92));
        return composed is { Length: > 0 }
            ? (composed, Math.Min(4, seq.Count))
            : (latestFrame.JpegBytes, 1);
    }

    public void Clear()
    {
        lock (_sync)
            _frames.Clear();
    }
}
