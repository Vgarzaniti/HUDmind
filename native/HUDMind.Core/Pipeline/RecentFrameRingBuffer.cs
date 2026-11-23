using System.Globalization;
using System.Text;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

/// <summary>
/// Thread-safe circular buffer holding high-density motion frames (~8 FPS, ~125ms spacing)
/// so interactive questions, voice queries, and predictive callouts perceive a real video-like
/// clip sequence instead of a single isolated post-action snapshot.
/// </summary>
public sealed class RecentFrameRingBuffer
{
    private readonly object _sync = new();
    private readonly List<TemporalFrameSample> _frames = [];
    private readonly int _maxFrames;
    private readonly double _minSpacingMs;

    public RecentFrameRingBuffer(int maxFrames = 10, double minSpacingMs = 125.0)
    {
        _maxFrames = Math.Clamp(maxFrames, 2, 16);
        _minSpacingMs = Math.Clamp(minSpacingMs, 60.0, 1500.0);
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
    /// Returns evenly distributed chronological frames within <paramref name="maxAgeSeconds"/> (oldest -> newest).
    /// </summary>
    public List<TemporalFrameSample> SnapshotRecent(DateTimeOffset nowUtc, double maxAgeSeconds = 5.0, int maxCount = 6)
    {
        lock (_sync)
        {
            if (_frames.Count == 0)
                return [];

            var minUtc = nowUtc.AddSeconds(-Math.Max(0.5, maxAgeSeconds));
            var inWindow = _frames
                .Where(f => f.CapturedAtUtc >= minUtc)
                .ToList();

            return SelectEvenlySpacedWithTail(inWindow, Math.Clamp(maxCount, 1, _maxFrames));
        }
    }

    /// <summary>
    /// Records <paramref name="latestFrame"/> and builds a rich dual payload containing:
    /// 1) chronological native HD keyframes (for multi-image Vision APIs &amp; Live WebSockets) and
    /// 2) a timestamped CPU filmstrip (`2x1`, `2x2`, or `3x2`) for single-image endpoint fallback.
    /// </summary>
    public TemporalVisualPayload BuildVisualClipPayload(
        ITemporalStoryboardComposer? composer,
        CapturedFrameData? latestFrame,
        int maxFrames,
        double maxAgeSeconds,
        int jpegQuality,
        bool isEn)
    {
        if (latestFrame is null || latestFrame.JpegBytes is not { Length: > 0 })
            return new TemporalVisualPayload([], null, 0, string.Empty);

        Record(latestFrame, forceReplaceTailIfTooClose: true);
        int boundedMax = Math.Clamp(maxFrames, 1, 6);
        var seq = boundedMax == 1
            ? [new TemporalFrameSample(latestFrame.JpegBytes, latestFrame.CapturedAtUtc, latestFrame.PerceptualHash)]
            : SnapshotRecent(latestFrame.CapturedAtUtc, maxAgeSeconds, boundedMax);

        if (seq.Count == 0)
            seq = [new TemporalFrameSample(latestFrame.JpegBytes, latestFrame.CapturedAtUtc, latestFrame.PerceptualHash)];

        if (seq.Count == 1)
            return new TemporalVisualPayload(seq, latestFrame.JpegBytes, 1, string.Empty);

        byte[]? storyboard = null;
        if (composer is not null)
        {
            int targetW = seq.Count >= 5 ? 1620 : 1440;
            int targetH = seq.Count >= 5 ? 720 : (seq.Count >= 3 ? 810 : 405);
            storyboard = composer.ComposeSequenceJpeg(seq, targetW, targetH, Math.Clamp(jpegQuality, 70, 92));
        }

        string seqNote = FormatSequenceNote(seq, isEn);
        return new TemporalVisualPayload(
            seq,
            storyboard,
            seq.Count,
            seqNote);
    }

    /// <summary>
    /// Backward-compatible single-image storyboard builder delegating to <see cref="BuildVisualClipPayload"/>.
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

        if (composer is null || maxFrames <= 1)
        {
            Record(latestFrame, forceReplaceTailIfTooClose: true);
            return (latestFrame.JpegBytes, 1);
        }

        var clip = BuildVisualClipPayload(composer, latestFrame, maxFrames, maxAgeSeconds, jpegQuality, isEn: false);
        return clip.StoryboardJpeg is { Length: > 0 }
            ? (clip.StoryboardJpeg, clip.PanelCount)
            : (latestFrame.JpegBytes, 1);
    }

    public static string FormatSequenceNote(IReadOnlyList<TemporalFrameSample> frames, bool isEn)
    {
        if (frames.Count < 2)
            return string.Empty;

        var newest = frames[^1].CapturedAtUtc;
        var sb = new StringBuilder(112);
        sb.Append(isEn
            ? $"[Video-like clip sequence: {frames.Count} chronological frames "
            : $"[Secuencia tipo video: {frames.Count} cuadros cronológicos ");
        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0) sb.Append(" -> ");
            bool isLast = i == frames.Count - 1;
            if (isLast)
            {
                sb.Append('[').Append(i + 1).Append(':').Append(isEn ? "NOW]" : "AHORA]");
            }
            else
            {
                double sec = Math.Max(0.1, (newest - frames[i].CapturedAtUtc).TotalSeconds);
                sb.Append('[').Append(i + 1).Append(":-").Append(sec.ToString("0.0", CultureInfo.InvariantCulture)).Append("s]");
            }
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static List<TemporalFrameSample> SelectEvenlySpacedWithTail(List<TemporalFrameSample> source, int targetCount)
    {
        if (source.Count <= targetCount)
            return source;

        var picked = new List<TemporalFrameSample>(targetCount);
        double step = (source.Count - 1) / (double)(targetCount - 1);
        int lastIdx = -1;
        for (int i = 0; i < targetCount; i++)
        {
            int idx = i == targetCount - 1
                ? source.Count - 1
                : Math.Clamp((int)Math.Round(i * step), lastIdx + 1, source.Count - 1 - (targetCount - 1 - i));
            picked.Add(source[idx]);
            lastIdx = idx;
        }
        return picked;
    }

    public void Clear()
    {
        lock (_sync)
            _frames.Clear();
    }
}
