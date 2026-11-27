using System.Globalization;
using System.Text;

namespace HUDMind.Core.Overlay;

public sealed record LoopbackRconBridgeSettings(bool Enabled, int Port, string SecretToken);

/// <summary>
/// Generic, zero-injection local IPC protocol helpers for forwarding sanitized in-game toast
/// notifications through compatible external loopback RCON hosts (CWE-77 hardened).
/// </summary>
public static class InGameOverlayBridgeProtocol
{
    public const int DefaultRconPort = 9002;
    public const int DefaultTelemetryWsPort = 49122;

    public static LoopbackRconBridgeSettings ParseRconBridgeConfig(string? configContent)
    {
        bool enabled = false;
        int port = DefaultRconPort;
        string secret = string.Empty;

        if (string.IsNullOrWhiteSpace(configContent))
            return new LoopbackRconBridgeSettings(enabled, port, secret);

        foreach (var rawLine in configContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0)
                line = line[..commentIdx].Trim();

            if (line.StartsWith("rcon_enabled ", StringComparison.OrdinalIgnoreCase))
            {
                var val = UnquoteValue(line["rcon_enabled ".Length..]);
                enabled = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("rcon_port ", StringComparison.OrdinalIgnoreCase))
            {
                var val = UnquoteValue(line["rcon_port ".Length..]);
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                    port = Math.Clamp(p, 1024, 65535);
            }
            else if (line.StartsWith("rcon_password ", StringComparison.OrdinalIgnoreCase))
            {
                secret = UnquoteValue(line["rcon_password ".Length..]);
            }
        }

        return new LoopbackRconBridgeSettings(enabled, port, secret);
    }

    /// <summary>
    /// Strict CWE-77 sanitizer for console command arguments: strips command separators (;),
    /// quotes, variable expansions ($ / %), braces, backslashes, and control characters.
    /// </summary>
    public static string SanitizeConsoleArgument(string? raw, int maxLen = 104)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var sb = new StringBuilder(Math.Min(raw.Length, maxLen));
        bool lastWasSpace = true;
        foreach (char c in raw)
        {
            if (char.IsControl(c) || c is ';' or '"' or '\'' or '`' or '\\' or '$' or '%' or '{' or '}')
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
            if (sb.Length >= maxLen)
                break;
        }

        return sb.ToString().Trim();
    }

    public static string BuildToastCommand(string? title, string? message)
    {
        var safeMsg = SanitizeConsoleArgument(message, 104);
        if (string.IsNullOrEmpty(safeMsg))
            return string.Empty;

        var safeTitle = SanitizeConsoleArgument(string.IsNullOrWhiteSpace(title) ? "HUDMind" : title, 32);
        if (string.IsNullOrEmpty(safeTitle))
            safeTitle = "HUDMind";

        return $"toast \"{safeTitle}\" \"{safeMsg}\"";
    }

    public static (string UpdatedText, bool Added) EnsureCommandAllowlisted(string? existingText, string commandName)
    {
        var cmd = (commandName ?? string.Empty).Trim();
        var lines = (existingText ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (string.IsNullOrEmpty(cmd) || lines.Any(l => string.Equals(l, cmd, StringComparison.OrdinalIgnoreCase)))
            return (string.Join(Environment.NewLine, lines) + Environment.NewLine, false);

        lines.Add(cmd);
        return (string.Join(Environment.NewLine, lines) + Environment.NewLine, true);
    }

    private static string UnquoteValue(string raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }
}
