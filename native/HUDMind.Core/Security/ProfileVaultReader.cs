using System.Security.Cryptography;
using System.Text;

namespace HUDMind.Core.Security;

/// <summary>
/// Decrypts &amp; verifies the embedded <c>HUDMind.Vault.bin</c> archive into RAM via AES-256-GCM
/// authentication, ensuring zero plaintext profile <c>.md</c> files, manifests, or protected prompt/guard
/// templates sit readable on disk inside the binary.
/// </summary>
public static class ProfileVaultReader
{
    public const string ResourceLogicalName = "HUDMind.Vault.bin";
    public const string DefaultProfilePrefix = "HUDMind.DefaultProfiles/";
    private const string StringAssetPrefix = "ProtectedStrings/";

    private static readonly object LoadLock = new();
    private static Func<bool>? _externalIntegrityViolationProbe;
    private static IReadOnlyDictionary<string, string>? _cachedEntries;
    private static bool _integrityVerified = true;

    public static bool IsVaultIntegrityHealthy => _integrityVerified;

    public static void RegisterRuntimeIntegrityProbe(Func<bool> isViolationActive) =>
        _externalIntegrityViolationProbe = isViolationActive;

    public static IReadOnlyDictionary<string, string> GetDefaultProfileEntries()
    {
        var all = EnsureLoaded();
        if (all.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in all)
        {
            if (k.StartsWith(DefaultProfilePrefix, StringComparison.OrdinalIgnoreCase))
                result[k[DefaultProfilePrefix.Length..]] = v;
        }
        return result;
    }

    public static string ReadDefaultProfileText(string gameId, string fileName, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        var key = $"{DefaultProfilePrefix}{gameId.Trim().ToLowerInvariant()}/{fileName.Trim()}";
        if (!EnsureLoaded().TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw.Trim();
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    public static string ReadProtectedString(string name, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var key = $"{StringAssetPrefix}{name.Trim()}";
        return EnsureLoaded().TryGetValue(key, out var val) && !string.IsNullOrEmpty(val)
            ? val
            : fallback;
    }

    internal static IReadOnlyDictionary<string, string> EnsureLoaded()
    {
        var existing = Volatile.Read(ref _cachedEntries);
        if (existing is not null)
            return existing;

        lock (LoadLock)
        {
            if (_cachedEntries is not null)
                return _cachedEntries;

            if (_externalIntegrityViolationProbe is not null)
            {
                try
                {
                    if (_externalIntegrityViolationProbe())
                    {
                        _integrityVerified = false;
                        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch { }
            }

            try
            {
                var asm = typeof(ProfileVaultReader).Assembly;
                using var stream = asm.GetManifestResourceStream(ResourceLogicalName);
                if (stream is null)
                {
                    _integrityVerified = false;
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                using var ms = new MemoryStream(checked((int)Math.Min(stream.Length, 8 * 1024 * 1024)));
                stream.CopyTo(ms);
                var rawBytes = ProfileVaultCodec.Unseal(ms.GetBuffer().AsSpan(0, (int)ms.Length));
                var textMap = new Dictionary<string, string>(rawBytes.Count, StringComparer.OrdinalIgnoreCase);

                foreach (var (k, bytes) in rawBytes)
                {
                    textMap[k] = Encoding.UTF8.GetString(bytes);
                    CryptographicOperations.ZeroMemory(bytes);
                }

                Volatile.Write(ref _cachedEntries, textMap);
                _integrityVerified = true;
                return textMap;
            }
            catch (CryptographicException)
            {
                _integrityVerified = false;
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                _integrityVerified = false;
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
