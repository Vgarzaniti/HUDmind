using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HUDMind.Core.Security;

public sealed record ProfileArchiveMetadata(
    string DisplayName,
    string AvatarBadge,
    string AccentHex,
    string FavoriteGameId);

public sealed record ProfileArchiveImportResult(
    int FilesWritten,
    ProfileArchiveMetadata? Metadata,
    bool ImportedApiAccounts);

/// <summary>
/// Portable AES-256-GCM + PBKDF2-SHA256 (.hudmind) personal profile exporter/importer
/// so players can transfer their settings, custom rules, coach personality, and leak/match history
/// across PCs cleanly with $0 server deployment.
/// </summary>
public static class ProfileArchiveCodec
{
    private const string ProfileMetadataEntryKey = "config/profile_meta.json";
    private static readonly byte[] Magic = "HMP1"u8.ToArray();
    private static readonly byte[] Aad = "HUDMind.PersonalBundle.v1"u8.ToArray();
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 120_000;
    private const int MaxEntries = 256;
    private const int MaxUncompressedBytes = 4 * 1024 * 1024;

    public static byte[] ExportBundle(
        Config.ConfigRepository repo,
        string passphrase,
        bool includeApiAccounts = false,
        Config.UserProfileRecord? profileMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 4)
            throw new ArgumentException("La contraseña de exportación debe tener al menos 4 caracteres.", nameof(passphrase));

        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        TryAddRelativeFile(entries, "config/user_config.json", repo.UserConfigPath);
        TryAddRelativeFile(entries, "config/coach_soul.md", repo.CoachSoulPath);
        TryAddRelativeFile(entries, "config/player_profile.md", repo.PlayerProfilePath);
        TryAddRelativeFile(entries, "logs/leaks.json", Path.Combine(repo.WorkspaceRoot, "logs", "leaks.json"));

        if (profileMetadata is not null)
        {
            var meta = new ProfileArchiveMetadata(
                Config.UserProfileStore.SanitizeDisplayName(profileMetadata.DisplayName),
                Config.UserProfileStore.NormalizeAvatarBadge(profileMetadata.AvatarBadge),
                Config.UserProfileStore.NormalizeAccentHex(profileMetadata.AccentHex),
                AI.CoachingPersonaBuilder.SanitizeField(profileMetadata.FavoriteGameId, 48).ToLowerInvariant());
            entries[ProfileMetadataEntryKey] = JsonSerializer.SerializeToUtf8Bytes(meta);
        }

        if (includeApiAccounts)
            TryAddRelativeFile(entries, "config/accounts.json", repo.AccountsPath);

        if (Directory.Exists(repo.ProfilesDir) && (File.GetAttributes(repo.ProfilesDir) & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var dir in Directory.GetDirectories(repo.ProfilesDir))
            {
                var gameId = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(gameId)) continue;
                TryAddRelativeFile(entries, $"profiles/{gameId}/custom_rules.md", Path.Combine(dir, "custom_rules.md"));
            }
        }

        if (Directory.Exists(repo.MemoryDir) && (File.GetAttributes(repo.MemoryDir) & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var file in Directory.GetFiles(repo.MemoryDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                var fname = Path.GetFileName(file);
                if (!string.IsNullOrWhiteSpace(fname))
                    TryAddRelativeFile(entries, $"logs/memory/{fname}", file);
            }
        }

        byte[] plainArchive = SerializeEntries(entries);
        byte[] compressed;
        using (var ms = new MemoryStream(plainArchive.Length / 2 + 64))
        {
            using (var brotli = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                brotli.Write(plainArchive, 0, plainArchive.Length);
            compressed = ms.ToArray();
        }
        CryptographicOperations.ZeroMemory(plainArchive);

        Span<byte> salt = stackalloc byte[SaltSize];
        Span<byte> nonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            32);

        byte[] cipher = new byte[compressed.Length];
        Span<byte> tag = stackalloc byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, compressed, cipher, tag, Aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(compressed);
        }

        var output = new byte[Magic.Length + SaltSize + NonceSize + TagSize + cipher.Length];
        int pos = 0;
        Buffer.BlockCopy(Magic, 0, output, pos, Magic.Length); pos += Magic.Length;
        salt.CopyTo(output.AsSpan(pos, SaltSize)); pos += SaltSize;
        nonce.CopyTo(output.AsSpan(pos, NonceSize)); pos += NonceSize;
        tag.CopyTo(output.AsSpan(pos, TagSize)); pos += TagSize;
        Buffer.BlockCopy(cipher, 0, output, pos, cipher.Length);
        return output;
    }

    public static int ImportBundle(
        ReadOnlySpan<byte> bundleBytes,
        Config.ConfigRepository destRepo,
        string passphrase,
        bool importApiAccounts = false) =>
        ImportBundleDetailed(bundleBytes, destRepo, passphrase, importApiAccounts).FilesWritten;

    public static ProfileArchiveImportResult ImportBundleDetailed(
        ReadOnlySpan<byte> bundleBytes,
        Config.ConfigRepository destRepo,
        string passphrase,
        bool importApiAccounts = false)
    {
        ArgumentNullException.ThrowIfNull(destRepo);
        int headerSize = Magic.Length + SaltSize + NonceSize + TagSize;
        if (bundleBytes.Length <= headerSize || !bundleBytes[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("El archivo .hudmind no tiene un formato válido.");

        var salt = bundleBytes.Slice(Magic.Length, SaltSize);
        var nonce = bundleBytes.Slice(Magic.Length + SaltSize, NonceSize);
        var tag = bundleBytes.Slice(Magic.Length + SaltSize + NonceSize, TagSize);
        var cipher = bundleBytes[headerSize..];

        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase ?? string.Empty),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            32);

        byte[] compressed = new byte[cipher.Length];
        byte[] plainArchive;
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, compressed, Aad);

            using var compMs = new MemoryStream(compressed, writable: false);
            using var brotli = new BrotliStream(compMs, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            byte[] chunk = new byte[8192];
            int total = 0;
            int read;
            while ((read = brotli.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxUncompressedBytes)
                    throw new InvalidDataException("El archivo .hudmind excede el límite seguro de tamaño.");
                outMs.Write(chunk, 0, read);
            }
            plainArchive = outMs.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(compressed);
        }

        try
        {
            var entries = DeserializeEntries(plainArchive);
            var fullRoot = Path.GetFullPath(destRepo.WorkspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(fullRoot);

            int written = 0;
            bool importedAccounts = false;
            ProfileArchiveMetadata? metadata = null;

            foreach (var (relKey, data) in entries)
            {
                if (string.Equals(relKey, ProfileMetadataEntryKey, StringComparison.Ordinal))
                {
                    metadata = TryParseMetadataSafe(data);
                    continue;
                }

                if (!IsAllowedRelativeRestorePath(relKey, importApiAccounts))
                    continue;

                var destPath = Path.GetFullPath(Path.Combine(fullRoot, relKey.Replace('/', Path.DirectorySeparatorChar)));
                if (!destPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                var parentDir = Path.GetDirectoryName(destPath)!;
                Directory.CreateDirectory(parentDir);
                if (File.Exists(destPath) && (File.GetAttributes(destPath) & FileAttributes.ReparsePoint) != 0)
                    continue;

                var tmp = $"{destPath}.{Guid.NewGuid():N}.tmp";
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, destPath, overwrite: true);
                written++;
                if (string.Equals(relKey, "config/accounts.json", StringComparison.Ordinal))
                    importedAccounts = true;
            }
            return new ProfileArchiveImportResult(written, metadata, importedAccounts);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainArchive);
        }
    }

    private static ProfileArchiveMetadata? TryParseMetadataSafe(byte[] data)
    {
        if (data.Length == 0 || data.Length > 4096)
            return null;
        try
        {
            var raw = JsonSerializer.Deserialize<ProfileArchiveMetadata>(data);
            if (raw is null)
                return null;
            return new ProfileArchiveMetadata(
                Config.UserProfileStore.SanitizeDisplayName(raw.DisplayName),
                Config.UserProfileStore.NormalizeAvatarBadge(raw.AvatarBadge),
                Config.UserProfileStore.NormalizeAccentHex(raw.AccentHex),
                AI.CoachingPersonaBuilder.SanitizeField(raw.FavoriteGameId, 48).ToLowerInvariant());
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAllowedRelativeRestorePath(string relKey, bool allowAccounts)
    {
        if (string.IsNullOrWhiteSpace(relKey) || relKey.Contains("..", StringComparison.Ordinal) || relKey.Contains('\\'))
            return false;

        if (relKey is "config/user_config.json" or "config/coach_soul.md" or "config/player_profile.md" or "logs/leaks.json")
            return true;

        if (relKey == "config/accounts.json")
            return allowAccounts;

        var parts = relKey.Split('/');
        if (parts.Length == 3 && parts[0] == "profiles" && parts[2] == "custom_rules.md" && IsSafeId(parts[1]))
            return true;

        if (parts.Length == 3 && parts[0] == "logs" && parts[1] == "memory" &&
            parts[2].EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            IsSafeId(parts[2][..^5]))
        {
            return true;
        }

        return false;
    }

    private static bool IsSafeId(string s) =>
        s.Length is >= 1 and <= 64 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void TryAddRelativeFile(Dictionary<string, byte[]> entries, string relKey, string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) return;
            var info = new FileInfo(fullPath);
            if (info.Length > 512 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return;
            entries[relKey] = File.ReadAllBytes(fullPath);
        }
        catch { }
    }

    private static byte[] SerializeEntries(Dictionary<string, byte[]> entries)
    {
        using var ms = new MemoryStream(4096);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, entries.Count);
        ms.Write(len);
        foreach (var (k, v) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            byte[] kb = Encoding.UTF8.GetBytes(k);
            BinaryPrimitives.WriteInt32LittleEndian(len, kb.Length);
            ms.Write(len);
            ms.Write(kb);
            BinaryPrimitives.WriteInt32LittleEndian(len, v.Length);
            ms.Write(len);
            ms.Write(v);
        }
        return ms.ToArray();
    }

    private static Dictionary<string, byte[]> DeserializeEntries(byte[] data)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int pos = 0;
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;
        if (count < 0 || count > MaxEntries)
            throw new InvalidDataException("Conteo inválido en paquete .hudmind.");

        for (int i = 0; i < count; i++)
        {
            int kLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4)); pos += 4;
            if (kLen <= 0 || kLen > 256) throw new InvalidDataException("Clave inválida.");
            string key = Encoding.UTF8.GetString(data, pos, kLen); pos += kLen;

            int vLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4)); pos += 4;
            if (vLen < 0 || pos + vLen > data.Length) throw new InvalidDataException("Tamaño inválido.");
            byte[] val = new byte[vLen];
            Buffer.BlockCopy(data, pos, val, 0, vLen);
            pos += vLen;
            result[key] = val;
        }
        return result;
    }
}
