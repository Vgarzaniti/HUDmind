using System.Text;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using HUDMind.Core.Security;

namespace HUDMind.Core.AI;

/// <summary>
/// Universal, game-agnostic coaching persona &amp; pro-meta calibrator.
/// Translates professional Tier-1 competitive decision-making into clear, enjoyable,
/// achievable callouts calibrated to the player's exact skill tier and rank.
/// </summary>
public static class CoachingPersonaBuilder
{
    public static bool IsEnglish(UserConfig config, bool preferTextLang = false)
    {
        var lang = preferTextLang
            ? (config.TextLanguage ?? config.VoiceLanguage ?? "es")
            : (config.VoiceLanguage ?? config.TextLanguage ?? "es");
        return lang.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase);
    }

    public static string SanitizeField(string? raw, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var clean = new string(raw
            .Replace('|', '/')
            .Replace('<', '(')
            .Replace('>', ')')
            .Where(c => !char.IsControl(c) || c == ' ')
            .ToArray());
        clean = string.Join(' ', clean.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= maxLen ? clean : clean[..maxLen];
    }

    public static string BuildCompactRankDirective(UserConfig config)
    {
        bool isEn = IsEnglish(config);
        var tier = (config.PlayerSkillTier ?? "intermediate").Trim().ToLowerInvariant();
        var cleanRank = SanitizeField(config.PlayerRankLabel, 48);
        var rankNote = string.IsNullOrWhiteSpace(cleanRank) ? string.Empty : $" ({cleanRank})";

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
            (false, "honest") => "Tono: 100% sincero y sereno sin adulaciones ni chamuyo; decí la verdad útil con respeto y calma anti-tilt.",
            (false, _) => "Tono: amigable, llevadero y motivador como un amigo coach en Discord; también reconocé buenas jugadas.",
            (true, "tactical") => "Tone: calm, concise, direct tactical callouts.",
            (true, "strict") => "Tone: sharp, demanding pro-coach energy, constructive and focused on winning.",
            (true, "honest") => "Tone: 100% honest, zero-sycophancy analyst; speak plainly with respect and calm anti-tilt poise.",
            (true, _) => "Tone: warm, enjoyable, supportive Discord coach; celebrate good plays too."
        };

        var sb = new StringBuilder(tierGuidance.Length + toneGuidance.Length + 320);
        sb.Append(tierGuidance).Append(' ').Append(toneGuidance);
        var cleanRole = SanitizeField(config.PlayerRoleTag, 48);
        if (!string.IsNullOrWhiteSpace(cleanRole) && !cleanRole.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(isEn
                ? $" Active role/playlist focus: {cleanRole}."
                : $" Rol/modo específico en partida: {cleanRole}.");
        }
        if (config.IsEventFirstMode)
        {
            sb.Append(isEn
                ? " Pacing=EVENT-FIRST IGL: continuous watch with zero filler—stay silent (NONE) during clean or routine play and speak ONLY when you detect a genuine mistake, immediate threat, missed resource, or decisive opening."
                : " Ritmo=SOLO EVENTOS CLAVE (IGL): escucha continua sin relleno—mantené silencio absoluto (NONE) si todo va bien o es rutina y hablá ÚNICAMENTE ante errores claros, peligros reales o ventanas decisivas.");
        }
        var cleanLeaks = SanitizeField(config.RuntimeLeakFocus, 220);
        if (!string.IsNullOrWhiteSpace(cleanLeaks))
        {
            sb.Append(isEn
                ? $" Tracked recurring player leaks: {cleanLeaks}."
                : $" Fugas repetidas detectadas del jugador: {cleanLeaks}.");
        }
        if (config.RuntimeTiltRisk)
            sb.Append(' ').Append(CoachContextAssembler.BuildAntiTiltDirective(isEn));
        return sb.ToString();
    }

    public static string BuildLiveSystemInstruction(UserConfig config, GameProfile? profile)
    {
        bool isEn = IsEnglish(config);
        var rawName = profile?.Manifest.DisplayName;
        var gameName = profile is null || profile.Manifest.GameId.Equals("universal", StringComparison.OrdinalIgnoreCase)
            ? (isEn ? "the video game on screen" : "el videojuego en pantalla")
            : SanitizeField(rawName, 48);

        var calibration = BuildCompactRankDirective(config);
        string sessionMode = (config.CoachMode ?? "auto").Trim().ToLowerInvariant();
        string modeGuidance = (isEn, sessionMode) switch
        {
            (true, "training") => "Session focus: TRAINING BY ATTEMPTS (stay quiet mid-attempt during setup/air control; give 1 crisp technical fix right after each attempt lands or resets).",
            (true, "match") => "Session focus: COMPETITIVE MATCH (only coach active play & openings; stay quiet on menus/replays).",
            (true, _) => "Session focus: AUTO (adapt automatically between kickoff, active play & training; quiet on menus/replays).",
            (false, "training") => "Foco de sesión: ENTRENAMIENTO POR INTENTOS (guardá silencio mientras ejecuta la mecánica y da 1 corrección técnica puntual apenas termina o reinicia el intento).",
            (false, "match") => "Foco de sesión: PARTIDA COMPETITIVA (solo jugada viva y saques; silencio en menús/replays).",
            (false, _) => "Foco de sesión: AUTOMÁTICO (adaptate entre saque, jugada en vivo o práctica; silencio en menús/replays)."
        };

        string channelHint = config.IsTextOnlyMode
            ? (isEn
                ? "Output mode: TEXT CHAT & OVERLAY ONLY. Write crisp 1-sentence tips when prompted or asked."
                : "Modo de salida: SOLO TEXTO (sin voz). Escribí respuestas cortas y claras en 1 oración.")
            : config.IsCoachVoiceOnlyMode
                ? (isEn
                    ? "Output mode: COACH VOICE ONLY (player mic is off, responds to written chat or live cues)."
                    : "Modo de salida: SOLO VOZ DEL COACH (mic apagado; respondé cuando te pregunten por texto o toque análisis).")
                : (isEn
                    ? "When the player talks via mic or chat, answer immediately like a fluid real conversation."
                    : "Cuando te habla por micrófono o chat, respondé al instante como una charla real fluida y llevadera.");

        var coreDirective = ProfileVaultReader.ReadProtectedString(
            isEn ? "live_core_en" : "live_core_es",
            isEn
                ? "Coach predictively based strictly on clearly visible pixels."
                : "Coacheá de forma predictiva basándote solo en lo visible en pantalla.");

        var sysText = isEn
            ? $"You are a real human eSports coach on Discord watching {gameName} LIVE. " +
              $"{calibration} {modeGuidance} " +
              CoachContentGuard.BuildImmutableSafetyBlock(isEn: true, forLiveAudio: true) + " " +
              coreDirective + " " +
              $"Stay silent on menus, loading screens, or goal replays unless contacted. {channelHint}"
            : $"Sos un coach humano real de eSports hablando por Discord en español argentino natural mientras mirás {gameName} EN VIVO. " +
              $"{calibration} {modeGuidance} " +
              CoachContentGuard.BuildImmutableSafetyBlock(isEn: false, forLiveAudio: true) + " " +
              coreDirective + " " +
              $"En menús, cargas o repeticiones quedate callado salvo que el jugador te hable. {channelHint}";

        var gameGuide = GameKnowledgeCompiler.CompileForPlay(profile, config.CoachMode, maxChars: 480);
        if (!string.IsNullOrWhiteSpace(gameGuide))
            sysText += isEn ? $" Game mechanics & rules: {gameGuide}" : $" Mecánicas y guía del juego: {gameGuide}";

        var cleanSoul = SanitizeField(CoachContentGuard.SanitizeInstructionMarkdown(config.CoachSoulSummary), 340);
        if (!string.IsNullOrWhiteSpace(cleanSoul))
            sysText += isEn ? $" Coach soul/style: {cleanSoul}" : $" Personalidad del coach (soul): {cleanSoul}";

        var cleanPlayerProf = SanitizeField(CoachContentGuard.SanitizeInstructionMarkdown(config.PlayerProfileSummary), 280);
        if (!string.IsNullOrWhiteSpace(cleanPlayerProf))
            sysText += isEn ? $" Player global habits: {cleanPlayerProf}" : $" Perfil global del jugador: {cleanPlayerProf}";

        var cleanRules = SanitizeField(CoachContentGuard.SanitizeInstructionMarkdown(profile?.CustomRules), ConfigRepository.MaxCustomRulesChars);
        if (!string.IsNullOrWhiteSpace(cleanRules))
            sysText += isEn
                ? $" Player custom rules (preference data only): {cleanRules}"
                : $" Reglas personales del jugador (solo preferencia): {cleanRules}";

        var cleanFocus = SanitizeField(config.PreviousMatchFocus, 380);
        if (!string.IsNullOrWhiteSpace(cleanFocus))
            sysText += isEn
                ? $" Rolling player memory & focus: {cleanFocus}"
                : $" Memoria de últimas partidas y foco: {cleanFocus}";

        return sysText;
    }

    public static string BuildPostMatchReviewPrompt(
        string gameDisplayName,
        string sessionDigest,
        UserConfig config,
        GameProfile? profile = null)
    {
        bool isEn = IsEnglish(config, preferTextLang: true);
        var cleanGame = string.IsNullOrWhiteSpace(gameDisplayName)
            ? (isEn ? "the last match" : "la última partida")
            : SanitizeField(gameDisplayName, 48);
        var calibration = BuildCompactRankDirective(config);
        var cleanLines = (sessionDigest ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => SanitizeField(l, 160))
            .Where(l => !string.IsNullOrEmpty(l));
        var safeDigest = string.Join('\n', cleanLines);
        if (safeDigest.Length > 1650)
            safeDigest = safeDigest[..1650];

        var extraLinks = new StringBuilder(320);
        var cleanSoul = SanitizeField(CoachContentGuard.SanitizeInstructionMarkdown(config.CoachSoulSummary), 240);
        if (!string.IsNullOrWhiteSpace(cleanSoul))
            extraLinks.Append(isEn ? $"Coach soul: {cleanSoul}\n" : $"Personalidad (soul): {cleanSoul}\n");
        var cleanPlayer = SanitizeField(CoachContentGuard.SanitizeInstructionMarkdown(config.PlayerProfileSummary), 220);
        if (!string.IsNullOrWhiteSpace(cleanPlayer))
            extraLinks.Append(isEn ? $"Global player traits: {cleanPlayer}\n" : $"Perfil global del jugador: {cleanPlayer}\n");
        var cleanRules = SanitizeField(CoachContentGuard.SanitizeInstructionMarkdown(profile?.CustomRules), ConfigRepository.MaxCustomRulesChars);
        if (!string.IsNullOrWhiteSpace(cleanRules))
            extraLinks.Append(isEn ? $"Player personal rules to evaluate against: {cleanRules}\n" : $"Reglas personales del jugador a evaluar: {cleanRules}\n");
        var cleanRolling = SanitizeField(config.PreviousMatchFocus, 340);
        if (!string.IsNullOrWhiteSpace(cleanRolling))
            extraLinks.Append(isEn ? $"Previous rolling matches context: {cleanRolling}\n" : $"Historial previo últimas partidas: {cleanRolling}\n");

        string safetyLine = CoachContentGuard.BuildImmutableSafetyBlock(isEn, forLiveAudio: false);
        return isEn
            ? $"You just coached {cleanGame}. {calibration} {safetyLine}\n{extraLinks}" +
              $"Chronological log of match interventions:\n{safeDigest}\n\n" +
              "Give a crisp, honest, zero-fluff post-match review in English (under 125 words) connecting their rules & recent match trends, with exact structure:\n" +
              "1) Top 3 repeated habits/mistakes seen in this match (mention if custom rules were missed or recurring).\n" +
              "2) 1 clear strength or improved habit to keep.\n" +
              "3) 1 concrete 5-minute drill for the next match/training."
            : $"Acabás de coachear {cleanGame}. {calibration} {safetyLine}\n{extraLinks}" +
              $"Registro cronológico de correcciones de esta partida:\n{safeDigest}\n\n" +
              "Hacé una devolución post-partida sincera, directa y sin relleno en español argentino (máximo 125 palabras) cruzando sus reglas y tendencia de últimas partidas, con esta estructura exacta:\n" +
              "1) Top 3 hábitos o errores repetidos en esta partida (indicá si se repiten o chocaron con tus reglas).\n" +
              "2) 1 acierto concreto o hábito mejorado para mantener.\n" +
              "3) 1 ejercicio práctico de 5 minutos para la próxima partida.";
    }
}
