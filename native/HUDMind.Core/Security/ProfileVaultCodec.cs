using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HUDMind.Core.Security;

/// <summary>
/// Zero-NuGet authenticated Brotli + AES-256-GCM vault codec shared identically between
/// the build-time <c>HUDMind.Packer</c> tool and runtime <c>ProfileVaultReader</c>.
/// Eliminates plaintext embedded <c>.md</c> profiles, <c>.json</c> manifests, and prompt templates
/// from compiled assemblies.
/// </summary>
public static class ProfileVaultCodec
{
    private static readonly byte[] Magic = "HMV2"u8.ToArray();
    private static readonly byte[] AssociatedData = "HUDMind.Vault.v2"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // Split obfuscated key shards (no readable ASCII secret string in IL/binary)
    private static readonly byte[] ShardA = [0x9A, 0x3F, 0xC1, 0x54, 0x7E, 0x08, 0xD3, 0x62, 0xB9, 0x41, 0xE7, 0x19, 0x85, 0x2C, 0xFA, 0x0E];
    private static readonly byte[] ShardB = [0x4D, 0xE2, 0x18, 0xB7, 0x29, 0x91, 0x5C, 0xA4, 0x60, 0xDF, 0x33, 0x8B, 0x74, 0x12, 0xC6, 0x59];

    public static byte[] Seal(IReadOnlyDictionary<string, byte[]> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        byte[] plainArchive;
        using (var rawMs = new MemoryStream(64 * 1024))
        {
            Span<byte> lenBuf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, entries.Count);
            rawMs.Write(lenBuf);

            foreach (var (key, data) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                BinaryPrimitives.WriteInt32LittleEndian(lenBuf, keyBytes.Length);
                rawMs.Write(lenBuf);
                rawMs.Write(keyBytes);

                BinaryPrimitives.WriteInt32LittleEndian(lenBuf, data.Length);
                rawMs.Write(lenBuf);
                rawMs.Write(data);
            }
            plainArchive = rawMs.ToArray();
        }

        byte[] compressed;
        using (var compMs = new MemoryStream(plainArchive.Length / 2 + 128))
        {
            using (var brotli = new BrotliStream(compMs, CompressionLevel.SmallestSize, leaveOpen: true))
                brotli.Write(plainArchive, 0, plainArchive.Length);
            compressed = compMs.ToArray();
        }
        CryptographicOperations.ZeroMemory(plainArchive);

        Span<byte> nonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] cipher = new byte[compressed.Length];
        Span<byte> tag = stackalloc byte[TagSize];
        byte[] derivedKey = GC.AllocateUninitializedArray<byte>(32);

        try
        {
            DeriveKey(derivedKey);
            using var aes = new AesGcm(derivedKey, TagSize);
            aes.Encrypt(nonce, compressed, cipher, tag, AssociatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(compressed);
        }

        var output = new byte[Magic.Length + NonceSize + TagSize + cipher.Length];
        int pos = 0;
        Buffer.BlockCopy(Magic, 0, output, pos, Magic.Length); pos += Magic.Length;
        nonce.CopyTo(output.AsSpan(pos, NonceSize)); pos += NonceSize;
        tag.CopyTo(output.AsSpan(pos, TagSize)); pos += TagSize;
        Buffer.BlockCopy(cipher, 0, output, pos, cipher.Length);
        return output;
    }

    public static Dictionary<string, byte[]> Unseal(ReadOnlySpan<byte> blob)
    {
        int headerSize = Magic.Length + NonceSize + TagSize;
        if (blob.Length <= headerSize || !blob[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Invalid vault format.");

        var nonce = blob.Slice(Magic.Length, NonceSize);
        var tag = blob.Slice(Magic.Length + NonceSize, TagSize);
        var cipher = blob[headerSize..];

        byte[] compressed = new byte[cipher.Length];
        byte[] derivedKey = GC.AllocateUninitializedArray<byte>(32);
        byte[] plainArchive;

        try
        {
            DeriveKey(derivedKey);
            using var aes = new AesGcm(derivedKey, TagSize);
            aes.Decrypt(nonce, cipher, tag, compressed, AssociatedData);

            using var compMs = new MemoryStream(compressed, writable: false);
            using var brotli = new BrotliStream(compMs, CompressionMode.Decompress);
            using var outMs = new MemoryStream(compressed.Length * 3);
            brotli.CopyTo(outMs);
            plainArchive = outMs.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(compressed);
        }

        try
        {
            return ParseArchive(plainArchive);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainArchive);
        }
    }

    private static Dictionary<string, byte[]> ParseArchive(ReadOnlySpan<byte> archive)
    {
        if (archive.Length < 4)
            throw new InvalidDataException("Corrupt vault archive.");

        int count = BinaryPrimitives.ReadInt32LittleEndian(archive[..4]);
        if (count < 0 || count > 4096)
            throw new InvalidDataException("Invalid vault entry count.");

        int pos = 4;
        var dict = new Dictionary<string, byte[]>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            if (pos + 4 > archive.Length)
                throw new InvalidDataException("Truncated vault entry key length.");
            int keyLen = BinaryPrimitives.ReadInt32LittleEndian(archive.Slice(pos, 4));
            pos += 4;
            if (keyLen < 1 || pos + keyLen + 4 > archive.Length)
                throw new InvalidDataException("Invalid vault key bounds.");

            string key = Encoding.UTF8.GetString(archive.Slice(pos, keyLen));
            pos += keyLen;

            int dataLen = BinaryPrimitives.ReadInt32LittleEndian(archive.Slice(pos, 4));
            pos += 4;
            if (dataLen < 0 || pos + dataLen > archive.Length)
                throw new InvalidDataException("Invalid vault data bounds.");

            dict[key] = archive.Slice(pos, dataLen).ToArray();
            pos += dataLen;
        }
        return dict;
    }

    private static void DeriveKey(Span<byte> key32)
    {
        Span<byte> seed = stackalloc byte[48];
        try
        {
            for (int i = 0; i < 16; i++)
            {
                byte a = (byte)(ShardA[i] ^ (0x5A + i * 7));
                byte b = (byte)((ShardB[15 - i] + (i * 13)) ^ 0xA3);
                seed[i] = a;
                seed[16 + i] = b;
                seed[32 + i] = (byte)(a ^ b ^ (0x3D + i));
            }
            SHA256.HashData(seed, key32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }
}
