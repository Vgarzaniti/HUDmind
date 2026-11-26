using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HUDMind.Windows.Security;

/// <summary>
/// Manages optional opt-in Windows Startup registration under <c>HKCU\...\Run</c>
/// (never enabled by default; requires explicit user opt-in and validates safe non-UNC executable paths).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsStartupManager
{
    public const string DefaultRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "HUDMind";

    public static bool IsSafeExecutablePath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        var trimmed = exePath.Trim();
        if (trimmed.Contains('"') || trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        try
        {
            var full = Path.GetFullPath(trimmed);
            if (!Path.IsPathRooted(full) || new Uri(full).IsUnc)
                return false;
            return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full);
        }
        catch
        {
            return false;
        }
    }

    public static string? BuildCommandLine(string? exePath)
    {
        if (!IsSafeExecutablePath(exePath))
            return null;
        return $"\"{Path.GetFullPath(exePath!.Trim())}\" --minimized";
    }

    public static bool IsEnabled(string? subKey = null)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey ?? DefaultRunSubKey, writable: false);
            return key?.GetValue(ValueName) is string raw && !string.IsNullOrWhiteSpace(raw);
        }
        catch
        {
            return false;
        }
    }

    public static bool Apply(bool enabled, string? exePath = null, string? subKey = null)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        string targetSubKey = string.IsNullOrWhiteSpace(subKey) ? DefaultRunSubKey : subKey;
        if (!enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(targetSubKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        string? resolvedExe = exePath ?? Environment.ProcessPath;
        string? cmd = BuildCommandLine(resolvedExe);
        if (string.IsNullOrEmpty(cmd))
            return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(targetSubKey, writable: true);
            if (key is null)
                return false;
            key.SetValue(ValueName, cmd, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
