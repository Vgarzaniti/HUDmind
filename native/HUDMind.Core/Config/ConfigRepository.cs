using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HUDMind.Core.Models;

namespace HUDMind.Core.Config;

public static partial class SecretRedactor
{
    [GeneratedRegex(@"(AIza[0-9A-Za-z_\-]{12,}|AQ\.[0-9A-Za-z_\-\.]{12,}|gsk_[0-9A-Za-z_\-]{12,}|sk-[0-9A-Za-z_\-]{12,})", RegexOptions.Compiled)]
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

public sealed class ConfigRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public string WorkspaceRoot { get; }
    public string ConfigDir => Path.Combine(WorkspaceRoot, "config");
    public string ProfilesDir => Path.Combine(WorkspaceRoot, "profiles");
    public string AccountsPath => Path.Combine(ConfigDir, "accounts.json");
    public string UserConfigPath => Path.Combine(ConfigDir, "user_config.json");

    public ConfigRepository(string? workspaceRoot = null)
    {
        WorkspaceRoot = workspaceRoot ?? DiscoverWorkspaceRoot();
    }

    public static string DiscoverWorkspaceRoot()
    {
        var envHome = Environment.GetEnvironmentVariable("HUDMIND_HOME");
        if (!string.IsNullOrWhiteSpace(envHome) && Directory.Exists(Path.Combine(envHome, "profiles")))
            return envHome;

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

        // Fallback when launched from Downloads/Desktop shortcut
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
                var json = File.ReadAllText(UserConfigPath, Encoding.UTF8);
                return JsonSerializer.Deserialize<UserConfig>(json, JsonOpts) ?? new UserConfig();
            }
        }
        catch
        {
            // Fallback cleanly to defaults if corrupted
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
                var json = File.ReadAllText(AccountsPath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<ProviderAccount>>(json, JsonOpts);
                if (list is { Count: > 0 })
                    return [.. list.OrderBy(a => a.Priority)];
            }
        }
        catch
        {
            // Fallback cleanly to built-in free keyless pool
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
        if (!Directory.Exists(ProfilesDir))
            return result;

        foreach (var dir in Directory.GetDirectories(ProfilesDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize<GameManifest>(json, JsonOpts);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.GameId))
                    continue;

                var customRulesPath = Path.Combine(dir, "custom_rules.md");
                var customRules = File.Exists(customRulesPath)
                    ? File.ReadAllText(customRulesPath, Encoding.UTF8).Trim()
                    : string.Empty;

                result.Add(new GameProfile
                {
                    Manifest = manifest,
                    DirectoryPath = dir,
                    CustomRules = customRules
                });
            }
            catch
            {
                // Skip invalid profile directories safely
            }
        }

        return result;
    }

    public void SaveCustomRules(GameProfile profile, string rulesText)
    {
        var trimmed = (rulesText ?? string.Empty).Trim();
        if (trimmed.Length > 2000)
            throw new ArgumentException("Las reglas personalizadas no pueden superar 2,000 caracteres.");

        var path = Path.Combine(profile.DirectoryPath, "custom_rules.md");
        if (string.IsNullOrEmpty(trimmed))
        {
            if (File.Exists(path))
                File.Delete(path);
            profile.CustomRules = string.Empty;
            return;
        }

        AtomicWrite(path, trimmed);
        profile.CustomRules = trimmed;
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
