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

    public static string BuildTelemetryWsUrl(int port) =>
        $"ws://127.0.0.1:{Math.Clamp(port <= 0 ? DefaultTelemetryWsPort : port, 1024, 65535)}/";

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

    public static (string UpdatedText, bool Changed, bool HasSecret) EnsureRconConfigEnabled(
        string? configContent,
        string? fallbackSecret = null)
    {
        var rawLines = (configContent ?? string.Empty).Split(["\r\n", "\n"], StringSplitOptions.None);
        var outLines = new List<string>(rawLines.Length + 2);
        bool sawEnabled = false;
        bool sawSecret = false;
        bool hasNonEmptySecret = false;
        bool changed = false;

        foreach (var rawLine in rawLines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("rcon_enabled ", StringComparison.OrdinalIgnoreCase))
            {
                sawEnabled = true;
                var val = UnquoteValue(StripInlineComment(trimmed)["rcon_enabled ".Length..]);
                if (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    outLines.Add(rawLine);
                }
                else
                {
                    outLines.Add("rcon_enabled \"1\"");
                    changed = true;
                }
                continue;
            }

            if (trimmed.StartsWith("rcon_password ", StringComparison.OrdinalIgnoreCase))
            {
                sawSecret = true;
                var secretVal = UnquoteValue(StripInlineComment(trimmed)["rcon_password ".Length..]);
                if (!string.IsNullOrWhiteSpace(secretVal))
                {
                    hasNonEmptySecret = true;
                    outLines.Add(rawLine);
                }
                else if (!string.IsNullOrWhiteSpace(fallbackSecret))
                {
                    outLines.Add($"rcon_password \"{SanitizeSecretValue(fallbackSecret)}\"");
                    hasNonEmptySecret = true;
                    changed = true;
                }
                else
                {
                    outLines.Add(rawLine);
                }
                continue;
            }

            outLines.Add(rawLine);
        }

        // Drop trailing empty placeholder split line if original ended with newline
        if (outLines.Count > 0 && string.IsNullOrEmpty(outLines[^1]))
            outLines.RemoveAt(outLines.Count - 1);

        if (!sawEnabled)
        {
            outLines.Add("rcon_enabled \"1\"");
            changed = true;
        }

        if (!sawSecret && !string.IsNullOrWhiteSpace(fallbackSecret))
        {
            outLines.Add($"rcon_password \"{SanitizeSecretValue(fallbackSecret)}\"");
            hasNonEmptySecret = true;
            changed = true;
        }

        return (string.Join(Environment.NewLine, outLines) + Environment.NewLine, changed, hasNonEmptySecret);
    }

    private static string StripInlineComment(string trimmedLine)
    {
        int idx = trimmedLine.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? trimmedLine[..idx].Trim() : trimmedLine;
    }

    private static string SanitizeSecretValue(string secret) =>
        new string(secret.Where(c => !char.IsControl(c) && c is not ('"' or ';')).ToArray()).Trim();

    private static string UnquoteValue(string raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }
}
