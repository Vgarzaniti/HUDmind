using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HUDMind.Core.Models;

namespace HUDMind.Core.Config;

public static partial class SecretRedactor
{
    [GeneratedRegex(
        @"(AIza[0-9A-Za-z_\-]{12,}|AQ\.[0-9A-Za-z_\-\.]{12,}|gsk_[0-9A-Za-z_\-]{12,}|sk-[0-9A-Za-z_\-]{12,}|hf_[0-9A-Za-z]{12,}|xai-[0-9A-Za-z_\-]{12,}|r8_[0-9A-Za-z_\-]{12,}|Bearer\s+[0-9A-Za-z_\-\.]{10,}|(?i)(?:api_?key|access_token|token|key)=[^&\s""']+)",
        RegexOptions.Compiled)]
    private static partial Regex SecretPattern();

    public static string Redact(string text, IEnumerable<string>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = SecretPattern().Replace(text, "[REDACTED]");
        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets)
            {
                if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 6)
                    result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }
        return result;
    }
}

/// <summary>
/// OWASP SSRF (CWE-918) and Cleartext Transmission (CWE-319) validator for provider endpoints.
/// </summary>
public static class BaseUrlSecurityValidator
{
    public static (bool IsValid, string Error) Validate(string url, bool allowLoopbackHttp = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, "Base URL vacía.");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return (false, "Base URL con formato inválido.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return (false, "Base URL no puede incluir credenciales embebidas (user@host).");

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "https" && scheme != "http")
            return (false, $"Esquema no permitido: '{scheme}'. Solo HTTPS.");

        var host = uri.Host;
        bool isLoopbackHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              (IPAddress.TryParse(host, out var parsedIp) && IPAddress.IsLoopback(parsedIp));

        if (scheme == "http" && !(allowLoopbackHttp && isLoopbackHost))
            return (false, "HTTP sin cifrar remoto bloqueado (CWE-319). Usá HTTPS.");

        if (IPAddress.TryParse(host, out var ip) && IsBlockedAddress(ip, allowLoopbackHttp))
            return (false, $"IP privada/link-local bloqueada por seguridad SSRF (CWE-918): {host}.");

        return (true, string.Empty);
    }

    public static bool IsBlockedAddress(IPAddress ip, bool allowLoopback)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))
            return !allowLoopback;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;                                  // 0.0.0.0/8
            if (b[0] == 10) return true;                                 // 10.0.0.0/8
            if (b[0] == 127) return !allowLoopback;                      // 127.0.0.0/8
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 (cloud metadata)
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;     // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return true;    // 100.64.0.0/10 CGNAT
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 Unique Local
            if (ip.Equals(IPAddress.IPv6Any)) return true;
        }

        return false;
    }
}

public sealed partial class ConfigRepository
{
    public const int MaxCustomRulesChars = 600;
    private const long MaxConfigFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    [GeneratedRegex(@"^[a-z0-9_\-]{1,64}$", RegexOptions.Compiled)]
    private static partial Regex SafeGameIdRegex();

    public string WorkspaceRoot { get; }
    public string ConfigDir => Path.Combine(WorkspaceRoot, "config");
    public string ProfilesDir => Path.Combine(WorkspaceRoot, "profiles");
    public string AccountsPath => Path.Combine(ConfigDir, "accounts.json");
    public string UserConfigPath => Path.Combine(ConfigDir, "user_config.json");
    public string? LastLoadWarning { get; private set; }

    public ConfigRepository(string? workspaceRoot = null)
    {
        WorkspaceRoot = workspaceRoot ?? DiscoverWorkspaceRoot();
    }

    public static string DiscoverWorkspaceRoot()
    {
        var envHome = Environment.GetEnvironmentVariable("HUDMIND_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            var fullEnv = Path.GetFullPath(envHome);
            if (!new Uri(fullEnv).IsUnc && Directory.Exists(Path.Combine(fullEnv, "profiles")))
                return fullEnv;
        }

        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var start in candidates)
        {
            var dir = new DirectoryInfo(start);
            for (int depth = 0; depth < 7 && dir is not null; depth++)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "profiles")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "config")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }

        var desktopRepo = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "CouchIA");
        if (Directory.Exists(Path.Combine(desktopRepo, "profiles")))
            return desktopRepo;

        return Directory.GetCurrentDirectory();
    }

    public UserConfig LoadUserConfig()
    {
        try
        {
            if (File.Exists(UserConfigPath))
            {
                var info = new FileInfo(UserConfigPath);
                if (info.Length <= MaxConfigFileBytes)
                {
                    var json = File.ReadAllText(UserConfigPath, Encoding.UTF8);
                    var cfg = JsonSerializer.Deserialize<UserConfig>(json, JsonOpts) ?? new UserConfig();
                    if (!json.Contains("\"communication_mode\"", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!cfg.EnableVoice)
                            cfg.SetCommunicationMode("text_only");
                        else if (!cfg.EnableMic)
                            cfg.SetCommunicationMode("coach_voice");
                        else
                            cfg.SetCommunicationMode("conversation");
                    }
                    else
                    {
                        cfg.SetCommunicationMode(cfg.CommunicationMode);
                    }
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            BackupCorruptFile(UserConfigPath);
            LastLoadWarning = $"user_config.json corrupto (se creó backup): {ex.Message}";
        }
        return new UserConfig();
    }

    public void SaveUserConfig(UserConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, JsonOpts);
        AtomicWrite(UserConfigPath, json);
    }

    public List<ProviderAccount> LoadAccounts()
    {
        try
        {
            if (File.Exists(AccountsPath))
            {
                var info = new FileInfo(AccountsPath);
                if (info.Length <= MaxConfigFileBytes * 4)
                {
                    var json = File.ReadAllText(AccountsPath, Encoding.UTF8);
                    var list = JsonSerializer.Deserialize<List<ProviderAccount>>(json, JsonOpts);
                    if (list is { Count: > 0 })
                        return [.. list.OrderBy(a => a.Priority)];
                }
            }
        }
        catch (Exception ex)
        {
            BackupCorruptFile(AccountsPath);
            LastLoadWarning = $"accounts.json ilegible (se guardó backup): {ex.Message}";
        }
        return BuildDefaultFreeCloudPool();
    }

    public void SaveAccounts(IEnumerable<ProviderAccount> accounts)
    {
        Directory.CreateDirectory(ConfigDir);
        var ordered = accounts.OrderBy(a => a.Priority).ToList();
        var json = JsonSerializer.Serialize(ordered, JsonOpts);
        AtomicWrite(AccountsPath, json);
    }

    public static List<ProviderAccount> BuildDefaultFreeCloudPool() =>
    [
        new ProviderAccount
        {
            Id = "free-pollinations-fast",
            Label = "Pollinations · OpenAI Fast (Gratis Sin Login)",
            Provider = "pollinations",
            Model = "openai-fast",
            BaseUrl = "https://text.pollinations.ai/openai",
            Priority = 901,
            Enabled = true
        },
        new ProviderAccount
        {
            Id = "free-airforce-nemo",
            Label = "Airforce Cloud · Mistral Nemo (Gratis Sin Login)",
            Provider = "airforce",
            Model = "open-mistral-nemo",
            BaseUrl = "https://api.airforce/v1",
            Priority = 902,
            Enabled = true
        }
    ];

    public List<GameProfile> LoadProfiles()
    {
        var result = new List<GameProfile>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ProfilesDir))
            return result;

        var fullProfilesRoot = Path.GetFullPath(ProfilesDir) + Path.DirectorySeparatorChar;
        foreach (var dir in Directory.GetDirectories(ProfilesDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                continue;

            var fullDir = Path.GetFullPath(dir);
            if (!fullDir.StartsWith(fullProfilesRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            var manifestPath = Path.Combine(fullDir, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var info = new FileInfo(manifestPath);
                if (info.Length > MaxConfigFileBytes)
                    continue;

                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize<GameManifest>(json, JsonOpts);
                if (manifest is null || !SafeGameIdRegex().IsMatch(manifest.GameId) || !seenIds.Add(manifest.GameId))
                    continue;

                var customRules = ReadBoundedText(Path.Combine(fullDir, "custom_rules.md"), MaxCustomRulesChars);
                var tacticalSummary = ExtractCompactTacticalSummary(
                    Path.Combine(fullDir, "system_prompt.md"),
                    manifest.Notes);

                result.Add(new GameProfile
                {
                    Manifest = manifest,
                    DirectoryPath = fullDir,
                    CustomRules = customRules,
                    TacticalSummary = tacticalSummary
                });
            }
            catch
            {
                // Skip invalid profile directories safely
            }
        }

        return result;
    }

    public static GameProfile CreateUniversalProfile(string profilesRoot) =>
        new()
        {
            DirectoryPath = profilesRoot,
            Manifest = new GameManifest
            {
                GameId = "universal",
                DisplayName = "Cualquier Juego (Universal)",
                ProcessNames = [],
                CaptureFps = 1.5,
                PhashThreshold = 7,
                CooldownSeconds = 8.0,
                MaxResolution = [1280, 720],
                Language = "es",
                Notes = "Coach universal genérico que detecta fase (menú, replay, saque, entrenamiento o partida) en cualquier videojuego."
            }
        };

    public void SaveCustomRules(GameProfile profile, string rulesText)
    {
        var trimmed = (rulesText ?? string.Empty).Trim();
        if (trimmed.Length > MaxCustomRulesChars)
            throw new ArgumentException($"Las reglas personalizadas no pueden superar {MaxCustomRulesChars} caracteres.");

        var fullProfilesRoot = Path.GetFullPath(ProfilesDir) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(Path.Combine(profile.DirectoryPath, "custom_rules.md"));
        if (!fullTarget.StartsWith(fullProfilesRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Ruta de perfil fuera del directorio permitido (CWE-22).");

        if (Directory.Exists(profile.DirectoryPath) &&
            (File.GetAttributes(profile.DirectoryPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("No se permiten enlaces simbólicos en perfiles.");
        }

        if (string.IsNullOrEmpty(trimmed))
        {
            if (File.Exists(fullTarget))
                File.Delete(fullTarget);
            profile.CustomRules = string.Empty;
            return;
        }

        AtomicWrite(fullTarget, trimmed);
        profile.CustomRules = trimmed;
    }

    private static string ReadBoundedText(string path, int maxChars)
    {
        if (!File.Exists(path))
            return string.Empty;
        var info = new FileInfo(path);
        if (info.Length > MaxConfigFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            return string.Empty;
        var text = File.ReadAllText(path, Encoding.UTF8).Trim();
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static string ExtractCompactTacticalSummary(string systemPromptPath, string fallbackNotes)
    {
        var raw = ReadBoundedText(systemPromptPath, 240);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var singleLine = string.Join(' ', raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            return singleLine.Length <= 220 ? singleLine : singleLine[..220];
        }
        return (fallbackNotes ?? string.Empty).Trim();
    }

    private static void BackupCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var bak = $"{path}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(path, bak, overwrite: true);
            }
        }
        catch
        {
            // Best effort backup
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
