using System.Globalization;
using System.Text.RegularExpressions;
using HUDMind.Core.Models;

namespace HUDMind.Core.Pipeline;

public sealed record TacticalEvaluation(
    string PhaseTag,
    bool ShouldSilence,
    bool ShouldDelayNonUrgent,
    bool TriggerImmediate,
    bool MatchEnded,
    double CooldownMultiplier,
    string DeterministicFact
)
{
    public static TacticalEvaluation Neutral { get; } = new(
        PhaseTag: "PLAY",
        ShouldSilence: false,
        ShouldDelayNonUrgent: false,
        TriggerImmediate: false,
        MatchEnded: false,
        CooldownMultiplier: 1.0,
        DeterministicFact: string.Empty);
}

/// <summary>
/// Tracks edge transitions across telemetry reads so event triggers fire once per phase change
/// (death, buy phase, goal scored, C4 threshold, match boundary) rather than spamming on every tick.
/// </summary>
public sealed class TacticalSignalTracker
{
    private readonly object _sync = new();
    private string _gameKey = string.Empty;
    private bool _matchActiveSeen;
    private bool _matchEndEmittedForCurrentRun;
    private string _lastRoundPhase = string.Empty;
    private string _lastBombState = string.Empty;
    private bool _bombUrgencyTriggered;
    private bool _deadReviewTriggered;
    private int _lastHp = -1;
    private string _lastGoalMarker = string.Empty;
    private string _lastObjectiveWindow = string.Empty;
    private string _lastLogState = string.Empty;

    public void Reset(string? gameId = null)
    {
        lock (_sync)
        {
            _gameKey = gameId ?? string.Empty;
            _matchActiveSeen = false;
            _matchEndEmittedForCurrentRun = false;
            _lastRoundPhase = string.Empty;
            _lastBombState = string.Empty;
            _bombUrgencyTriggered = false;
            _deadReviewTriggered = false;
            _lastHp = -1;
            _lastGoalMarker = string.Empty;
            _lastObjectiveWindow = string.Empty;
            _lastLogState = string.Empty;
        }
    }

    internal void EnsureGame(string gameId)
    {
        lock (_sync)
        {
            if (!string.Equals(_gameKey, gameId, StringComparison.OrdinalIgnoreCase))
                Reset(gameId);
        }
    }

    internal T WithLock<T>(Func<TacticalSignalTrackerState, T> fn)
    {
        lock (_sync)
        {
            var state = new TacticalSignalTrackerState
            {
                MatchActiveSeen = _matchActiveSeen,
                MatchEndEmittedForCurrentRun = _matchEndEmittedForCurrentRun,
                LastRoundPhase = _lastRoundPhase,
                LastBombState = _lastBombState,
                BombUrgencyTriggered = _bombUrgencyTriggered,
                DeadReviewTriggered = _deadReviewTriggered,
                LastHp = _lastHp,
                LastGoalMarker = _lastGoalMarker,
                LastObjectiveWindow = _lastObjectiveWindow,
                LastLogState = _lastLogState
            };

            var res = fn(state);

            _matchActiveSeen = state.MatchActiveSeen;
            _matchEndEmittedForCurrentRun = state.MatchEndEmittedForCurrentRun;
            _lastRoundPhase = state.LastRoundPhase;
            _lastBombState = state.LastBombState;
            _bombUrgencyTriggered = state.BombUrgencyTriggered;
            _deadReviewTriggered = state.DeadReviewTriggered;
            _lastHp = state.LastHp;
            _lastGoalMarker = state.LastGoalMarker;
            _lastObjectiveWindow = state.LastObjectiveWindow;
            _lastLogState = state.LastLogState;
            return res;
        }
    }

    internal sealed class TacticalSignalTrackerState
    {
        public bool MatchActiveSeen;
        public bool MatchEndEmittedForCurrentRun;
        public string LastRoundPhase = string.Empty;
        public string LastBombState = string.Empty;
        public bool BombUrgencyTriggered;
        public bool DeadReviewTriggered;
        public int LastHp = -1;
        public string LastGoalMarker = string.Empty;
        public string LastObjectiveWindow = string.Empty;
        public string LastLogState = string.Empty;
    }
}

/// <summary>
/// Deterministic zero-cost tactical evaluator and event-trigger engine over passive local game telemetry.
/// Computes verified economy/timers/CS-min facts and phase pacing BEFORE invoking any AI vision/LLM call.
/// </summary>
public static partial class TacticalSignalEvaluator
{
    [GeneratedRegex(@"\bHP=(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Cs2HpRegex();

    [GeneratedRegex(@"\bArmor=(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Cs2ArmorRegex();

    [GeneratedRegex(@"\$(\d+)\b", RegexOptions.Compiled)]
    private static partial Regex Cs2MoneyRegex();

    [GeneratedRegex(@"\bround=([a-z0-9_]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Cs2RoundPhaseRegex();

    [GeneratedRegex(@"\bphase=([a-z0-9_]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Cs2MapPhaseRegex();

    [GeneratedRegex(@"\bside=([A-Z]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Cs2SideRegex();

    [GeneratedRegex(@"\bC4=planted(?:\(~?(\d+)s\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Cs2C4PlantedRegex();

    [GeneratedRegex(@"Tiempo\s+(\d+):(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LolClockRegex();

    [GeneratedRegex(@"Oro\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LolGoldRegex();

    [GeneratedRegex(@"Vida\s+(\d+)%", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PctHpRegex();

    [GeneratedRegex(@"\bCS\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LolCsRegex();

    [GeneratedRegex(@"DOTA2_GSI:\s+(-?)(\d+):(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DotaClockRegex();

    [GeneratedRegex(@"\bMP=(\d+)%", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DotaMpRegex();

    public static TacticalEvaluation Evaluate(
        string? telemetrySummary,
        GameProfile? profile,
        TacticalSignalTracker tracker,
        bool isEn = false)
    {
        if (string.IsNullOrWhiteSpace(telemetrySummary))
            return TacticalEvaluation.Neutral;

        tracker.EnsureGame(profile?.Manifest.GameId ?? "universal");
        var text = telemetrySummary.Trim();

        if (text.Contains("CS2_GSI:", StringComparison.OrdinalIgnoreCase))
            return EvaluateCs2(text, tracker, isEn);

        if (text.Contains("LoL_API:", StringComparison.OrdinalIgnoreCase))
            return EvaluateLol(text, tracker, isEn);

        if (text.Contains("DOTA2_GSI:", StringComparison.OrdinalIgnoreCase))
            return EvaluateDota(text, tracker, isEn);

        if (text.Contains("RL_LOG:", StringComparison.OrdinalIgnoreCase))
            return EvaluateRocketLeague(text, tracker, isEn);

        if (text.Contains("VAL_LOG:", StringComparison.OrdinalIgnoreCase))
            return EvaluateValorant(text, tracker, isEn);

        return TacticalEvaluation.Neutral;
    }

    private static TacticalEvaluation EvaluateCs2(string text, TacticalSignalTracker tracker, bool isEn) =>
        tracker.WithLock(s =>
        {
            var mapPhase = MatchGroup(Cs2MapPhaseRegex(), text).ToLowerInvariant();
            if (mapPhase == "gameover")
            {
                bool emitEnd = s.MatchActiveSeen && !s.MatchEndEmittedForCurrentRun;
                s.MatchEndEmittedForCurrentRun = true;
                s.MatchActiveSeen = false;
                return new TacticalEvaluation("MATCH_ENDED", ShouldSilence: true, false, false, emitEnd, 1.0, string.Empty);
            }

            var roundPhase = MatchGroup(Cs2RoundPhaseRegex(), text).ToLowerInvariant();
            if (roundPhase is "live" or "freezetime" or "over" || mapPhase == "live")
            {
                s.MatchActiveSeen = true;
                s.MatchEndEmittedForCurrentRun = false;
            }

            int hp = TryInt(Cs2HpRegex(), text, 100);
            int armor = TryInt(Cs2ArmorRegex(), text, 0);
            int money = TryInt(Cs2MoneyRegex(), text, -1);
            var side = MatchGroup(Cs2SideRegex(), text).ToUpperInvariant();
            bool hasKit = text.Contains("Kit=1", StringComparison.OrdinalIgnoreCase);

            bool phaseChanged = !string.Equals(s.LastRoundPhase, roundPhase, StringComparison.Ordinal);
            s.LastRoundPhase = roundPhase;

            var c4Match = Cs2C4PlantedRegex().Match(text);
            string bombState = c4Match.Success ? "planted" : string.Empty;
            bool bombChanged = !string.Equals(s.LastBombState, bombState, StringComparison.Ordinal);
            s.LastBombState = bombState;
            if (bombChanged)
                s.BombUrgencyTriggered = false;

            // Player died during live round -> 1 reflective death trigger, then silence until next round
            if (hp <= 0 && roundPhase != "freezetime")
            {
                bool firstDeath = !s.DeadReviewTriggered;
                s.DeadReviewTriggered = true;
                s.LastHp = 0;
                var fact = money >= 0
                    ? (isEn ? $"CS2 DEAD | Next round bank: ${money}" : $"CS2 Muerto | Banco prox. ronda: ${money}")
                    : (isEn ? "CS2 DEAD" : "CS2 Muerto");
                return new TacticalEvaluation(
                    "DEAD_REVIEW",
                    ShouldSilence: !firstDeath,
                    ShouldDelayNonUrgent: false,
                    TriggerImmediate: firstDeath,
                    MatchEnded: false,
                    CooldownMultiplier: 0.75,
                    DeterministicFact: fact);
            }

            s.DeadReviewTriggered = false;

            // Buy / freeze time -> ideal moment to coach economy + next round setup
            if (roundPhase == "freezetime")
            {
                s.LastHp = hp;
                string econTag = money switch
                {
                    >= 3900 => "FULL BUY",
                    >= 2000 => "FORCE/HALF",
                    >= 0 => "ECO",
                    _ => "BUY"
                };
                var extras = new List<string>(2);
                if (armor == 0 && money >= 650) extras.Add(isEn ? "no armor" : "sin chaleco");
                if (side == "CT" && !hasKit && money >= 4000) extras.Add(isEn ? "no kit" : "sin kit");
                string extraText = extras.Count > 0 ? $" ({string.Join(", ", extras)})" : string.Empty;
                string fact = money >= 0
                    ? (isEn ? $"CS2 Buy: {econTag} ${money}{extraText}" : $"CS2 Compra: {econTag} ${money}{extraText}")
                    : (isEn ? "CS2 Buy Phase" : "CS2 Fase de compra");

                return new TacticalEvaluation(
                    "BUY_PHASE",
                    ShouldSilence: false,
                    ShouldDelayNonUrgent: false,
                    TriggerImmediate: phaseChanged,
                    MatchEnded: false,
                    CooldownMultiplier: 0.55,
                    DeterministicFact: fact);
            }

            // Bomb critical threshold timing (~10s without kit / ~6s with kit)
            if (c4Match.Success && int.TryParse(c4Match.Groups[1].Value, out int c4Rem))
            {
                int threshold = (side == "CT" && !hasKit) ? 12 : 7;
                if (c4Rem <= threshold && !s.BombUrgencyTriggered)
                {
                    s.BombUrgencyTriggered = true;
                    s.LastHp = hp;
                    string kitTag = side == "CT" ? (hasKit ? "con kit" : "SIN kit") : "C4";
                    return new TacticalEvaluation(
                        "POST_PLANT",
                        ShouldSilence: false,
                        ShouldDelayNonUrgent: false,
                        TriggerImmediate: true,
                        MatchEnded: false,
                        CooldownMultiplier: 0.65,
                        DeterministicFact: $"CS2 C4 ~{c4Rem}s ({kitTag})");
                }
            }

            // Active damage taken during gunfight -> defer non-urgent tips briefly
            bool takingDamage = s.LastHp > 0 && hp > 0 && (s.LastHp - hp) >= 10 && roundPhase == "live";
            s.LastHp = hp;
            return new TacticalEvaluation(
                takingDamage ? "COMBAT" : "PLAY",
                ShouldSilence: false,
                ShouldDelayNonUrgent: takingDamage,
                TriggerImmediate: false,
                MatchEnded: false,
                CooldownMultiplier: takingDamage ? 1.40 : 1.0,
                DeterministicFact: string.Empty);
        });

    private static TacticalEvaluation EvaluateLol(string text, TacticalSignalTracker tracker, bool isEn) =>
        tracker.WithLock(s =>
        {
            s.MatchActiveSeen = true;
            s.MatchEndEmittedForCurrentRun = false;

            var clockMatch = LolClockRegex().Match(text);
            int mins = clockMatch.Success ? int.Parse(clockMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            int secs = clockMatch.Success ? int.Parse(clockMatch.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            double totalMins = mins + secs / 60.0;

            int gold = TryInt(LolGoldRegex(), text, 0);
            int hpPct = TryInt(PctHpRegex(), text, 100);
            int cs = TryInt(LolCsRegex(), text, -1);
            bool isDead = text.Contains("DEAD", StringComparison.OrdinalIgnoreCase);

            var csRate = (cs >= 0 && totalMins >= 2.0)
                ? string.Create(CultureInfo.InvariantCulture, $"{cs / totalMins:0.0} CS/m")
                : string.Empty;
            var factParts = new List<string>(4);
            if (clockMatch.Success) factParts.Add($"LoL {mins:D2}:{secs:D2}");
            if (!string.IsNullOrEmpty(csRate)) factParts.Add(csRate);
            if (gold >= 1300) factParts.Add(isEn ? $"Unspent gold: {gold}g" : $"Oro sin gastar: {gold}g");

            if (isDead)
            {
                bool firstDeath = !s.DeadReviewTriggered;
                s.DeadReviewTriggered = true;
                s.LastHp = 0;
                factParts.Add(isEn ? "DEAD" : "Muerto");
                return new TacticalEvaluation(
                    "DEAD_REVIEW",
                    ShouldSilence: !firstDeath,
                    ShouldDelayNonUrgent: false,
                    TriggerImmediate: firstDeath,
                    MatchEnded: false,
                    CooldownMultiplier: 0.75,
                    DeterministicFact: string.Join(" | ", factParts));
            }

            s.DeadReviewTriggered = false;

            // Early objective preparation windows (first dragon 5:00, voidgrubs 6:00, herald 14:00)
            string objWindow = string.Empty;
            if (mins == 4 && secs >= 15) objWindow = "drake5";
            else if (mins == 5 && secs >= 25) objWindow = "grubs6";
            else if (mins == 19 && secs >= 20) objWindow = "baron20";

            bool newObj = !string.IsNullOrEmpty(objWindow) && !string.Equals(s.LastObjectiveWindow, objWindow, StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(objWindow))
            {
                s.LastObjectiveWindow = objWindow;
                factParts.Add(isEn ? "Objective spawning soon — prep prio/vision" : "Objetivo proximo — prepará prio y visión");
            }

            bool takingDamage = s.LastHp > 0 && hpPct > 0 && (s.LastHp - hpPct) >= 18;
            s.LastHp = hpPct;

            return new TacticalEvaluation(
                takingDamage ? "COMBAT" : (!string.IsNullOrEmpty(objWindow) ? "OBJECTIVE_WINDOW" : "PLAY"),
                ShouldSilence: false,
                ShouldDelayNonUrgent: takingDamage,
                TriggerImmediate: newObj,
                MatchEnded: false,
                CooldownMultiplier: takingDamage ? 1.35 : (gold >= 1400 || newObj ? 0.80 : 1.0),
                DeterministicFact: factParts.Count > 1 ? string.Join(" | ", factParts) : string.Empty);
        });

    private static TacticalEvaluation EvaluateDota(string text, TacticalSignalTracker tracker, bool isEn) =>
        tracker.WithLock(s =>
        {
            s.MatchActiveSeen = true;
            s.MatchEndEmittedForCurrentRun = false;

            bool isDead = text.Contains("DEAD", StringComparison.OrdinalIgnoreCase);
            int hpPct = TryInt(PctHpRegex(), text, 100);
            int mpPct = TryInt(DotaMpRegex(), text, 100);

            if (isDead)
            {
                bool firstDeath = !s.DeadReviewTriggered;
                s.DeadReviewTriggered = true;
                s.LastHp = 0;
                return new TacticalEvaluation(
                    "DEAD_REVIEW",
                    ShouldSilence: !firstDeath,
                    ShouldDelayNonUrgent: false,
                    TriggerImmediate: firstDeath,
                    MatchEnded: false,
                    CooldownMultiplier: 0.75,
                    DeterministicFact: isEn ? "Dota 2 HERO DEAD — review buyback/next item" : "Dota 2 Héroe muerto — evaluá buyback y próximo ítem");
            }

            s.DeadReviewTriggered = false;

            var clock = DotaClockRegex().Match(text);
            string timingHint = string.Empty;
            string objWindow = string.Empty;
            if (clock.Success && string.IsNullOrEmpty(clock.Groups[1].Value))
            {
                int mins = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
                int secs = int.Parse(clock.Groups[3].Value, CultureInfo.InvariantCulture);

                if (mins >= 3 && mins % 2 == 1 && secs >= 45)
                {
                    objWindow = $"rune_{mins + 1}";
                    timingHint = isEn ? $"Power rune in {60 - secs}s" : $"Runa de poder en {60 - secs}s";
                }
                else if (secs is >= 45 and <= 54)
                {
                    objWindow = $"stack_{mins}";
                    timingHint = isEn ? "Camp stack timing (:53-:55)" : "Ventana stack :53-:55";
                }
            }

            bool newObj = !string.IsNullOrEmpty(objWindow) && !string.Equals(s.LastObjectiveWindow, objWindow, StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(objWindow))
                s.LastObjectiveWindow = objWindow;

            bool takingDamage = s.LastHp > 0 && hpPct > 0 && (s.LastHp - hpPct) >= 20;
            s.LastHp = hpPct;

            if (mpPct < 15 && string.IsNullOrEmpty(timingHint))
                timingHint = isEn ? $"Low mana ({mpPct}%)" : $"Maná crítico ({mpPct}%)";

            return new TacticalEvaluation(
                takingDamage ? "COMBAT" : (!string.IsNullOrEmpty(timingHint) ? "OBJECTIVE_WINDOW" : "PLAY"),
                ShouldSilence: false,
                ShouldDelayNonUrgent: takingDamage,
                TriggerImmediate: newObj,
                MatchEnded: false,
                CooldownMultiplier: takingDamage ? 1.35 : (newObj ? 0.75 : 1.0),
                DeterministicFact: timingHint);
        });

    private static TacticalEvaluation EvaluateRocketLeague(string text, TacticalSignalTracker tracker, bool isEn) =>
        tracker.WithLock(s =>
        {
            if (text.Contains("En menú principal", StringComparison.OrdinalIgnoreCase))
            {
                bool emitEnd = s.MatchActiveSeen && !s.MatchEndEmittedForCurrentRun;
                s.MatchEndEmittedForCurrentRun = true;
                s.MatchActiveSeen = false;
                return new TacticalEvaluation("MENU", ShouldSilence: true, false, false, emitEnd, 1.0, string.Empty);
            }

            if (text.Contains("Partido finalizado", StringComparison.OrdinalIgnoreCase))
            {
                bool emitEnd = s.MatchActiveSeen && !s.MatchEndEmittedForCurrentRun;
                s.MatchEndEmittedForCurrentRun = true;
                s.MatchActiveSeen = false;
                return new TacticalEvaluation("MATCH_ENDED", ShouldSilence: true, false, false, emitEnd, 1.0, string.Empty);
            }

            s.MatchActiveSeen = true;
            s.MatchEndEmittedForCurrentRun = false;

            if (text.Contains("Gol anotado", StringComparison.OrdinalIgnoreCase))
            {
                bool newGoal = !string.Equals(s.LastGoalMarker, text, StringComparison.Ordinal);
                s.LastGoalMarker = text;
                return new TacticalEvaluation(
                    "REPLAY_GOAL",
                    ShouldSilence: false,
                    ShouldDelayNonUrgent: false,
                    TriggerImmediate: newGoal,
                    MatchEnded: false,
                    CooldownMultiplier: 0.75,
                    DeterministicFact: isEn ? "Goal scored recently — kickoff role reminder" : "Gol reciente — recordá rol de saque");
            }

            return TacticalEvaluation.Neutral;
        });

    private static TacticalEvaluation EvaluateValorant(string text, TacticalSignalTracker tracker, bool isEn) =>
        tracker.WithLock(s =>
        {
            if (text.Contains("Menu / Lobby", StringComparison.OrdinalIgnoreCase))
            {
                bool emitEnd = s.MatchActiveSeen && !s.MatchEndEmittedForCurrentRun &&
                               !string.Equals(s.LastLogState, "MENU", StringComparison.Ordinal);
                s.LastLogState = "MENU";
                s.MatchEndEmittedForCurrentRun = true;
                s.MatchActiveSeen = false;
                return new TacticalEvaluation("MENU", ShouldSilence: true, false, false, emitEnd, 1.0, string.Empty);
            }

            if (text.Contains("Selección de agentes", StringComparison.OrdinalIgnoreCase))
            {
                s.LastLogState = "PREGAME";
                return new TacticalEvaluation("LOBBY", ShouldSilence: true, false, false, false, 1.0, string.Empty);
            }

            if (text.Contains("Partida en curso", StringComparison.OrdinalIgnoreCase))
            {
                s.LastLogState = "INGAME";
                s.MatchActiveSeen = true;
                s.MatchEndEmittedForCurrentRun = false;
            }

            return TacticalEvaluation.Neutral;
        });

    private static string MatchGroup(Regex rx, string input)
    {
        var m = rx.Match(input);
        return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : string.Empty;
    }

    private static int TryInt(Regex rx, string input, int fallback)
    {
        var m = rx.Match(input);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : fallback;
    }
}
