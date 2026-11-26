using System.Globalization;
using System.Text;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

/// <summary>
/// OBS-style dual-tier continuous Replay Buffer in RAM (0% GPU):
/// 1) High-density micro burst ring (~10 FPS, ~95ms spacing) capturing every rapid mechanic in the last ~2.5-3s.
/// 2) Long-horizon macro keyframe timeline (~1.1 FPS, ~25s OBS lookback) continuously recording even while silent,
/// paired with real-time dHash motion energy tracking so the coach sees everything and predicts ahead proactively.
/// </summary>
public sealed class RecentFrameRingBuffer
{
    private readonly object _sync = new();
    private readonly List<TemporalFrameSample> _frames = [];
    private readonly List<TemporalFrameSample> _macroFrames = [];
    private readonly int _maxFrames;
    private readonly double _minSpacingMs;
    private readonly int _macroMaxFrames;
    private readonly double _macroMinSpacingMs;

    public RecentFrameRingBuffer(
        int maxFrames = 24,
        double minSpacingMs = 95.0,
        int macroMaxFrames = 25,
        double macroMinSpacingMs = 900.0)
    {
        _maxFrames = Math.Clamp(maxFrames, 2, 48);
        _minSpacingMs = Math.Clamp(minSpacingMs, 50.0, 1500.0);
        _macroMaxFrames = Math.Clamp(macroMaxFrames, 4, 40);
        _macroMinSpacingMs = Math.Clamp(macroMinSpacingMs, 500.0, 3000.0);
    }

    public int Count
    {
        get { lock (_sync) return _frames.Count; }
    }

    public int MacroCount
    {
        get { lock (_sync) return _macroFrames.Count; }
    }

    public TemporalFrameSample? LatestSample
    {
        get
        {
            lock (_sync)
            {
                if (_frames.Count > 0) return _frames[^1];
                if (_macroFrames.Count > 0) return _macroFrames[^1];
                return null;
            }
        }
    }

    public bool NeedsMacroKeyframe(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            if (_macroFrames.Count == 0)
                return true;
            return (nowUtc - _macroFrames[^1].CapturedAtUtc).TotalMilliseconds >= _macroMinSpacingMs;
        }
    }

    /// <summary>
    /// Normalized recent visual motion intensity (0.0 = static/stalled/single-jump screen, 1.0 = rapid action/camera swing)
    /// computed strictly from in-RAM 64-bit dHash deltas across the last ~2.1s (&lt; 0.1ms CPU, 0% GPU).
    /// Requires &gt;= 2 real transitions and decays cleanly to 0 when capture has stalled.
    /// </summary>
    public double ComputeRecentMotionEnergy(double windowSeconds = 2.1, DateTimeOffset? utcNow = null)
    {
        lock (_sync)
        {
            if (_frames.Count < 3)
                return 0.0;

            var now = utcNow ?? DateTimeOffset.UtcNow;
            var newest = _frames[^1].CapturedAtUtc;
            if ((now - newest).TotalSeconds > Math.Max(2.5, windowSeconds * 1.5))
                return 0.0;

            var cutoff = newest.AddSeconds(-Math.Max(0.4, windowSeconds) - 0.05);
            double sumNormalized = 0.0;
            int pairs = 0;

            for (int i = _frames.Count - 1; i >= 1; i--)
            {
                if (_frames[i - 1].CapturedAtUtc < cutoff)
                    break;
                int dist = PerceptualHasher.HammingDistance(_frames[i].PerceptualHash, _frames[i - 1].PerceptualHash);
                sumNormalized += Math.Clamp(dist / 20.0, 0.0, 1.0);
                pairs++;
            }

            return pairs < 2 ? 0.0 : sumNormalized / pairs;
        }
    }

    public void Record(CapturedFrameData frame, bool forceReplaceTailIfTooClose = false)
    {
        if (frame.JpegBytes is not { Length: > 0 })
            return;

        var sample = new TemporalFrameSample(frame.JpegBytes, frame.CapturedAtUtc, frame.PerceptualHash);
        lock (_sync)
        {
            // Continuously append macro keyframes (~1 FPS OBS lookback) regardless of micro burst window
            if (_macroFrames.Count == 0 ||
                (frame.CapturedAtUtc - _macroFrames[^1].CapturedAtUtc).TotalMilliseconds >= _macroMinSpacingMs)
            {
                _macroFrames.Add(sample);
                while (_macroFrames.Count > _macroMaxFrames)
                    _macroFrames.RemoveAt(0);
            }

            if (_frames.Count > 0)
            {
                var last = _frames[^1];
                double elapsedMs = (frame.CapturedAtUtc - last.CapturedAtUtc).TotalMilliseconds;
                if (elapsedMs < 0)
                    return; // Ignore older frame arriving out of timestamp order
                if (elapsedMs < _minSpacingMs)
                {
                    if (forceReplaceTailIfTooClose)
                        _frames[^1] = sample;
                    return;
                }
            }

            _frames.Add(sample);
            while (_frames.Count > _maxFrames)
                _frames.RemoveAt(0);
        }
    }

    /// <summary>
    /// Returns evenly distributed chronological frames within <paramref name="maxAgeSeconds"/> (oldest -> newest).
    /// Windows &gt; 3.5s blend OBS macro keyframes (~25s lookback) with micro burst frames for complete silent-play awareness.
    /// </summary>
    public List<TemporalFrameSample> SnapshotRecent(DateTimeOffset nowUtc, double maxAgeSeconds = 5.0, int maxCount = 6)
    {
        lock (_sync)
        {
            if (_frames.Count == 0 && _macroFrames.Count == 0)
                return [];

            var minUtc = nowUtc.AddSeconds(-Math.Max(0.5, maxAgeSeconds));
            if (maxAgeSeconds <= 3.5)
            {
                var microOnly = _frames.Where(f => f.CapturedAtUtc >= minUtc).ToList();
                return SelectEvenlySpacedWithTail(microOnly, Math.Clamp(maxCount, 1, _maxFrames));
            }

            var merged = new List<TemporalFrameSample>(_macroFrames.Count + _frames.Count);
            foreach (var f in _frames)
            {
                if (f.CapturedAtUtc >= minUtc)
                    merged.Add(f);
            }

            foreach (var m in _macroFrames)
            {
                if (m.CapturedAtUtc < minUtc)
                    continue;
                bool dupNear = merged.Any(existing =>
                    Math.Abs((existing.CapturedAtUtc - m.CapturedAtUtc).TotalMilliseconds) < 85.0);
                if (!dupNear)
                    merged.Add(m);
            }

            merged.Sort(static (a, b) => a.CapturedAtUtc.CompareTo(b.CapturedAtUtc));
            return SelectEvenlySpacedWithTail(merged, Math.Clamp(maxCount, 1, Math.Max(_maxFrames, _macroMaxFrames)));
        }
    }

    /// <summary>
    /// Builds a visual clip payload from the latest OBS-buffer sample (or explicitly passed frame),
    /// combining macro context and micro trajectory keyframes cleanly.
    /// </summary>
    public TemporalVisualPayload BuildVisualClipPayload(
        ITemporalStoryboardComposer? composer,
        CapturedFrameData? latestFrame,
        int maxFrames,
        double maxAgeSeconds,
        int jpegQuality,
        bool isEn)
    {
        if (latestFrame is { JpegBytes.Length: > 0 })
            Record(latestFrame, forceReplaceTailIfTooClose: true);

        var anchorSample = latestFrame is { JpegBytes.Length: > 0 }
            ? new TemporalFrameSample(latestFrame.JpegBytes, latestFrame.CapturedAtUtc, latestFrame.PerceptualHash)
            : LatestSample;

        if (anchorSample is null || anchorSample.JpegBytes is not { Length: > 0 })
            return new TemporalVisualPayload([], null, 0, string.Empty);

        int boundedMax = Math.Clamp(maxFrames, 1, 6);
        var seq = boundedMax == 1
            ? [anchorSample]
            : SnapshotRecent(anchorSample.CapturedAtUtc, maxAgeSeconds, boundedMax);

        if (seq.Count == 0)
            seq = [anchorSample];

        if (seq.Count == 1)
            return new TemporalVisualPayload(seq, anchorSample.JpegBytes, 1, string.Empty);

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
            ? $"[OBS Replay clip: {frames.Count} chronological frames "
            : $"[Clip continuo OBS: {frames.Count} cuadros cronológicos ");
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
        if (targetCount <= 1)
            return [source[^1]];

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
        {
            _frames.Clear();
            _macroFrames.Clear();
        }
    }
}
