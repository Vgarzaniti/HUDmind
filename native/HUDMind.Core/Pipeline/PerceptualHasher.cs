using System.Numerics;

namespace HUDMind.Core.Pipeline;

/// <summary>
/// Ultra-fast 64-bit difference hash (dHash) with hardware POPCNT Hamming distance (&lt;0.1ms).
/// Expects a 9x8 grayscale luminance buffer (72 bytes).
/// </summary>
public static class PerceptualHasher
{
    public const int GridWidth = 9;
    public const int GridHeight = 8;

    public static ulong ComputeDHash64(ReadOnlySpan<byte> gray9x8)
    {
        if (gray9x8.Length < GridWidth * GridHeight)
            throw new ArgumentException("Se requieren al menos 72 bytes (9x8 escala de grises).", nameof(gray9x8));

        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < GridHeight; y++)
        {
            int row = y * GridWidth;
            for (int x = 0; x < GridWidth - 1; x++)
            {
                if (gray9x8[row + x] > gray9x8[row + x + 1])
                    hash |= 1UL << bit;
                bit++;
            }
        }
        return hash;
    }

    public static int HammingDistance(ulong a, ulong b) =>
        BitOperations.PopCount(a ^ b);

    /// <summary>
    /// Computes mean absolute luminance delta across the 72 sub-sampled HUD grid cells (&lt;0.01ms).
    /// Catches localized HUD meter / timer / menu transitions even when global gradient bits do not flip.
    /// </summary>
    public static double ComputeMeanAbsLumaDelta(ReadOnlySpan<byte> prevGray9x8, ReadOnlySpan<byte> currGray9x8)
    {
        int len = Math.Min(prevGray9x8.Length, currGray9x8.Length);
        if (len == 0)
            return 0.0;

        long sum = 0;
        for (int i = 0; i < len; i++)
            sum += Math.Abs(currGray9x8[i] - prevGray9x8[i]);
        return sum / (double)len;
    }
}
