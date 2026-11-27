using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HUDMind.Core.Config;

public sealed class UserProfileRecord
{
    [JsonPropertyName("profile_id")]
    public string ProfileId { get; set; } = "default";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "Jugador Principal";

    [JsonPropertyName("avatar_badge")]
    public string AvatarBadge { get; set; } = "🎮";

    [JsonPropertyName("accent_hex")]
    public string AccentHex { get; set; } = "#22D3EE";

    [JsonPropertyName("favorite_game_id")]
    public string FavoriteGameId { get; set; } = "";

    [JsonPropertyName("pin_hash")]
    public string PinHash { get; set; } = string.Empty;

    [JsonPropertyName("linked_account_badge")]
    public string LinkedAccountBadge { get; set; } = string.Empty;

    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("last_seen_at_utc")]
    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool HasPin => !string.IsNullOrWhiteSpace(PinHash);
}

public sealed class UserProfileIndex
{
    [JsonPropertyName("active_profile_id")]
    public string ActiveProfileId { get; set; } = "default";

    [JsonPropertyName("profiles")]
    public List<UserProfileRecord> Profiles { get; set; } = [];
}

/// <summary>
/// Local multi-profile account registry (`users/users.json`) with CWE-22 workspace isolation,
/// optional PBKDF2-SHA256 PIN lock, and lossless upgrade from single-user installations.
/// </summary>
public sealed partial class UserProfileStore
{
    private const int Pbkdf2Iterations = 120_000;
    private const long MaxIndexBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"^[a-z0-9_\-]{1,48}$", RegexOptions.Compiled)]
    private static partial Regex SafeProfileIdRegex();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled)]
    private static partial Regex SafeHexColorRegex();

    public string BaseWorkspaceRoot { get; }
    public string UsersRootDir => Path.Combine(BaseWorkspaceRoot, "users");
    public string IndexPath => Path.Combine(UsersRootDir, "users.json");

    public UserProfileStore(string baseWorkspaceRoot)
    {
        BaseWorkspaceRoot = Path.GetFullPath(baseWorkspaceRoot);
    }

    public static bool IsValidProfileId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && SafeProfileIdRegex().IsMatch(id.Trim().ToLowerInvariant());

    public static string SanitizeDisplayName(string? name, string fallback = "Jugador")
    {
        var clean = AI.CoachingPersonaBuilder.SanitizeField(name, 32);
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }

    public static string NormalizeAccentHex(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && SafeHexColorRegex().IsMatch(hex.Trim())
            ? hex.Trim().ToUpperInvariant()
            : "#22D3EE";

    public static string NormalizeAvatarBadge(string? badge)
    {
        var t = (badge ?? string.Empty).Trim();
        return string.IsNullOrEmpty(t) || t.Length > 4 ? "🎮" : t;
    }

    public UserProfileIndex LoadOrInitialize()
    {
        UserProfileIndex index = new();
        try
        {
            if (File.Exists(IndexPath))
            {
                var info = new FileInfo(IndexPath);
                if (info.Length <= MaxIndexBytes && (info.Attributes & FileAttributes.ReparsePoint) == 0 && !HasUsersReparsePoint())
                {
                    var raw = File.ReadAllText(IndexPath, Encoding.UTF8);
                    index = JsonSerializer.Deserialize<UserProfileIndex>(raw, JsonOpts) ?? new UserProfileIndex();
                }
            }
        }
        catch
        {
            index = new UserProfileIndex();
        }

        index.Profiles ??= [];
        index.Profiles = index.Profiles
            .Where(p => p is not null && IsValidProfileId(p.ProfileId))
            .GroupBy(p => p.ProfileId.Trim().ToLowerInvariant())
            .Select(g =>
            {
                var item = g.First();
                item.ProfileId = g.Key;
                item.DisplayName = SanitizeDisplayName(item.DisplayName, g.Key == "default" ? "Jugador Principal" : g.Key);
                item.AccentHex = NormalizeAccentHex(item.AccentHex);
                item.AvatarBadge = NormalizeAvatarBadge(item.AvatarBadge);
                return item;
            })
            .ToList();

        if (!index.Profiles.Any(p => p.ProfileId == "default"))
        {
            index.Profiles.Insert(0, new UserProfileRecord
            {
                ProfileId = "default",
                DisplayName = "Jugador Principal",
                AvatarBadge = "🎮",
                AccentHex = "#22D3EE"
            });
        }

        var normActive = (index.ActiveProfileId ?? "default").Trim().ToLowerInvariant();
        if (!index.Profiles.Any(p => p.ProfileId == normActive))
            index.ActiveProfileId = index.Profiles[0].ProfileId;
        else
            index.ActiveProfileId = normActive;

        return index;
    }

    public void Save(UserProfileIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (HasUsersReparsePoint())
            throw new UnauthorizedAccessException("No se permiten enlaces simbólicos en el directorio users.");

        Directory.CreateDirectory(UsersRootDir);
        var json = JsonSerializer.Serialize(index, JsonOpts);
        AtomicWrite(IndexPath, json);
    }

    public UserProfileRecord CreateProfile(
        string displayName,
        string avatarBadge = "🎮",
        string accentHex = "#22D3EE",
        string favoriteGameId = "",
        string? pin = null)
    {
        var cleanName = SanitizeDisplayName(displayName);
        var slugBase = BuildSafeSlug(cleanName);
        var index = LoadOrInitialize();

        string id = slugBase;
        int suffix = 2;
        while (index.Profiles.Any(p => p.ProfileId.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            id = $"{slugBase[..Math.Min(slugBase.Length, 40)]}_{suffix}";
            suffix++;
        }

        var record = new UserProfileRecord
        {
            ProfileId = id,
            DisplayName = cleanName,
            AvatarBadge = NormalizeAvatarBadge(avatarBadge),
            AccentHex = NormalizeAccentHex(accentHex),
            FavoriteGameId = AI.CoachingPersonaBuilder.SanitizeField(favoriteGameId, 48).ToLowerInvariant(),
            PinHash = string.IsNullOrEmpty(pin) ? string.Empty : HashPin(pin),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow
        };

        Directory.CreateDirectory(ResolveWorkspaceRoot(id));
        index.Profiles.Add(record);
        index.ActiveProfileId = id;
        Save(index);
        return record;
    }

    public UserProfileRecord SwitchActiveProfile(string profileId)
    {
        var norm = (profileId ?? string.Empty).Trim().ToLowerInvariant();
        var index = LoadOrInitialize();
        var found = index.Profiles.FirstOrDefault(p => p.ProfileId == norm)
            ?? throw new KeyNotFoundException($"Perfil no encontrado: {profileId}");

        found.LastSeenAtUtc = DateTimeOffset.UtcNow;
        index.ActiveProfileId = found.ProfileId;
        Save(index);
        return found;
    }

    public void UpdateProfileMetadata(
        string profileId,
        string? displayName = null,
        string? avatarBadge = null,
        string? accentHex = null,
        string? favoriteGameId = null,
        string? linkedBadge = null)
    {
        var norm = (profileId ?? string.Empty).Trim().ToLowerInvariant();
        var index = LoadOrInitialize();
        var found = index.Profiles.FirstOrDefault(p => p.ProfileId == norm);
        if (found is null)
            return;

        if (!string.IsNullOrWhiteSpace(displayName))
            found.DisplayName = SanitizeDisplayName(displayName, found.DisplayName);
        if (!string.IsNullOrWhiteSpace(avatarBadge))
            found.AvatarBadge = NormalizeAvatarBadge(avatarBadge);
        if (!string.IsNullOrWhiteSpace(accentHex))
            found.AccentHex = NormalizeAccentHex(accentHex);
        if (favoriteGameId is not null)
            found.FavoriteGameId = AI.CoachingPersonaBuilder.SanitizeField(favoriteGameId, 48).ToLowerInvariant();
        if (linkedBadge is not null)
            found.LinkedAccountBadge = AI.CoachingPersonaBuilder.SanitizeField(linkedBadge, 48);
        found.LastSeenAtUtc = DateTimeOffset.UtcNow;
        Save(index);
    }

    public void SetProfilePin(string profileId, string? newPin)
    {
        var norm = (profileId ?? string.Empty).Trim().ToLowerInvariant();
        var index = LoadOrInitialize();
        var found = index.Profiles.FirstOrDefault(p => p.ProfileId == norm)
            ?? throw new KeyNotFoundException($"Perfil no encontrado: {profileId}");
        found.PinHash = string.IsNullOrWhiteSpace(newPin) ? string.Empty : HashPin(newPin);
        Save(index);
    }

    public bool DeleteProfile(string profileId)
    {
        var norm = (profileId ?? string.Empty).Trim().ToLowerInvariant();
        if (norm == "default" || !IsValidProfileId(norm))
            return false;

        var index = LoadOrInitialize();
        if (string.Equals(index.ActiveProfileId, norm, StringComparison.OrdinalIgnoreCase))
            index.ActiveProfileId = "default";

        int removed = index.Profiles.RemoveAll(p => p.ProfileId == norm);
        if (removed == 0)
            return false;

        Save(index);

        var dir = Path.GetFullPath(Path.Combine(UsersRootDir, norm));
        var usersFull = Path.GetFullPath(UsersRootDir);
        if (dir.StartsWith(usersFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(dir) &&
            (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0)
        {
            Directory.Delete(dir, recursive: true);
        }
        return true;
    }

    public string ResolveWorkspaceRoot(string? profileId)
    {
        var norm = (profileId ?? "default").Trim().ToLowerInvariant();
        if (norm == "default" || !IsValidProfileId(norm))
            return BaseWorkspaceRoot;

        var fullUsers = Path.GetFullPath(UsersRootDir);
        var fullTarget = Path.GetFullPath(Path.Combine(fullUsers, norm));
        if (!fullTarget.StartsWith(fullUsers + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Ruta de perfil inválida (CWE-22).");

        return fullTarget;
    }

    public static string HashPin(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
            return string.Empty;

        Span<byte> salt = stackalloc byte[16];
        RandomNumberGenerator.Fill(salt);
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin.Trim()),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            32);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(derived)}";
    }

    public static bool VerifyPin(string? pinHash, string? candidatePin)
    {
        if (string.IsNullOrWhiteSpace(pinHash))
            return true;
        if (string.IsNullOrWhiteSpace(candidatePin))
            return false;

        var parts = pinHash.Split(':');
        if (parts.Length != 3 || parts[0] != "v1")
            return false;

        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(candidatePin.Trim()),
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            bool ok = CryptographicOperations.FixedTimeEquals(actual, expected);
            CryptographicOperations.ZeroMemory(actual);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private bool HasUsersReparsePoint()
    {
        if (Directory.Exists(BaseWorkspaceRoot) && (File.GetAttributes(BaseWorkspaceRoot) & FileAttributes.ReparsePoint) != 0)
            return true;
        if (Directory.Exists(UsersRootDir) && (File.GetAttributes(UsersRootDir) & FileAttributes.ReparsePoint) != 0)
            return true;
        return false;
    }

    private static string BuildSafeSlug(string name)
    {
        var raw = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (raw.Contains("__", StringComparison.Ordinal))
            raw = raw.Replace("__", "_", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(raw) || raw == "default")
            return "player";
        return raw.Length <= 40 ? raw : raw[..40];
    }

    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
