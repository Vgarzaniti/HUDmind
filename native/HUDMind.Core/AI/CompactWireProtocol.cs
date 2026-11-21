using System.Text;
using System.Text.Json;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

public static class CompactWireProtocol
{
    public static string BuildRealtimePrompt(
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice)
    {
        var lang = (config.VoiceLanguage ?? config.TextLanguage ?? "es").Trim().ToLowerInvariant();
        var langDesc = lang == "en"
            ? "English (concise, natural Discord eSports coach)"
            : "Spanish (español natural y directo, estilo coach en Discord)";

        var modeRule = config.CoachMode.Trim().ToLowerInvariant() switch
        {
            "training" => "Mode=TRAINING: coach mechanics, positioning & consistency like a live trainer.",
            "auto" => "Auto-detect: if menu/lobby/loading -> NONE; if training/free play -> coach mechanics; if live match -> coach rotation, teammates, resources & clock.",
            _ => "Mode=MATCH: coach rotation, teammates, resources & score/clock. If menu -> NONE."
        };

        var sb = new StringBuilder(384);
        sb.Append("Live human eSports coach watching ").Append(profile.Manifest.DisplayName).Append(". Language: ").Append(langDesc).Append(".\n");
        sb.Append(modeRule).Append('\n');
        sb.Append("Give 1 improvised macro/positioning/economy callout useful for the next 10-25s (never late split-second reflexes, never robotic textbook phrasing). Reference 1 concrete HUD/player detail you see.\n");

        if (!string.IsNullOrWhiteSpace(profile.CustomRules))
        {
            var trimmed = profile.CustomRules.Trim();
            if (trimmed.Length > 260)
                trimmed = trimmed[..260];
            sb.Append("Player rules: ").Append(trimmed).Append('\n');
        }

        if (forceAdvice)
            sb.Append("PLAYER PRESSED F10: give your best tactical read NOW (do not reply NONE).\n");

        if (!string.IsNullOrWhiteSpace(recentHistory))
            sb.Append("Do NOT repeat: ").Append(recentHistory).Append('\n');

        sb.Append("Reply STRICTLY 1 line: H|<max 8 words> OR M|<max 8 words> OR L|<max 8 words> OR NONE.");
        return sb.ToString();
    }

    public static CoachResponse? ParseCompactLine(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        var text = rawText.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var cleanLines = text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal));
            text = string.Join('\n', cleanLines).Trim();
        }
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // JSON fallback if a provider returns JSON
        if (text.StartsWith('{'))
        {
            var jsonParsed = TryParseJson(text);
            if (jsonParsed is not null)
                return jsonParsed;
        }

        var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (string.IsNullOrEmpty(firstLine))
            return null;

        var upper = firstLine.Trim(' ', '.', '!', '"', '\'', '`').ToUpperInvariant();
        if (upper is "NONE" or "NO" or "SILENCE" or "SKIP" or "NULL")
            return CoachResponse.Silent();

        if (firstLine.Contains('|'))
        {
            var parts = firstLine.Split('|').Select(s => s.Trim().Trim('"', '\'', '`')).ToArray();
            if (parts.Length >= 2 && TryMapUrgency(parts[0], out var urg))
            {
                var callout = parts[1];
                if (string.IsNullOrWhiteSpace(callout) || callout.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    return CoachResponse.Silent();

                var ctx = parts.Length >= 3 ? Truncate(parts[2], 150) : string.Empty;
                var shortCallout = Truncate(callout, 115);
                return new CoachResponse(true, urg, shortCallout, Truncate(callout, 170), ctx);
            }
        }

        // Graceful fallback for direct single-line coach sentence
        var cleaned = firstLine.Trim('"', '\'', '`', ' ');
        if (cleaned.Length is >= 4 and <= 135 && !cleaned.StartsWith('{'))
        {
            return new CoachResponse(true, UrgencyLevel.Medium, Truncate(cleaned, 115), cleaned);
        }

        return null;
    }

    private static bool TryMapUrgency(string token, out UrgencyLevel urgency)
    {
        switch (token.Trim().ToUpperInvariant())
        {
            case "H" or "HIGH" or "ALTA":
                urgency = UrgencyLevel.High;
                return true;
            case "M" or "MED" or "MEDIUM" or "MEDIA":
                urgency = UrgencyLevel.Medium;
                return true;
            case "L" or "LOW" or "BAJA":
                urgency = UrgencyLevel.Low;
                return true;
            default:
                urgency = UrgencyLevel.Medium;
                return false;
        }
    }

    private static CoachResponse? TryParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool intervene = root.TryGetProperty("should_intervene", out var si) && si.ValueKind == JsonValueKind.True;
            var overlay = root.TryGetProperty("overlay_text", out var ov) ? ov.GetString() ?? string.Empty : string.Empty;
            var voice = root.TryGetProperty("voice_line", out var vl) ? vl.GetString() ?? overlay : overlay;
            var ctx = root.TryGetProperty("match_context", out var mc) ? mc.GetString() ?? string.Empty : string.Empty;
            var urgRaw = root.TryGetProperty("urgency", out var ug) ? ug.GetString() ?? "medium" : "medium";
            TryMapUrgency(urgRaw, out var urgency);
            return new CoachResponse(intervene, urgency, Truncate(overlay, 115), Truncate(voice, 175), Truncate(ctx, 155));
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen - 3), "...");
}
