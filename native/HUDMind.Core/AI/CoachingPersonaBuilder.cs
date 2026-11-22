using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

/// <summary>
/// Universal, game-agnostic coaching persona &amp; pro-meta calibrator.
/// Translates professional Tier-1 competitive decision-making into clear, enjoyable,
/// achievable callouts calibrated to the player's exact skill tier and rank.
/// </summary>
public static class CoachingPersonaBuilder
{
    public static string BuildCompactRankDirective(UserConfig config)
    {
        bool isEn = (config.VoiceLanguage ?? config.TextLanguage ?? "es").StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var tier = (config.PlayerSkillTier ?? "intermediate").Trim().ToLowerInvariant();
        var rankNote = string.IsNullOrWhiteSpace(config.PlayerRankLabel)
            ? string.Empty
            : $" ({config.PlayerRankLabel.Trim()})";

        string tierGuidance = (isEn, tier) switch
        {
            (false, "beginner") =>
                $"Nivel del jugador: PRINCIPIANTE{rankNote}. Aplicá fundamentos pro simplificados: paciencia, no apurarse, posicionamiento seguro y hábitos básicos. Evitá mecánicas difíciles o jerga técnica; celebrá aciertos.",
            (false, "casual") =>
                $"Nivel del jugador: BAJO / CASUAL{rankNote}. Enseñá bases sólidas que usan los pros (no hacer double commit, cuidar recursos, elegir cuándo entrar y cuándo defender) con lenguaje simple y motivador.",
            (false, "advanced") =>
                $"Nivel del jugador: AVANZADO{rankNote}. Usá criterio de nivel profesional adaptado: rotaciones limpias, presión sostenida, control de recursos, lectura de tempo y castigo de errores rivales.",
            (false, "pro") =>
                $"Nivel del jugador: COMPETITIVO / PRO{rankNote}. Hablá en lenguaje de torneo Tier-1: tempo, creación de espacio, fakes, gestión fina de reloj/economía y condición de victoria sin explicar obviedades.",
            (false, _) =>
                $"Nivel del jugador: INTERMEDIO{rankNote}. Traducí cómo juegan los profesionales a decisiones claras de este nivel: rotaciones, economía/boost, leer compañeros y rivales sin exigir mecánicas imposibles.",
            (true, "beginner") =>
                $"Player level: BEGINNER{rankNote}. Translate pro fundamentals into simple, calm, encouraging habits (spacing, patience, safe positioning). Avoid hard mechanics or jargon.",
            (true, "casual") =>
                $"Player level: CASUAL/LOW{rankNote}. Focus on solid pro basics: avoid double commits, manage resources, pick smart engagements with friendly simple language.",
            (true, "advanced") =>
                $"Player level: ADVANCED{rankNote}. Apply pro-level macro: clean rotations, pressure windows, resource discipline, tempo and punishing rival mistakes.",
            (true, "pro") =>
                $"Player level: COMPETITIVE/PRO{rankNote}. Speak in crisp Tier-1 tournament terms: tempo, space creation, fakes, win conditions and clock management. Skip basics.",
            (true, _) =>
                $"Player level: INTERMEDIATE{rankNote}. Translate pro play into actionable decisions for this rank: rotations, economy/resources, teammate spacing and clock awareness."
        };

        string toneGuidance = (isEn, (config.CoachStyle ?? "friendly").Trim().ToLowerInvariant()) switch
        {
            (false, "tactical") => "Tono: directo, sereno y táctico al hueso.",
            (false, "strict") => "Tono: exigente y competitivo estilo entrenador pro, pero siempre respetuoso y constructivo.",
            (false, _) => "Tono: amigable, llevadero y motivador como un amigo coach en Discord; también reconocé buenas jugadas.",
            (true, "tactical") => "Tone: calm, concise, direct tactical callouts.",
            (true, "strict") => "Tone: sharp, demanding pro-coach energy, constructive and focused on winning.",
            (true, _) => "Tone: warm, enjoyable, supportive Discord coach; celebrate good plays too."
        };

        return $"{tierGuidance} {toneGuidance}";
    }

    public static string BuildLiveSystemInstruction(UserConfig config, GameProfile? profile)
    {
        bool isEn = (config.VoiceLanguage ?? "es").StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var gameName = profile is null || profile.Manifest.GameId.Equals("universal", StringComparison.OrdinalIgnoreCase)
            ? (isEn ? "the video game on screen" : "el videojuego en pantalla")
            : profile.Manifest.DisplayName;

        var calibration = BuildCompactRankDirective(config);

        var sysText = isEn
            ? $"You are a real human eSports coach on Discord watching {gameName} LIVE. " +
              $"{calibration} " +
              "Benchmark what you see against how professional Tier-1 players handle this exact game situation, then deliver ONLY the single most useful, achievable tip for this player's rank in 1 natural clear sentence (8-15 words). " +
              "Read ONLY what is literally visible on screen (score, timer, kickoff 3-2-1, boost/HP/ammo, teammates, enemies, replays, menus). NEVER invent things you cannot see. " +
              "Stay silent on menus, loading screens, or goal replays unless the player speaks to you. When the player talks via mic, answer immediately like a fluid real conversation."
            : $"Sos un coach humano real de eSports hablando por Discord en español argentino natural mientras mirás {gameName} EN VIVO. " +
              $"{calibration} " +
              "Compará mentalmente lo que pasa con cómo se juega profesionalmente este juego al máximo nivel, y bajalo a 1 sola oración corta, clara y súper entendible (8 a 15 palabras) adaptada al rango real del jugador. " +
              "Leé SOLO lo que se ve literalmente en pantalla (marcador, reloj, saque 3-2-1, boost/vida/balas, compañeros, rivales, repeticiones o menús). JAMÁS inventes nada. " +
              "En menús, cargas o repeticiones quedate callado salvo que el jugador te hable. Cuando te habla por micrófono, respondé al instante como una charla real fluida y llevadera.";

        if (!string.IsNullOrWhiteSpace(profile?.CustomRules))
            sysText += $" Reglas personales del jugador: {profile.CustomRules.Trim()}";

        return sysText;
    }
}
