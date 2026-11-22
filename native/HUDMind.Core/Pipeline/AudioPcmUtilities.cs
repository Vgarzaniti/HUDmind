namespace HUDMind.Core.Pipeline;

/// <summary>
/// Shared zero-allocation 16-bit mono PCM audio DSP utilities (RMS measurement &amp; linear upsampling).
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

    public static byte[] Resample16kTo24k(ReadOnlySpan<byte> pcm16k)
    {
        int inSamples = pcm16k.Length / 2;
        if (inSamples == 0) return [];
        int outSamples = (inSamples * 3) / 2;
        var outBuf = new byte[outSamples * 2];

        for (int i = 0; i < outSamples; i++)
        {
            double srcPos = (i * 2.0) / 3.0;
            int idx0 = Math.Min((int)srcPos, inSamples - 1);
            int idx1 = Math.Min(idx0 + 1, inSamples - 1);
            double frac = srcPos - idx0;

            short s0 = (short)(pcm16k[idx0 * 2] | (pcm16k[idx0 * 2 + 1] << 8));
            short s1 = (short)(pcm16k[idx1 * 2] | (pcm16k[idx1 * 2 + 1] << 8));
            short interp = (short)Math.Clamp((int)Math.Round(s0 + (s1 - s0) * frac), short.MinValue, short.MaxValue);
            outBuf[i * 2] = (byte)(interp & 0xFF);
            outBuf[i * 2 + 1] = (byte)((interp >> 8) & 0xFF);
        }
        return outBuf;
    }
}
