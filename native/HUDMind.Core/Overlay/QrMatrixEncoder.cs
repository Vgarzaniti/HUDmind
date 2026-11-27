using System.Text;

namespace HUDMind.Core.Overlay;

/// <summary>
/// Zero-dependency ISO/IEC 18004 QR Code (Byte mode, Error Correction Level L, Versions 1–5)
/// matrix encoder for local LAN Companion HUD URLs (0% GPU, cross-platform net9.0).
/// </summary>
public static class QrMatrixEncoder
{
    private readonly record struct QrVersionSpec(int Version, int DataCodewords, int EcCodewords, int AlignCenter);

    // Versions 1–5 Level L each use 1 Reed-Solomon block, supporting up to 106 UTF-8 URL bytes.
    private static readonly QrVersionSpec[] Versions =
    [
        new(1, 19, 7, 0),
        new(2, 34, 10, 18),
        new(3, 55, 15, 22),
        new(4, 80, 20, 26),
        new(5, 108, 26, 30)
    ];

    private static readonly int[] GfExp = BuildGfExp();
    private static readonly int[] GfLog = BuildGfLog(GfExp);

    public const int MaxPayloadBytes = 106;

    /// <summary>
    /// Encodes <paramref name="text"/> as a square QR module matrix (<c>true</c> = dark module).
    /// Returns <c>null</c> if input is empty or exceeds <see cref="MaxPayloadBytes"/> UTF-8 bytes.
    /// </summary>
    public static bool[,]? TryEncode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        byte[] payload = Encoding.UTF8.GetBytes(text);
        if (payload.Length == 0 || payload.Length > MaxPayloadBytes)
            return null;

        QrVersionSpec spec = default;
        bool found = false;
        foreach (var candidate in Versions)
        {
            int maxBytes = (candidate.DataCodewords * 8 - 12) / 8;
            if (payload.Length <= maxBytes)
            {
                spec = candidate;
                found = true;
                break;
            }
        }
        if (!found)
            return null;

        byte[] dataCodewords = EncodeDataCodewords(payload, spec.DataCodewords);
        byte[] ecCodewords = ComputeReedSolomonEc(dataCodewords, spec.EcCodewords);

        var stream = new List<bool>((dataCodewords.Length + ecCodewords.Length) * 8);
        AppendBytesMsbFirst(stream, dataCodewords);
        AppendBytesMsbFirst(stream, ecCodewords);

        int size = 17 + 4 * spec.Version;
        var baseModules = new bool[size, size];
        var isFunction = new bool[size, size];

        DrawFunctionPatterns(baseModules, isFunction, size, spec.AlignCenter);
        DrawCodewords(baseModules, isFunction, size, stream);

        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        bool[,]? bestMatrix = null;

        for (int mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])baseModules.Clone();
            ApplyMask(candidate, isFunction, size, mask);
            DrawFormatBits(candidate, size, mask);
            int penalty = ComputePenaltyScore(candidate, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
                bestMatrix = candidate;
            }
        }

        if (bestMatrix is null)
        {
            bestMatrix = (bool[,])baseModules.Clone();
            ApplyMask(bestMatrix, isFunction, size, bestMask);
            DrawFormatBits(bestMatrix, size, bestMask);
        }

        return bestMatrix;
    }

    public static byte[] ComputeReedSolomonEc(ReadOnlySpan<byte> data, int ecCount)
    {
        byte[] gen = BuildRsGenerator(ecCount);
        byte[] rem = new byte[ecCount];

        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, ecCount - 1);
            rem[ecCount - 1] = 0;

            for (int j = 0; j < ecCount; j++)
                rem[j] ^= GfMultiply(gen[j + 1], factor);
        }

        return rem;
    }

    public static bool VerifyReedSolomonSyndrome(ReadOnlySpan<byte> data, ReadOnlySpan<byte> ec)
    {
        int ecCount = ec.Length;
        byte[] full = new byte[data.Length + ecCount];
        data.CopyTo(full);
        ec.CopyTo(full.AsSpan(data.Length));

        for (int root = 0; root < ecCount; root++)
        {
            byte alphaPow = (byte)GfExp[root];
            byte acc = 0;
            foreach (byte coef in full)
                acc = (byte)(GfMultiply(acc, alphaPow) ^ coef);
            if (acc != 0)
                return false;
        }
        return true;
    }

    private static byte[] EncodeDataCodewords(ReadOnlySpan<byte> payload, int dataCodewordCount)
    {
        int totalBits = dataCodewordCount * 8;
        var bits = new List<bool>(totalBits);

        // Mode 0100 (Byte), 8-bit character count for Versions 1–9
        AppendBits(bits, 0b0100, 4);
        AppendBits(bits, payload.Length, 8);
        foreach (byte b in payload)
            AppendBits(bits, b, 8);

        int terminator = Math.Min(4, totalBits - bits.Count);
        for (int i = 0; i < terminator; i++)
            bits.Add(false);

        while (bits.Count % 8 != 0)
            bits.Add(false);

        byte[] result = new byte[dataCodewordCount];
        int byteIdx = bits.Count / 8;
        for (int i = 0; i < byteIdx; i++)
        {
            int v = 0;
            for (int b = 0; b < 8; b++)
                v = (v << 1) | (bits[i * 8 + b] ? 1 : 0);
            result[i] = (byte)v;
        }

        byte[] pads = [0xEC, 0x11];
        for (int padIdx = 0; byteIdx < dataCodewordCount; byteIdx++, padIdx++)
            result[byteIdx] = pads[padIdx & 1];

        return result;
    }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunc, int size, int alignCenter)
    {
        DrawFinderWithSeparator(modules, isFunc, size, 0, 0);
        DrawFinderWithSeparator(modules, isFunc, size, 0, size - 7);
        DrawFinderWithSeparator(modules, isFunc, size, size - 7, 0);

        for (int i = 8; i < size - 8; i++)
        {
            bool dark = (i & 1) == 0;
            SetFunc(modules, isFunc, 6, i, dark);
            SetFunc(modules, isFunc, i, 6, dark);
        }

        if (alignCenter > 0)
        {
            for (int dr = -2; dr <= 2; dr++)
            {
                for (int dc = -2; dc <= 2; dc++)
                {
                    int dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
                    SetFunc(modules, isFunc, alignCenter + dr, alignCenter + dc, dist != 1);
                }
            }
        }

        // Fixed dark module + reserved format information slots (overwritten later per mask)
        SetFunc(modules, isFunc, size - 8, 8, true);
        for (int i = 0; i <= 8; i++)
        {
            if (i != 6)
            {
                isFunc[8, i] = true;
                isFunc[i, 8] = true;
            }
        }
        for (int i = 0; i < 8; i++)
            isFunc[8, size - 1 - i] = true;
        for (int i = 0; i < 7; i++)
            isFunc[size - 1 - i, 8] = true;
    }

    private static void DrawFinderWithSeparator(bool[,] modules, bool[,] isFunc, int size, int top, int left)
    {
        for (int dr = -1; dr <= 7; dr++)
        {
            for (int dc = -1; dc <= 7; dc++)
            {
                int r = top + dr;
                int c = left + dc;
                if ((uint)r >= (uint)size || (uint)c >= (uint)size)
                    continue;
                int dist = Math.Max(Math.Abs(dr - 3), Math.Abs(dc - 3));
                bool dark = dist != 2 && dist != 4;
                SetFunc(modules, isFunc, r, c, dark);
            }
        }
    }

    private static void DrawCodewords(bool[,] modules, bool[,] isFunc, int size, IReadOnlyList<bool> dataBits)
    {
        int bitIdx = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5;

            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int col = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int row = upward ? size - 1 - vert : vert;
                    if (!isFunc[row, col] && bitIdx < dataBits.Count)
                    {
                        modules[row, col] = dataBits[bitIdx];
                        bitIdx++;
                    }
                }
            }
        }
    }

    private static void ApplyMask(bool[,] modules, bool[,] isFunc, int size, int mask)
    {
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                if (isFunc[r, c])
                    continue;

                bool invert = mask switch
                {
                    0 => ((r + c) & 1) == 0,
                    1 => (r & 1) == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => (((r >> 1) + (c / 3)) & 1) == 0,
                    5 => ((r * c) & 1) + ((r * c) % 3) == 0,
                    6 => ((((r * c) & 1) + ((r * c) % 3)) & 1) == 0,
                    7 => ((((r + c) & 1) + ((r * c) % 3)) & 1) == 0,
                    _ => false
                };
                if (invert)
                    modules[r, c] = !modules[r, c];
            }
        }
    }

    private static void DrawFormatBits(bool[,] modules, int size, int mask)
    {
        // EC Level L indicator = 01
        int data = (0b01 << 3) | (mask & 7);
        int rem = data;
        for (int i = 0; i < 10; i++)
            rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;

        // First copy around top-left finder (bit 0 at top-right vertical end, bit 14 at left end)
        for (int i = 0; i <= 5; i++)
            modules[8, i] = ((bits >> (14 - i)) & 1) != 0;
        modules[8, 7] = ((bits >> 8) & 1) != 0;
        modules[8, 8] = ((bits >> 7) & 1) != 0;
        modules[7, 8] = ((bits >> 6) & 1) != 0;
        for (int i = 0; i <= 5; i++)
            modules[5 - i, 8] = ((bits >> (5 - i)) & 1) != 0;

        // Second copy split across bottom-left & top-right
        for (int i = 0; i < 7; i++)
            modules[size - 1 - i, 8] = ((bits >> (14 - i)) & 1) != 0;
        for (int i = 0; i < 8; i++)
            modules[8, size - 8 + i] = ((bits >> (7 - i)) & 1) != 0;
    }

    private static int ComputePenaltyScore(bool[,] modules, int size)
    {
        int penalty = 0;

        // Rule 1: adjacent runs >= 5
        for (int r = 0; r < size; r++)
        {
            int runLen = 1;
            for (int c = 1; c < size; c++)
            {
                if (modules[r, c] == modules[r, c - 1])
                    runLen++;
                else
                {
                    if (runLen >= 5) penalty += 3 + (runLen - 5);
                    runLen = 1;
                }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }

        for (int c = 0; c < size; c++)
        {
            int runLen = 1;
            for (int r = 1; r < size; r++)
            {
                if (modules[r, c] == modules[r - 1, c])
                    runLen++;
                else
                {
                    if (runLen >= 5) penalty += 3 + (runLen - 5);
                    runLen = 1;
                }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }

        // Rule 2: 2x2 solid blocks
        for (int r = 0; r < size - 1; r++)
        {
            for (int c = 0; c < size - 1; c++)
            {
                bool v = modules[r, c];
                if (v == modules[r, c + 1] && v == modules[r + 1, c] && v == modules[r + 1, c + 1])
                    penalty += 3;
            }
        }

        // Rule 3: finder-like 1:1:3:1:1 patterns
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c <= size - 11; c++)
            {
                if (IsRule3Pattern(modules, r, c, horizontal: true))
                    penalty += 40;
            }
        }
        for (int c = 0; c < size; c++)
        {
            for (int r = 0; r <= size - 11; r++)
            {
                if (IsRule3Pattern(modules, r, c, horizontal: false))
                    penalty += 40;
            }
        }

        // Rule 4: dark module balance vs 50%
        int darkCount = 0;
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
                if (modules[r, c]) darkCount++;
        int k = Math.Abs(darkCount * 20 - size * size * 10) / (size * size);
        penalty += k * 10;

        return penalty;
    }

    private static bool IsRule3Pattern(bool[,] m, int r, int c, bool horizontal)
    {
        bool At(int i) => horizontal ? m[r, c + i] : m[r + i, c];
        bool forward = At(0) && !At(1) && At(2) && At(3) && At(4) && !At(5) && At(6) && !At(7) && !At(8) && !At(9) && !At(10);
        bool backward = !At(0) && !At(1) && !At(2) && !At(3) && At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10);
        return forward || backward;
    }

    private static void SetFunc(bool[,] modules, bool[,] isFunc, int r, int c, bool dark)
    {
        modules[r, c] = dark;
        isFunc[r, c] = true;
    }

    private static void AppendBits(List<bool> list, int value, int bitCount)
    {
        for (int i = bitCount - 1; i >= 0; i--)
            list.Add(((value >> i) & 1) != 0);
    }

    private static void AppendBytesMsbFirst(List<bool> list, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            AppendBits(list, b, 8);
    }

    private static int[] BuildGfExp()
    {
        var exp = new int[512];
        int v = 1;
        for (int i = 0; i < 255; i++)
        {
            exp[i] = v;
            exp[i + 255] = v;
            v <<= 1;
            if ((v & 0x100) != 0)
                v ^= 0x11D;
        }
        exp[510] = exp[0];
        exp[511] = exp[1];
        return exp;
    }

    private static int[] BuildGfLog(int[] exp)
    {
        var log = new int[256];
        for (int i = 0; i < 255; i++)
            log[exp[i]] = i;
        return log;
    }

    private static byte GfMultiply(byte a, byte b) =>
        (a == 0 || b == 0) ? (byte)0 : (byte)GfExp[GfLog[a] + GfLog[b]];

    private static byte[] BuildRsGenerator(int ecCount)
    {
        byte[] poly = new byte[ecCount + 1];
        poly[0] = 1;
        for (int i = 0; i < ecCount; i++)
        {
            byte root = (byte)GfExp[i];
            for (int j = i + 1; j >= 1; j--)
                poly[j] = (byte)(poly[j] ^ GfMultiply(poly[j - 1], root));
        }
        return poly;
    }
}
