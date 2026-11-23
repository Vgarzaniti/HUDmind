using System.Buffers.Binary;

namespace HUDMind.Core.Pipeline;

/// <summary>
/// Shared 16-bit mono PCM audio DSP utilities (RMS measurement, Catmull-Rom 4-tap resampling &amp; anti-click edge fades).
/// </summary>
public static class AudioPcmUtilities
{
    public static double ComputeRms16(ReadOnlySpan<byte> pcm16)
    {
        int samples = pcm16.Length / 2;
        if (samples == 0) return 0.0;
        double sumSq = 0.0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            sumSq += s * (double)s;
        }
        return Math.Sqrt(sumSq / samples);
    }

    public static byte[] Resample16kTo24k(ReadOnlySpan<byte> pcm16k) =>
        ResampleMonoPcm16(pcm16k, channels: 1, srcRate: 16000, dstRate: 24000);

    /// <summary>
    /// Resamples 16-bit signed PCM (1 or 2 channels) into mono at <paramref name="dstRate"/> using
    /// 4-tap Catmull-Rom cubic interpolation for cleaner speech clarity than 2-point linear interpolation.
    /// </summary>
    public static byte[] ResampleMonoPcm16(ReadOnlySpan<byte> pcmBytes, int channels, uint srcRate, uint dstRate)
    {
        if (channels is < 1 or > 2 || srcRate == 0 || dstRate == 0)
            return [];

        int frameBytes = channels * 2;
        int srcFrames = pcmBytes.Length / frameBytes;
        if (srcFrames <= 0)
            return [];

        if (channels == 1 && srcRate == dstRate)
        {
            var exact = new byte[srcFrames * 2];
            pcmBytes[..exact.Length].CopyTo(exact);
            return exact;
        }

        int dstFrames = Math.Max(1, (int)((long)srcFrames * dstRate / srcRate));
        var dst = new byte[dstFrames * 2];
        double ratio = (double)srcRate / dstRate;

        for (int i = 0; i < dstFrames; i++)
        {
            double srcPos = i * ratio;
            int idx1 = Math.Clamp((int)srcPos, 0, srcFrames - 1);
            double t = srcPos - idx1;

            double y0 = ReadMonoSample(pcmBytes, Math.Max(0, idx1 - 1), channels);
            double y1 = ReadMonoSample(pcmBytes, idx1, channels);
            double y2 = ReadMonoSample(pcmBytes, Math.Min(srcFrames - 1, idx1 + 1), channels);
            double y3 = ReadMonoSample(pcmBytes, Math.Min(srcFrames - 1, idx1 + 2), channels);

            double t2 = t * t;
            double t3 = t2 * t;
            double sample = 0.5 * (
                (2.0 * y1) +
                (-y0 + y2) * t +
                (2.0 * y0 - 5.0 * y1 + 4.0 * y2 - y3) * t2 +
                (-y0 + 3.0 * y1 - 3.0 * y2 + y3) * t3);

            short clamped = (short)Math.Clamp((int)Math.Round(sample), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(dst.AsSpan(i * 2, 2), clamped);
        }

        return dst;
    }

    public static void ApplyFadeIn16(Span<byte> pcm16, int fadeSamples = 120)
    {
        int totalSamples = pcm16.Length / 2;
        int n = Math.Min(fadeSamples, totalSamples);
        if (n <= 1) return;

        for (int i = 0; i < n; i++)
        {
            double gain = i / (double)n;
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2));
            short scaled = (short)Math.Clamp((int)Math.Round(s * gain), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.Slice(i * 2, 2), scaled);
        }
    }

    public static void ApplyFadeOut16(Span<byte> pcm16, int fadeSamples = 96)
    {
        int totalSamples = pcm16.Length / 2;
        int n = Math.Min(fadeSamples, totalSamples);
        if (n <= 1) return;

        int start = totalSamples - n;
        for (int k = 0; k < n; k++)
        {
            double gain = (n - 1 - k) / (double)n;
            int idx = (start + k) * 2;
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(idx, 2));
            short scaled = (short)Math.Clamp((int)Math.Round(s * gain), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.Slice(idx, 2), scaled);
        }
    }

    private static short ReadMonoSample(ReadOnlySpan<byte> pcmBytes, int frameIndex, int channels)
    {
        int off = frameIndex * channels * 2;
        short left = BinaryPrimitives.ReadInt16LittleEndian(pcmBytes.Slice(off, 2));
        if (channels == 1)
            return left;
        short right = BinaryPrimitives.ReadInt16LittleEndian(pcmBytes.Slice(off + 2, 2));
        return (short)((left + right) / 2);
    }
}
