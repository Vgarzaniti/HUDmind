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
        bool forceAdvice,
        string spatialTelemetry = "")
    {
        var lang = (config.VoiceLanguage ?? config.TextLanguage ?? "es").Trim().ToLowerInvariant();
        var langTag = lang == "en" ? "en (concise Discord coach)" : "es (español natural, estilo coach Discord)";

        var modeRule = config.CoachMode.Trim().ToLowerInvariant() switch
        {
            "training" => "TRAINING: coach mechanics & positioning.",
            "auto" => "AUTO: menu/lobby->NONE; training->mechanics; match->rotation, teammates, resources & clock.",
            _ => "MATCH: coach rotation, teammates, resources & clock; menu->NONE."
        };

        var sb = new StringBuilder(320);
        sb.Append("Live eSports coach for ").Append(SanitizeInline(profile.Manifest.DisplayName, 48))
          .Append(". Lang: ").Append(langTag).Append(".\n");
        sb.Append(modeRule).Append('\n');
        sb.Append("Improvise 1 macro/positioning/economy tip valid 10-25s referencing 1 real HUD detail (never robotic/textbook).\n");

        if (!string.IsNullOrWhiteSpace(profile.TacticalSummary))
            sb.Append("Focus: ").Append(SanitizeInline(profile.TacticalSummary, 180)).Append('\n');

        if (!string.IsNullOrWhiteSpace(profile.CustomRules))
            sb.Append("Player rules: ").Append(SanitizeInline(profile.CustomRules, Config.ConfigRepository.MaxCustomRulesChars)).Append('\n');

        if (!string.IsNullOrWhiteSpace(spatialTelemetry))
            sb.Append("HUD_SCAN: ").Append(SanitizeInline(spatialTelemetry, 200)).Append('\n');

        if (forceAdvice)
            sb.Append("F10 MANUAL: give best tactical tip NOW (do not reply NONE).\n");

        if (!string.IsNullOrWhiteSpace(recentHistory))
            sb.Append("No repeat: ").Append(SanitizeInline(recentHistory, 220)).Append('\n');

        sb.Append("Output STRICTLY 1 line: H|<max 8 words> OR M|<max 8 words> OR L|<max 8 words> OR NONE.");
        return sb.ToString();
    }

    private static string SanitizeInline(string raw, int maxLen)
    {
        var clean = string.Join(' ', raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        return clean.Length <= maxLen ? clean : clean[..maxLen];
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
                return new CoachResponse(true, urg, Truncate(callout, 115), Truncate(callout, 170), ctx);
            }
        }

        var cleaned = firstLine.Trim('"', '\'', '`', ' ');
        if (cleaned.Length is >= 4 and <= 135 && !cleaned.StartsWith('{'))
            return new CoachResponse(true, UrgencyLevel.Medium, Truncate(cleaned, 115), cleaned);

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
