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
        @"(AIza[0-9A-Za-z_\-]{12,}|AQ\.[0-9A-Za-z_\-\.]{12,}|gsk_[0-9A-Za-z_\-]{12,}|sk-[0-9A-Za-z_\-]{12,}|hf_[0-9A-Za-z]{12,}|xai-[0-9A-Za-z_\-]{12,}|r8_[0-9A-Za-z_\-]{12,}|(?:Bearer|Basic)\s+\S{6,}|(?i)[""']?(?:x-(?:goog-)?api-key|api_?key|access_token|authorization)[""']?\s*[:=]\s*(?:[""'][^""'\r\n]{4,}[""']|[^\s,;}{&""']{6,})|(?i)(?:api_?key|access_token|token|key)=[^&\s""']+)",
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
    public static (bool IsValid, string Error) Validate(string url, bool allowLoopbackHttp = false, bool allowWebSocket = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, "Base URL vacía.");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return (false, "Base URL con formato inválido.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return (false, "Base URL no puede incluir credenciales embebidas (user@host).");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return (false, "Base URL no puede incluir query string ni fragment.");

        var scheme = uri.Scheme.ToLowerInvariant();
        bool allowedScheme = scheme is "https" or "http" || (allowWebSocket && scheme is "wss" or "ws");
        if (!allowedScheme)
            return (false, $"Esquema no permitido: '{scheme}'. Solo HTTPS.");

        var host = uri.Host;
        bool isLoopbackHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              (IPAddress.TryParse(host, out var parsedIp) && IPAddress.IsLoopback(parsedIp));

        if (scheme is "http" or "ws" && !(allowLoopbackHttp && isLoopbackHost))
            return (false, "Conexión sin cifrar remota bloqueada (CWE-319). Usá HTTPS/WSS.");

        if (IPAddress.TryParse(host, out var ip) && IsBlockedAddress(ip, allowLoopbackHttp))
            return (false, $"IP privada/reservada bloqueada por seguridad SSRF (CWE-918): {host}.");

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
            if (b[0] == 0) return true;                                  // 0.0.0.0/8 ("This" network)
            if (b[0] == 10) return true;                                 // 10.0.0.0/8 RFC1918
            if (b[0] == 127) return !allowLoopback;                      // 127.0.0.0/8 Loopback
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 Link-Local / Cloud Metadata
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;     // 172.16.0.0/12 RFC1918
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16 RFC1918
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;      // 192.0.0.0/24 IETF protocol assignments
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;      // 192.0.2.0/24 TEST-NET-1
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;  // 198.18.0.0/15 Benchmarking
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;   // 198.51.100.0/24 TEST-NET-2
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;    // 203.0.113.0/24 TEST-NET-3
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return true;    // 100.64.0.0/10 CGNAT
            if (b[0] >= 224) return true;                                // 224.0.0.0/4 Multicast & 240.0.0.0/4 Reserved/Broadcast
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6Teredo)
                return true;

            var b = ip.GetAddressBytes();
            if (ip.Equals(IPAddress.IPv6Any)) return true;
            if ((b[0] & 0xFE) == 0xFC) return true;                      // fc00::/7 Unique Local (ULA)
            if (b[0] == 0x20 && b[1] == 0x02) return true;               // 2002::/16 6to4 (embedded IPv4)
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true; // 2001::/32 Teredo
            if (b[0] == 0x01 && b[1] == 0x00 && b[2] == 0x00 && b[3] == 0x00) return true; // 100::/64 Discard

            // RFC 8215 NAT64 Local-Use 64:ff9b:1::/48 is strictly non-routable globally regardless of IPv4 offset!
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01)
                return true;

            // IPv4-compatible ::/96 and NAT64 well-known 64:ff9b::/96 -> recursively check embedded IPv4 in last 4 bytes!
            bool isZeroPrefix96 = b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
                                  b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
                                  b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0;
            bool isNat64WellKnown = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
                                    b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
                                    b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0;
            bool isIpv4Translated = b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
                                    b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
                                    b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0;

            if (isZeroPrefix96 || isNat64WellKnown || isIpv4Translated)
            {
                var embeddedV4 = new IPAddress([b[12], b[13], b[14], b[15]]);
                return IsBlockedAddress(embeddedV4, allowLoopback: isZeroPrefix96 && allowLoopback && embeddedV4.Equals(IPAddress.Loopback));
            }
        }

        return false;
    }
}

/// <summary>
/// Strict ASCII global hotkey parser &amp; formatter shared across Windows hotkey registration and validation.
/// </summary>
public static class HotkeyParser
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;

    public static bool TryParse(string? raw, out uint modifiers, out uint virtualKey, out string normalized)
    {
        modifiers = 0;
        virtualKey = 0;
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var modNames = new List<string>(3);
        string? keyName = null;

        foreach (var token in parts)
        {
            var t = token.ToUpperInvariant();
            if (t is "CTRL" or "CONTROL")
            {
                if ((modifiers & ModControl) == 0) modNames.Add("Ctrl");
                modifiers |= ModControl;
                continue;
            }
            if (t == "ALT")
            {
                if ((modifiers & ModAlt) == 0) modNames.Add("Alt");
                modifiers |= ModAlt;
                continue;
            }
            if (t == "SHIFT")
            {
                if ((modifiers & ModShift) == 0) modNames.Add("Shift");
                modifiers |= ModShift;
                continue;
            }

            if (virtualKey != 0)
                return false; // Duplicate non-modifier primary key

            if (t.StartsWith('F') && int.TryParse(t[1..], out int fNum) && fNum is >= 1 and <= 24)
            {
                virtualKey = (uint)(0x70 + (fNum - 1));
                keyName = $"F{fNum}";
            }
            else if (t.StartsWith("NUM") && int.TryParse(t[3..], out int nNum) && nNum is >= 0 and <= 9)
            {
                virtualKey = (uint)(0x60 + nNum);
                keyName = $"NUM{nNum}";
            }
            else if (t is "INSERT" or "INS") { virtualKey = 0x2D; keyName = "Insert"; }
            else if (t is "DELETE" or "DEL") { virtualKey = 0x2E; keyName = "Delete"; }
            else if (t == "HOME") { virtualKey = 0x24; keyName = "Home"; }
            else if (t == "END") { virtualKey = 0x23; keyName = "End"; }
            else if (t is "PAGEUP" or "PGUP") { virtualKey = 0x21; keyName = "PageUp"; }
            else if (t is "PAGEDOWN" or "PGDN") { virtualKey = 0x22; keyName = "PageDown"; }
            else if (t == "PAUSE") { virtualKey = 0x13; keyName = "Pause"; }
            else if (t == "SPACE") { virtualKey = 0x20; keyName = "Space"; }
            else if (t.Length == 1 && ((t[0] >= 'A' && t[0] <= 'Z') || (t[0] >= '0' && t[0] <= '9')))
            {
                virtualKey = t[0];
                keyName = t;
            }
            else
            {
                return false;
            }
        }

        if (virtualKey == 0 || keyName is null)
            return false;

        normalized = modNames.Count > 0 ? $"{string.Join('+', modNames)}+{keyName}" : keyName;
        return true;
    }

    /// <summary>
    /// Parses a single hold-to-talk key (supports letters, digits, F1-F24, Space, Alt, Ctrl, Shift, CapsLock, Tab, Mouse4, Mouse5).
    /// </summary>
    public static bool TryParsePushToTalkKey(string? raw, out uint virtualKey, out string normalized)
    {
        virtualKey = 0;
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var t = raw.Trim().ToUpperInvariant();
        if (t.Contains('+') || t.Any(c => c > 0x7F))
            return false;

        if (TryParse(t, out uint mods, out uint vk, out var norm) && mods == 0)
        {
            virtualKey = vk;
            normalized = norm;
            return true;
        }

        (virtualKey, normalized) = t switch
        {
            "CAPSLOCK" or "CAPS" => (0x14u, "CapsLock"),
            "TAB" => (0x09u, "Tab"),
            "ALT" or "LALT" => (0xA4u, "Alt"),
            "RALT" => (0xA5u, "RAlt"),
            "CTRL" or "LCTRL" or "CONTROL" => (0xA2u, "Ctrl"),
            "RCTRL" => (0xA3u, "RCtrl"),
            "SHIFT" or "LSHIFT" => (0xA0u, "Shift"),
            "RSHIFT" => (0xA1u, "RShift"),
            "TILDE" or "BACKQUOTE" or "`" => (0xC0u, "Backquote"),
            "MOUSE4" or "XBUTTON1" or "MB4" => (0x05u, "Mouse4"),
            "MOUSE5" or "XBUTTON2" or "MB5" => (0x06u, "Mouse5"),
            _ => (0u, string.Empty)
        };
        return virtualKey != 0;
    }
}

public sealed partial class ConfigRepository
{
    public const int MaxCustomRulesChars = 600;
    public const int MaxMarkdownProfileChars = 700;
    public const string ProtectedSecretPrefix = "dpapi:";
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
    public string ReviewsDir => Path.Combine(WorkspaceRoot, "logs", "reviews");
    public string MemoryDir => Path.Combine(WorkspaceRoot, "logs", "memory");
    public string AccountsPath => Path.Combine(ConfigDir, "accounts.json");
    public string UserConfigPath => Path.Combine(ConfigDir, "user_config.json");
    public string CoachSoulPath => Path.Combine(ConfigDir, "coach_soul.md");
    public string PlayerProfilePath => Path.Combine(ConfigDir, "player_profile.md");
    private readonly AI.ICoachSafetyPipeline _safety;
    private readonly Security.ISecretProtector _secretProtector;

    public string? LastLoadWarning { get; private set; }

    public ConfigRepository(
        string? workspaceRoot = null,
        AI.ICoachSafetyPipeline? safetyPipeline = null,
        Security.ISecretProtector? secretProtector = null)
    {
        WorkspaceRoot = workspaceRoot ?? DiscoverWorkspaceRoot();
        _safety = safetyPipeline ?? AI.CoachContentGuard.DefaultPipeline;
        _secretProtector = secretProtector ?? Security.PlaintextSecretProtector.Instance;
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

        var portableDir = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(portableDir, "profiles")))
            return portableDir;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HUDMind");
    }

    /// <summary>
    /// Populates the 7 built-in game profiles from embedded assembly resources when running
    /// standalone on a new PC where `profiles/` does not yet exist or is completely empty.
    /// Never overwrites existing user or test profiles.
    /// </summary>
    public int EnsureDefaultProfilesSeeded()
    {
        var fullProfilesRoot = Path.GetFullPath(ProfilesDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(fullProfilesRoot))
        {
            if ((File.GetAttributes(fullProfilesRoot) & FileAttributes.ReparsePoint) != 0)
                return 0;
            if (Directory.EnumerateDirectories(fullProfilesRoot).Any())
                return 0;
        }

        int extracted = 0;
        Directory.CreateDirectory(fullProfilesRoot);

        foreach (var (relKey, content) in Security.ProfileVaultReader.GetDefaultProfileEntries())
        {
            var parts = relKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                continue;

            var gameId = parts[0].ToLowerInvariant();
            var fileName = parts[1];
            if (!SafeGameIdRegex().IsMatch(gameId) ||
                fileName is not ("manifest.json" or "system_prompt.md" or "knowledge_base.md"))
            {
                continue;
            }

            var targetDir = Path.GetFullPath(Path.Combine(fullProfilesRoot, gameId));
            var targetFile = Path.GetFullPath(Path.Combine(targetDir, fileName));
            if (!targetFile.StartsWith(fullProfilesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(targetDir);
            if (File.Exists(targetFile))
                continue;

            AtomicWrite(targetFile, content);
            extracted++;
        }

        return extracted;
    }

    public UserConfig LoadUserConfig()
    {
        try
        {
            if (File.Exists(UserConfigPath))
            {
                var info = new FileInfo(UserConfigPath);
                if (info.Length <= MaxConfigFileBytes && (info.Attributes & FileAttributes.ReparsePoint) == 0)
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
                if (info.Length <= MaxConfigFileBytes * 4 && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    var json = File.ReadAllText(AccountsPath, Encoding.UTF8);
                    var rawList = JsonSerializer.Deserialize<List<ProviderAccount?>>(json, JsonOpts);
                    var list = rawList?.Where(a => a is not null).Cast<ProviderAccount>().ToList();
                    if (list is { Count: > 0 })
                    {
                        bool needsMigration = false;
                        foreach (var acc in list)
                        {
                            if (string.IsNullOrWhiteSpace(acc.ApiKey))
                                continue;

                            if (acc.ApiKey.StartsWith(ProtectedSecretPrefix, StringComparison.Ordinal))
                            {
                                if (_secretProtector.IsSupported)
                                {
                                    try
                                    {
                                        acc.ApiKey = _secretProtector.Unprotect(acc.ApiKey);
                                    }
                                    catch
                                    {
                                        acc.ApiKey = string.Empty;
                                        LastLoadWarning = "Algunas API keys guardadas pertenecían a otro usuario/PC (DPAPI) y fueron limpiadas.";
                                    }
                                }
                            }
                            else if (_secretProtector.IsSupported)
                            {
                                needsMigration = true;
                            }
                        }

                        var ordered = list.OrderBy(a => a.Priority).ToList();
                        if (needsMigration)
                        {
                            try { SaveAccounts(ordered); } catch { }
                        }
                        return ordered;
                    }
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
        var ordered = accounts.OrderBy(a => a.Priority)
            .Select(a =>
            {
                string rawKey = a.ApiKey ?? string.Empty;
                string storedKey = _secretProtector.IsSupported &&
                                   !string.IsNullOrWhiteSpace(rawKey) &&
                                   !rawKey.StartsWith(ProtectedSecretPrefix, StringComparison.Ordinal)
                    ? _secretProtector.Protect(rawKey)
                    : rawKey;

                return new ProviderAccount
                {
                    Id = a.Id,
                    Label = a.Label,
                    Provider = a.Provider,
                    ApiKey = storedKey,
                    Model = a.Model,
                    BaseUrl = a.BaseUrl,
                    Priority = a.Priority,
                    Enabled = a.Enabled,
                    SupportsVisionOverride = a.SupportsVisionOverride,
                    ExtraFields = a.ExtraFields
                };
            })
            .ToList();
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
                if (info.Length > MaxConfigFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize<GameManifest>(json, JsonOpts);
                if (manifest is null || !SafeGameIdRegex().IsMatch(manifest.GameId) || !seenIds.Add(manifest.GameId))
                    continue;

                if (!string.IsNullOrWhiteSpace(manifest.DisplayName) && manifest.DisplayName.Length > 64)
                    manifest.DisplayName = manifest.DisplayName[..64];

                var customRules = _safety.SanitizeInstructionMarkdown(
                    ReadBoundedText(Path.Combine(fullDir, "custom_rules.md"), MaxCustomRulesChars));
                var diskSysPrompt = ReadBoundedText(Path.Combine(fullDir, "system_prompt.md"), 750);
                var embeddedSysPrompt = ReadEmbeddedDefaultProfileText(manifest.GameId, "system_prompt.md", 750);
                var bestSysPrompt = embeddedSysPrompt.Length > diskSysPrompt.Length ? embeddedSysPrompt : diskSysPrompt;
                var tacticalSummary = CompactTacticalSummaryText(bestSysPrompt, manifest.Notes);

                var diskKb = ReadBoundedText(Path.Combine(fullDir, "knowledge_base.md"), 7500);
                var embeddedKb = ReadEmbeddedDefaultProfileText(manifest.GameId, "knowledge_base.md", 7500);
                var knowledgeGuide = embeddedKb.Length > diskKb.Length ? embeddedKb : diskKb;

                result.Add(new GameProfile
                {
                    Manifest = manifest,
                    DirectoryPath = fullDir,
                    CustomRules = customRules,
                    TacticalSummary = tacticalSummary,
                    KnowledgeGuide = knowledgeGuide
                });
            }
            catch
            {
                // Skip invalid profile directories safely
            }
        }

        return result;
    }

    public static GameProfile CreateUniversalProfile(
        string profilesRoot,
        bool isEn = false,
        AI.ICoachSafetyPipeline? safetyPipeline = null)
    {
        var universalDir = Path.Combine(profilesRoot, "universal");
        // Check universal/custom_rules.md first, and legacy profiles/custom_rules.md fallback
        var rules = ReadBoundedText(Path.Combine(universalDir, "custom_rules.md"), MaxCustomRulesChars);
        if (string.IsNullOrEmpty(rules))
            rules = ReadBoundedText(Path.Combine(profilesRoot, "custom_rules.md"), MaxCustomRulesChars);
        rules = (safetyPipeline ?? AI.CoachContentGuard.DefaultPipeline).SanitizeInstructionMarkdown(rules);

        return new GameProfile
        {
            DirectoryPath = universalDir,
            CustomRules = rules,
            Manifest = new GameManifest
            {
                GameId = "universal",
                DisplayName = isEn ? "Any Game (Universal)" : "Cualquier Juego (Universal)",
                ProcessNames = [],
                CaptureFps = 1.5,
                PhashThreshold = 7,
                CooldownSeconds = 8.0,
                MaxResolution = [1280, 720],
                Language = isEn ? "en" : "es",
                Notes = isEn
                    ? "Generic universal coach detecting match phases cleanly across any game."
                    : "Coach universal genérico que detecta fase (menú, replay, saque, entrenamiento o partida) en cualquier videojuego."
            }
        };
    }

    private (string ProfileDir, string TargetFile) ValidateProfileSubpath(GameProfile profile, string? fileName = null)
    {
        var fullProfilesRoot = Path.GetFullPath(ProfilesDir) + Path.DirectorySeparatorChar;
        var fullProfileDir = Path.GetFullPath(profile.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRootNoSlash = fullProfilesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullProfileDir, fullRootNoSlash, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("La operación debe ejecutarse dentro de una subcarpeta de perfil (CWE-22).");

        var candidate = string.IsNullOrEmpty(fileName)
            ? fullProfileDir + Path.DirectorySeparatorChar
            : Path.GetFullPath(Path.Combine(fullProfileDir, fileName));
        if (!candidate.StartsWith(fullProfilesRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Ruta de perfil fuera del directorio permitido (CWE-22).");

        if (Directory.Exists(fullProfileDir) &&
            (File.GetAttributes(fullProfileDir) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("No se permiten enlaces simbólicos en perfiles.");
        }

        return (fullProfileDir, string.IsNullOrEmpty(fileName) ? fullProfileDir : candidate);
    }

    private static List<string> NormalizeProcessNames(IEnumerable<string> processNames)
    {
        var list = processNames
            .Select(p => (p ?? string.Empty).Trim())
            .Where(p => p.Length is >= 2 and <= 80 &&
                        !p.Contains('/') && !p.Contains('\\') && !p.Contains("..", StringComparison.Ordinal) &&
                        p.All(c => !char.IsControl(c)))
            .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : $"{p}.exe")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0)
            throw new ArgumentException("Ingresá al menos un nombre de ejecutable válido (ej: MiJuego.exe).", nameof(processNames));
        return list;
    }

    public void SaveCustomRules(GameProfile profile, string rulesText)
    {
        var trimmed = (rulesText ?? string.Empty).Trim();
        if (trimmed.Length > MaxCustomRulesChars)
            throw new ArgumentException($"Las reglas personalizadas no pueden superar {MaxCustomRulesChars} caracteres.");
        if (_safety.ContainsUnsafeInstruction(trimmed))
            throw new ArgumentException("Las reglas no pueden incluir insultos, temas sexuales/ilegales ni pedidos fuera del coaching gamer.");

        var (fullProfileDir, fullTarget) = ValidateProfileSubpath(profile, "custom_rules.md");

        if (string.IsNullOrEmpty(trimmed))
        {
            if (File.Exists(fullTarget))
                File.Delete(fullTarget);
            profile.CustomRules = string.Empty;
            return;
        }

        Directory.CreateDirectory(fullProfileDir);
        AtomicWrite(fullTarget, trimmed);
        profile.CustomRules = trimmed;
    }

    /// <summary>
    /// Updates process executables, cooldown, and pHash sensitivity in a profile's manifest.json safely.
    /// </summary>
    public void UpdateProfileManifest(
        GameProfile profile,
        IEnumerable<string> processNames,
        double cooldownSeconds,
        int phashThreshold)
    {
        if (string.Equals(profile.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El perfil dinámico 'universal' no usa archivo manifest editable.");

        var validProcs = NormalizeProcessNames(processNames);
        var (_, manifestPath) = ValidateProfileSubpath(profile, "manifest.json");

        profile.Manifest.ProcessNames = validProcs;
        profile.Manifest.CooldownSeconds = Math.Round(Math.Clamp(cooldownSeconds, 1.0, 120.0), 1);
        profile.Manifest.PhashThreshold = Math.Clamp(phashThreshold, 1, 64);

        AtomicWrite(manifestPath, JsonSerializer.Serialize(profile.Manifest, JsonOpts));
    }

    /// <summary>
    /// Safely deletes a profile directory under `profiles/` (refusing `universal`, symlinks, and CWE-22).
    /// </summary>
    public void DeleteGameProfile(GameProfile profile)
    {
        if (string.Equals(profile.Manifest.GameId, "universal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("No se puede eliminar el perfil dinámico 'universal'.");

        var (fullProfileDir, _) = ValidateProfileSubpath(profile);
        if (Directory.Exists(fullProfileDir))
            Directory.Delete(fullProfileDir, recursive: true);
    }

    /// <summary>
    /// Creates a new custom game profile folder safely (with strict ID + executable sanitization and CWE-22 guard).
    /// </summary>
    public GameProfile CreateNewGameProfile(
        string gameId,
        string displayName,
        IEnumerable<string> processNames,
        string notes = "",
        string systemPrompt = "")
    {
        var id = (gameId ?? string.Empty).Trim().ToLowerInvariant();
        if (!SafeGameIdRegex().IsMatch(id) || string.Equals(id, "universal", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El ID de juego debe usar solo minúsculas, números, '_' o '-' (y no 'universal').", nameof(gameId));

        var cleanName = string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim();
        if (cleanName.Length > 64) cleanName = cleanName[..64];

        var validProcs = NormalizeProcessNames(processNames);

        var fullProfilesRoot = Path.GetFullPath(ProfilesDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullTargetDir = Path.GetFullPath(Path.Combine(fullProfilesRoot, id));
        if (!fullTargetDir.StartsWith(fullProfilesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Ruta de perfil fuera del directorio permitido (CWE-22).");

        if (Directory.Exists(fullTargetDir))
            throw new InvalidOperationException($"Ya existe un perfil con ID '{id}'.");

        if (Directory.Exists(fullProfilesRoot) && (File.GetAttributes(fullProfilesRoot) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("No se permiten enlaces simbólicos en profiles.");

        Directory.CreateDirectory(fullTargetDir);

        var manifest = new GameManifest
        {
            GameId = id,
            DisplayName = cleanName,
            ProcessNames = validProcs,
            CaptureFps = 1.5,
            PhashThreshold = 7,
            CooldownSeconds = 8.0,
            MaxResolution = [1280, 720],
            Language = "es",
            Notes = string.IsNullOrWhiteSpace(notes)
                ? $"Perfil táctico personalizado para {cleanName}."
                : notes.Trim()[..Math.Min(notes.Trim().Length, 240)]
        };

        AtomicWrite(Path.Combine(fullTargetDir, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOpts));

        var bodyPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? $"# Rol: Coach Táctico de {cleanName}\n\nAnalizá solo lo visible en pantalla en {cleanName}. Da consejos breves (7-12 palabras), claros y sin alucinaciones.\n"
            : systemPrompt.Trim();
        AtomicWrite(Path.Combine(fullTargetDir, "system_prompt.md"), bodyPrompt);

        return new GameProfile
        {
            Manifest = manifest,
            DirectoryPath = fullTargetDir,
            TacticalSummary = ExtractCompactTacticalSummary(Path.Combine(fullTargetDir, "system_prompt.md"), manifest.Notes)
        };
    }

    /// <summary>
    /// Persists a post-match coaching review cleanly as timestamped Markdown under `logs/reviews/`.
    /// </summary>
    public string SavePostMatchReview(string gameDisplayName, string modelName, string reviewText, DateTimeOffset? nowLocal = null)
    {
        var ts = nowLocal ?? DateTimeOffset.Now;
        var logsDir = Path.GetFullPath(Path.Combine(WorkspaceRoot, "logs"));
        var reviewsFull = Path.GetFullPath(ReviewsDir);

        if (Directory.Exists(logsDir) && (File.GetAttributes(logsDir) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("No se permiten enlaces simbólicos en logs.");
        if (Directory.Exists(reviewsFull) && (File.GetAttributes(reviewsFull) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("No se permiten enlaces simbólicos en logs/reviews.");

        Directory.CreateDirectory(reviewsFull);

        var rawGame = string.IsNullOrWhiteSpace(gameDisplayName) ? "partida" : gameDisplayName.Trim();
        var slug = new string(rawGame.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')
            .ToArray()).Trim('_');
        if (string.IsNullOrEmpty(slug)) slug = "partida";
        if (slug.Length > 36) slug = slug[..36];

        var fileName = $"{ts:yyyyMMdd_HHmmss}_{slug}.md";
        var destPath = Path.GetFullPath(Path.Combine(reviewsFull, fileName));
        if (!destPath.StartsWith(reviewsFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Ruta de resumen inválida (CWE-22).");

        var safeModel = SecretRedactor.Redact(string.IsNullOrWhiteSpace(modelName) ? "AI" : modelName);
        var safeBody = SecretRedactor.Redact((reviewText ?? string.Empty).Trim());
        var md = $"# Resumen Post-Partida — {rawGame}\n\n" +
                 $"- **Fecha:** {ts:yyyy-MM-dd HH:mm:ss}\n" +
                 $"- **Modelo IA:** `{safeModel}`\n\n" +
                 $"{safeBody}\n";

        AtomicWrite(destPath, md);
        return destPath;
    }

    /// <summary>
    /// Loads a concise, secret-redacted training focus hint from the latest post-match review under `logs/reviews/`.
    /// </summary>
    public string LoadLatestPostMatchFocus(string? gameDisplayName = null, int maxChars = 220)
    {
        try
        {
            var logsDir = Path.GetFullPath(Path.Combine(WorkspaceRoot, "logs"));
            var reviewsFull = Path.GetFullPath(ReviewsDir);
            if (!Directory.Exists(reviewsFull))
                return string.Empty;
            if ((Directory.Exists(logsDir) && (File.GetAttributes(logsDir) & FileAttributes.ReparsePoint) != 0) ||
                (File.GetAttributes(reviewsFull) & FileAttributes.ReparsePoint) != 0)
            {
                return string.Empty;
            }

            var files = Directory.GetFiles(reviewsFull, "*.md", SearchOption.TopDirectoryOnly)
                .Where(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
                return string.Empty;

            string? chosen = null;
            if (!string.IsNullOrWhiteSpace(gameDisplayName))
            {
                var slug = new string(gameDisplayName.Trim().ToLowerInvariant()
                    .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
                if (!string.IsNullOrEmpty(slug))
                    chosen = files.FirstOrDefault(f => Path.GetFileName(f).Contains($"_{slug}.md", StringComparison.OrdinalIgnoreCase));
            }
            chosen ??= files[0];

            var raw = ReadBoundedText(chosen, 4096);
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            var bodyLines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith('#') && !l.StartsWith("- **Fecha:", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("- **Modelo", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (bodyLines.Count == 0)
                return string.Empty;

            // Prefer the drill / top mistakes summary concisely
            var preferred = bodyLines
                .Where(l => l.StartsWith("3)", StringComparison.Ordinal) ||
                            l.Contains("ejercicio", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("drill", StringComparison.OrdinalIgnoreCase) ||
                            l.StartsWith("1)", StringComparison.Ordinal))
                .ToList();
            var picked = preferred.Count > 0 ? preferred : bodyLines.Take(2).ToList();
            var joined = string.Join(" · ", picked).Replace("**", "", StringComparison.Ordinal);
            return AI.CoachingPersonaBuilder.SanitizeField(SecretRedactor.Redact(joined), maxChars);
        }
        catch
        {
            return string.Empty;
        }
    }

    public Pipeline.PlayerGameMemory LoadPlayerGameMemory(string? gameId)
    {
        var normId = string.IsNullOrWhiteSpace(gameId) ? "universal" : gameId.Trim().ToLowerInvariant();
        if (!TryResolveSafeMemoryPath(normId, out var destPath))
            return new Pipeline.PlayerGameMemory { GameId = normId };

        try
        {
            if (!File.Exists(destPath))
                return new Pipeline.PlayerGameMemory { GameId = normId };

            var info = new FileInfo(destPath);
            if (info.Length > MaxConfigFileBytes * 2 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return new Pipeline.PlayerGameMemory { GameId = normId };

            var json = File.ReadAllText(destPath, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Pipeline.PlayerGameMemory>(json, JsonOpts);
            if (parsed is not null)
            {
                parsed.GameId = string.IsNullOrWhiteSpace(parsed.GameId) ? normId : parsed.GameId;
                return parsed;
            }
        }
        catch (Exception ex)
        {
            BackupCorruptFile(destPath);
            LastLoadWarning = $"player_memory ilegible (backup guardado): {ex.Message}";
        }

        return new Pipeline.PlayerGameMemory { GameId = normId };
    }

    public Pipeline.PlayerGameMemory RecordMatchToRollingMemory(
        string? gameId,
        string? displayName,
        Pipeline.MatchMemoryEntry entry,
        int windowSize = Pipeline.PlayerMemoryStore.DefaultWindowSize,
        string? latestReviewFileName = null)
    {
        var normId = string.IsNullOrWhiteSpace(gameId) ? "universal" : gameId.Trim().ToLowerInvariant();
        if (!TryResolveSafeMemoryPath(normId, ".json", out var destPath) ||
            !TryResolveSafeMemoryPath(normId, ".md", out var mdPath))
        {
            throw new UnauthorizedAccessException("Ruta de memoria de juego inválida o insegura (CWE-22).");
        }

        var memoryDir = Path.GetDirectoryName(destPath)!;
        Directory.CreateDirectory(memoryDir);

        var mem = LoadPlayerGameMemory(normId);
        if (string.IsNullOrWhiteSpace(mem.DisplayName) && !string.IsNullOrWhiteSpace(displayName))
            mem.DisplayName = displayName.Trim();

        if (!string.IsNullOrWhiteSpace(latestReviewFileName))
            mem.LatestReviewFile = Path.GetFileName(latestReviewFileName.Trim());

        Pipeline.PlayerMemoryStore.ApplyRollingMatch(mem, entry, windowSize);
        var json = JsonSerializer.Serialize(mem, JsonOpts);
        AtomicWrite(destPath, json);

        string customRulesFull = Path.Combine(ProfilesDir, normId, "custom_rules.md");
        string? customRel = File.Exists(customRulesFull) ? $"../../profiles/{normId}/custom_rules.md" : null;
        string? reviewRel = !string.IsNullOrWhiteSpace(mem.LatestReviewFile) ? $"../reviews/{mem.LatestReviewFile}" : null;
        AtomicWrite(mdPath, Pipeline.PlayerMemoryStore.RenderGameMemoryMarkdown(mem, customRel, reviewRel));
        RefreshMemoryIndexMarkdown();
        return mem;
    }

    public void ResetPlayerGameMemory(string? gameId)
    {
        var normId = string.IsNullOrWhiteSpace(gameId) ? "universal" : gameId.Trim().ToLowerInvariant();
        if (!TryResolveSafeMemoryPath(normId, ".json", out var destPath) ||
            !TryResolveSafeMemoryPath(normId, ".md", out var mdPath))
        {
            throw new UnauthorizedAccessException("Ruta de memoria de juego inválida o insegura (CWE-22).");
        }

        if (File.Exists(destPath) && (File.GetAttributes(destPath) & FileAttributes.ReparsePoint) == 0)
            File.Delete(destPath);
        if (File.Exists(mdPath) && (File.GetAttributes(mdPath) & FileAttributes.ReparsePoint) == 0)
            File.Delete(mdPath);

        if (Directory.Exists(MemoryDir))
            RefreshMemoryIndexMarkdown();
    }

    public string LoadCoachSoulMarkdown() =>
        ReadSafeConfigMarkdown(CoachSoulPath, AI.CoachContextAssembler.DefaultCoachSoulMarkdown);

    public void SaveCoachSoulMarkdown(string? markdown) =>
        WriteSafeConfigMarkdown(CoachSoulPath, markdown);

    public string LoadPlayerProfileMarkdown() =>
        ReadSafeConfigMarkdown(PlayerProfilePath, AI.CoachContextAssembler.DefaultPlayerProfileMarkdown);

    public void SavePlayerProfileMarkdown(string? markdown) =>
        WriteSafeConfigMarkdown(PlayerProfilePath, markdown);

    public AI.AssembledCoachContext LoadAssembledCoachContext(
        string? gameId,
        UserConfig config,
        GameProfile? profile,
        AI.CoachContextDepth depth = AI.CoachContextDepth.RealtimePlay)
    {
        var normId = string.IsNullOrWhiteSpace(gameId) ? "universal" : gameId.Trim().ToLowerInvariant();
        var mem = LoadPlayerGameMemory(normId);
        var fallbackFocus = mem.Matches.Count == 0
            ? LoadLatestPostMatchFocus(profile?.Manifest.DisplayName, 240)
            : string.Empty;

        bool isEn = AI.CoachingPersonaBuilder.IsEnglish(config, preferTextLang: true);
        return AI.CoachContextAssembler.Assemble(
            config,
            profile,
            mem,
            LoadCoachSoulMarkdown(),
            LoadPlayerProfileMarkdown(),
            isEn,
            depth,
            fallbackFocus);
    }

    public string LoadPlayerMemoryPromptContext(
        string? gameId,
        string? gameDisplayName = null,
        int maxChars = 320,
        bool isEn = false)
    {
        var normId = string.IsNullOrWhiteSpace(gameId) ? "universal" : gameId.Trim().ToLowerInvariant();
        var mem = LoadPlayerGameMemory(normId);
        var rolling = Pipeline.PlayerMemoryStore.FormatForCoachPrompt(mem, isEn: isEn, maxChars: maxChars);
        if (!string.IsNullOrWhiteSpace(rolling))
            return rolling;

        return LoadLatestPostMatchFocus(gameDisplayName, Math.Min(maxChars, 240));
    }

    private void RefreshMemoryIndexMarkdown()
    {
        try
        {
            if (!TryResolveSafeMemoryPath("index", ".md", out _))
                return;

            var memoryFull = Path.GetFullPath(MemoryDir);
            var indexPath = Path.GetFullPath(Path.Combine(memoryFull, "INDEX.md"));
            if (!indexPath.StartsWith(memoryFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            if (File.Exists(indexPath) && (File.GetAttributes(indexPath) & FileAttributes.ReparsePoint) != 0)
                return;

            var allMems = new List<Pipeline.PlayerGameMemory>();
            foreach (var jsonFile in Directory.GetFiles(memoryFull, "*.json", SearchOption.TopDirectoryOnly))
            {
                var baseId = Path.GetFileNameWithoutExtension(jsonFile);
                if (!SafeGameIdRegex().IsMatch(baseId))
                    continue;
                var m = LoadPlayerGameMemory(baseId);
                if (m.Matches.Count > 0)
                    allMems.Add(m);
            }

            AtomicWrite(indexPath, Pipeline.PlayerMemoryStore.RenderGlobalIndexMarkdown(allMems));
        }
        catch
        {
            // Index generation is best-effort
        }
    }

    private string ReadSafeConfigMarkdown(string path, string fallbackContent)
    {
        try
        {
            var configFull = Path.GetFullPath(ConfigDir);
            var fullTarget = Path.GetFullPath(path);
            if (!fullTarget.StartsWith(configFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return fallbackContent;
            if (Directory.Exists(configFull) && (File.GetAttributes(configFull) & FileAttributes.ReparsePoint) != 0)
                return fallbackContent;

            if (!File.Exists(fullTarget))
                return fallbackContent;

            var info = new FileInfo(fullTarget);
            if (info.Length > MaxConfigFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return fallbackContent;

            var raw = File.ReadAllText(fullTarget, Encoding.UTF8);
            return raw.Length <= MaxMarkdownProfileChars * 2 ? raw : raw[..(MaxMarkdownProfileChars * 2)];
        }
        catch
        {
            return fallbackContent;
        }
    }

    private void WriteSafeConfigMarkdown(string path, string? rawContent)
    {
        var configFull = Path.GetFullPath(ConfigDir);
        if (Directory.Exists(configFull) && (File.GetAttributes(configFull) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("No se permiten enlaces simbólicos en config.");

        Directory.CreateDirectory(configFull);
        var fullTarget = Path.GetFullPath(path);
        if (!fullTarget.StartsWith(configFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Ruta de perfil markdown inválida (CWE-22).");
        if (File.Exists(fullTarget) && (File.GetAttributes(fullTarget) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("No se permiten enlaces simbólicos en archivos config.");

        var cleaned = SecretRedactor.Redact(_safety.SanitizeInstructionMarkdown((rawContent ?? string.Empty).Trim()));
        if (cleaned.Length > MaxMarkdownProfileChars)
            cleaned = cleaned[..MaxMarkdownProfileChars];
        AtomicWrite(fullTarget, string.IsNullOrEmpty(cleaned) ? string.Empty : cleaned + Environment.NewLine);
    }

    private bool TryResolveSafeMemoryPath(string normId, out string destPath) =>
        TryResolveSafeMemoryPath(normId, ".json", out destPath);

    private bool TryResolveSafeMemoryPath(string normId, string extension, out string destPath)
    {
        destPath = string.Empty;
        if (!SafeGameIdRegex().IsMatch(normId))
            return false;

        var logsDir = Path.GetFullPath(Path.Combine(WorkspaceRoot, "logs"));
        var memoryFull = Path.GetFullPath(MemoryDir);
        if ((Directory.Exists(logsDir) && (File.GetAttributes(logsDir) & FileAttributes.ReparsePoint) != 0) ||
            (Directory.Exists(memoryFull) && (File.GetAttributes(memoryFull) & FileAttributes.ReparsePoint) != 0))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(memoryFull, $"{normId}{extension}"));
        if (!candidate.StartsWith(memoryFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            return false;

        destPath = candidate;
        return true;
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

    private static string CompactTacticalSummaryText(string raw, string fallbackNotes)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var compact = string.Join(' ', raw
                .Replace('#', ' ')
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimStart('-', '*')));
            return compact.Length <= 380 ? compact : compact[..380];
        }
        return (fallbackNotes ?? string.Empty).Trim();
    }

    private static string ExtractCompactTacticalSummary(string systemPromptPath, string fallbackNotes) =>
        CompactTacticalSummaryText(ReadBoundedText(systemPromptPath, 750), fallbackNotes);

    private static string ReadEmbeddedDefaultProfileText(string gameId, string fileName, int maxChars) =>
        Security.ProfileVaultReader.ReadDefaultProfileText(gameId, fileName, maxChars);

    private static void BackupCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var bak = $"{path}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                if (path.EndsWith("accounts.json", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = File.ReadAllText(path, Encoding.UTF8);
                    AtomicWrite(bak, SecretRedactor.Redact(raw));
                }
                else
                {
                    File.Copy(path, bak, overwrite: true);
                }
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
