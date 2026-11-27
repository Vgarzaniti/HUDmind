using System.Text.Json;

namespace HUDMind.Core.Pipeline;

/// <summary>
/// Vendor-agnostic normalizer for passive local loopback WebSocket game-state telemetry JSON feeds
/// into concise deterministic LOCAL_WS_TELEMETRY snapshots for TacticalSignalEvaluator.
/// </summary>
public static class ExternalTelemetryParser
{
    public const string TelemetryPrefix = "LOCAL_WS_TELEMETRY:";

    public static bool TryParseStateEventJson(string? json, out string telemetrySummary)
    {
        telemetrySummary = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evProp))
                return false;

            string ev = evProp.GetString() ?? string.Empty;
            if (string.Equals(ev, "game:match_ended", StringComparison.OrdinalIgnoreCase))
            {
                telemetrySummary = $"{TelemetryPrefix} InReplay=0, MatchEnded=1";
                return true;
            }

            if (string.Equals(ev, "game:goal_scored", StringComparison.OrdinalIgnoreCase))
            {
                telemetrySummary = $"{TelemetryPrefix} InReplay=1, GoalScored=1";
                return true;
            }

            if (!string.Equals(ev, "game:update_state", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("game", out var game))
            {
                return false;
            }

            bool isReplay = TryGetBool(game, "isReplay");
            bool hasWinner = TryGetBool(game, "hasWinner");
            bool isOt = TryGetBool(game, "isOT");
            int timeSec = Math.Max(0, TryGetInt(game, "time_seconds"));
            string clock = $"{timeSec / 60:00}:{timeSec % 60:00}";

            int team0Score = 0;
            int team1Score = 0;
            if (game.TryGetProperty("teams", out var teams) && teams.ValueKind == JsonValueKind.Array)
            {
                if (teams.GetArrayLength() > 0) team0Score = TryGetInt(teams[0], "score");
                if (teams.GetArrayLength() > 1) team1Score = TryGetInt(teams[1], "score");
            }

            int resourcePct = -1;
            int speed = -1;
            string targetId = game.TryGetProperty("target", out var tgt) ? (tgt.GetString() ?? string.Empty) : string.Empty;
            if (data.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
            {
                JsonElement picked = default;
                if (!string.IsNullOrEmpty(targetId) && players.TryGetProperty(targetId, out var byTarget))
                {
                    picked = byTarget;
                }
                else
                {
                    foreach (var p in players.EnumerateObject())
                    {
                        picked = p.Value;
                        break;
                    }
                }

                if (picked.ValueKind == JsonValueKind.Object)
                {
                    resourcePct = Math.Clamp(TryGetInt(picked, "boost", -1), 0, 100);
                    speed = Math.Max(0, TryGetInt(picked, "speed", -1));
                }
            }

            var parts = new List<string>(6);
            if (resourcePct >= 0) parts.Add($"Resource={resourcePct}%");
            if (speed >= 0) parts.Add($"Speed={speed}");
            parts.Add($"Clock={clock}");
            parts.Add($"Score={team0Score}-{team1Score}");
            parts.Add($"InReplay={(isReplay ? 1 : 0)}");
            parts.Add($"OT={(isOt ? 1 : 0)}");
            if (hasWinner) parts.Add("MatchEnded=1");

            telemetrySummary = $"{TelemetryPrefix} {string.Join(", ", parts)}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetBool(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False && el.GetBoolean();

    private static int TryGetInt(JsonElement obj, string prop, int fallback = 0)
    {
        if (!obj.TryGetProperty(prop, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt32(out int iv)) return iv;
            if (el.TryGetDouble(out double dv)) return (int)Math.Round(dv);
        }
        return fallback;
    }
}
