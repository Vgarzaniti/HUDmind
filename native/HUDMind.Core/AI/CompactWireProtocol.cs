using System.Text;
using System.Text.Json;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

public static class CompactWireProtocol
{
    private static readonly HashSet<string> SilentPhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "MENU", "LOBBY", "LOADING", "REPLAY", "PAUSE", "PAUSED", "SCOREBOARD", "DEAD", "DESKTOP"
    };

    private static readonly HashSet<string> KnownPhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "MENU", "LOBBY", "LOADING", "REPLAY", "PAUSE", "PAUSED", "SCOREBOARD", "DEAD", "DESKTOP",
        "KICKOFF", "ROUND_START", "TRAINING", "PLAY", "MATCH"
    };

    public static string BuildRealtimePrompt(
        GameProfile profile,
        UserConfig config,
        string recentHistory,
        bool forceAdvice,
        string visualTimeline = "")
    {
        var lang = (config.VoiceLanguage ?? config.TextLanguage ?? "es").Trim().ToLowerInvariant();
        bool isEn = lang == "en";

        var gameLabel = profile.Manifest.GameId.Equals("universal", StringComparison.OrdinalIgnoreCase)
            ? "the video game currently on screen"
            : SanitizeInline(profile.Manifest.DisplayName, 48);

        var modeNote = config.CoachMode.Trim().ToLowerInvariant() switch
        {
            "training" => isEn
                ? "Mode=TRAINING: coach mechanics, spacing & consistency naturally."
                : "Modo=ENTRENAMIENTO: guiá mecánicas, control y posicionamiento como entrenador real.",
            "auto" => isEn
                ? "Auto-detect phase from screen: MENU/LOADING/REPLAY/DESKTOP -> NONE; KICKOFF/ROUND_START -> opening/kickoff read; TRAINING -> mechanics; PLAY -> live match coaching."
                : "Detectá solo la FASE real en pantalla: MENU/LOADING/REPLAY/DESKTOP -> NONE (silencio); KICKOFF (saque/cuenta regresiva/inicio de ronda) -> lectura de saque/apertura; TRAINING -> entrenamiento; PLAY -> partida en vivo.",
            _ => isEn
                ? "Mode=MATCH: only coach live gameplay or kickoff/round start; MENU/REPLAY/LOADING -> NONE."
                : "Modo=PARTIDA: solo hablá en partida o saque; en MENU/REPLAY/LOADING devolvé NONE."
        };

        var sb = new StringBuilder(680);
        if (isEn)
        {
            sb.Append("You are a calm, sharp human eSports coach watching ").Append(gameLabel).Append(" LIVE on Discord (100% generic across any game).\n");
            sb.Append(modeNote).Append('\n');
            sb.Append("CRITICAL RULES:\n");
            sb.Append("1) OBSERVE FIRST: Read ONLY what is literally visible (scoreboard, timer, countdown, boost/HP/ammo/resources, ball/teammates/enemies). NEVER invent numbers or events you do not clearly see.\n");
            sb.Append("2) SILENCE OVER NONSENSE: If it is a menu, loading screen, goal replay, pause, desktop, or nothing clearly useful to say -> reply NONE.\n");
            sb.Append("3) CLEAR HUMAN SPEECH: Write 1 complete, natural, easy-to-understand sentence (10-16 words) that makes immediate tactical sense for the next 10-20 seconds (positioning, kickoff role, resource economy, clock/score). Never cryptic 4-word fragments.\n");
        }
        else
        {
            sb.Append("Sos un coach humano real de eSports mirando ").Append(gameLabel).Append(" EN VIVO por Discord (100% genérico para cualquier juego).\n");
            sb.Append(modeNote).Append('\n');
            sb.Append("REGLAS CRÍTICAS:\n");
            sb.Append("1) OBSERVÁ PRIMERO: Leé SOLO lo que se ve literalmente en pantalla (marcador, reloj, cuenta regresiva de saque, boost/vida/munición/recursos, pelota, compañeros o rivales). JAMÁS inventes números ni jugadas que no veas claras.\n");
            sb.Append("2) SILENCIO ANTES QUE DECIR CUALQUIER COSA: Si es menú, pantalla de carga, repetición de gol (REPLAY), pausa, escritorio o no hay nada claro y útil -> usá NONE.\n");
            sb.Append("3) CONSEJO CLARO Y ENTENDIBLE: Escribí 1 sola oración completa en español argentino natural (10 a 16 palabras) que se entienda perfecto al escucharla y sirva para los próximos 10-20s (saque, rotación, recursos, reloj/marcador). Nunca frases cortadas ni robóticas.\n");
        }

        if (!string.IsNullOrWhiteSpace(profile.CustomRules))
            sb.Append("Reglas del jugador: ").Append(SanitizeInline(profile.CustomRules, Config.ConfigRepository.MaxCustomRulesChars)).Append('\n');

        if (!string.IsNullOrWhiteSpace(visualTimeline))
            sb.Append("Contexto reciente de la partida: ").Append(SanitizeInline(visualTimeline, 300)).Append('\n');

        if (!string.IsNullOrWhiteSpace(recentHistory))
            sb.Append("No repitas estos consejos ya dados: ").Append(SanitizeInline(recentHistory, 240)).Append('\n');

        if (forceAdvice)
            sb.Append("SOLICITUD MANUAL (F10): el jugador pidió una lectura táctica clara de esta situación exacta ahora.\n");

        sb.Append("Formato ESTRICTO en 1 sola línea: FASE|qué ves en 6-10 palabras|H/M/L/NONE|consejo claro completo (vacío si NONE)");
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

            // 4-field grounded generic protocol: FASE | QUE_VEO | URGENCIA/NONE | CONSEJO
            if (parts.Length >= 3 && KnownPhases.Contains(parts[0]))
            {
                var phase = parts[0].ToUpperInvariant();
                var whatISee = parts[1];
                var ctxTag = $"[{phase}] {whatISee}".Trim();
                var urgToken = parts[2].ToUpperInvariant();
                var advice = parts.Length >= 4 ? parts[3] : string.Empty;

                if (SilentPhases.Contains(phase) || urgToken is "NONE" or "NO" or "SILENCE" or "" || string.IsNullOrWhiteSpace(advice) || advice.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    return CoachResponse.Silent(Truncate(ctxTag, 155));

                if (!TryMapUrgency(urgToken, out var urgency))
                    urgency = UrgencyLevel.Medium;

                return new CoachResponse(
                    true,
                    urgency,
                    Truncate(advice, 130),
                    Truncate(advice, 190),
                    Truncate(ctxTag, 155));
            }

            // Legacy 2/3-field compact protocol: H/M/L | CONSEJO [| CTX]
            if (parts.Length >= 2 && TryMapUrgency(parts[0], out var legacyUrg))
            {
                var callout = parts[1];
                if (string.IsNullOrWhiteSpace(callout) || callout.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    return CoachResponse.Silent();

                var ctx = parts.Length >= 3 ? Truncate(parts[2], 150) : string.Empty;
                return new CoachResponse(true, legacyUrg, Truncate(callout, 130), Truncate(callout, 190), ctx);
            }
        }

        var cleaned = firstLine.Trim('"', '\'', '`', ' ');
        if (cleaned.Length is >= 8 and <= 160 && !cleaned.StartsWith('{') && !cleaned.Contains('|'))
            return new CoachResponse(true, UrgencyLevel.Medium, Truncate(cleaned, 130), cleaned);

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
            return new CoachResponse(intervene, urgency, Truncate(overlay, 130), Truncate(voice, 190), Truncate(ctx, 155));
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen - 3), "...");
}
