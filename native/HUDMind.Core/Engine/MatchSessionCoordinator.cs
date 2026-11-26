using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;

namespace HUDMind.Core.Engine;

public readonly record struct ProfileSwitchOutcome(
    bool GameInForeground,
    bool Switched,
    GameProfile? PreviousProfile,
    GameProfile? CurrentProfile,
    string ForegroundExecutable
);

/// <summary>
/// Unified match-session state coordinator shared across all <see cref="ICoachEngine"/>
/// implementations (Classic Multi-IA &amp; Live WebSocket). Centralizes epoch fencing,
/// cross-game state isolation, RAM motion ring buffer, tactical tilt tracking,
/// black-screen diagnostics, and clean post-match snapshot flushing.
/// </summary>
public sealed class MatchSessionCoordinator
{
    private readonly List<GameProfile> _profiles;
    private readonly IForegroundWindowDetector _windowDetector;
    private readonly IScreenCaptureService _capture;
    private readonly IOverlayOutputService _overlay;
    private readonly Action<string>? _onLog;
    private readonly Action<MatchSessionSnapshot>? _onMatchCompleted;
    private readonly Func<GameProfile, AssembledCoachContext>? _resolveContextForProfile;
    private readonly string? _forcedGameId;
    private readonly bool _testMode;

    private volatile GameProfile? _activeProfile;
    private long _profileEpoch;

    public InterventionMemory Memory { get; } = new(maxItems: 12, ttlSeconds: 75.0);
    public RecentFrameRingBuffer FrameRingBuffer { get; } = new(maxFrames: 24, minSpacingMs: 95.0, macroMaxFrames: 25, macroMinSpacingMs: 900.0);
    public TacticalSignalTracker TacticalTracker { get; } = new();
    public BlackScreenDiagnostic BlackScreenDiagnostic { get; } = new();

    public GameProfile? ActiveProfile => _activeProfile;
    public long CurrentEpoch => Volatile.Read(ref _profileEpoch);

    public MatchSessionCoordinator(
        List<GameProfile> profiles,
        IForegroundWindowDetector windowDetector,
        IScreenCaptureService capture,
        IOverlayOutputService overlay,
        bool testMode,
        string? forcedGameId = null,
        Action<string>? onLog = null,
        Action<MatchSessionSnapshot>? onMatchCompleted = null,
        Func<GameProfile, AssembledCoachContext>? resolveContextForProfile = null)
    {
        _profiles = profiles;
        _windowDetector = windowDetector;
        _capture = capture;
        _overlay = overlay;
        _testMode = testMode;
        _forcedGameId = forcedGameId;
        _onLog = onLog;
        _onMatchCompleted = onMatchCompleted;
        _resolveContextForProfile = resolveContextForProfile;
    }

    public GameProfile? ResolveForcedOrDefaultProfile() =>
        _profiles.FirstOrDefault(p => string.Equals(p.Manifest.GameId, _forcedGameId, StringComparison.OrdinalIgnoreCase))
        ?? _profiles.FirstOrDefault();

    public GameProfile? InitializeOnStart(UserConfig config, bool predetectInNonTestMode)
    {
        config.RuntimeTiltRisk = false;
        Memory.Clear(clearMatchLog: true);
        FrameRingBuffer.Clear();
        TacticalTracker.Reset();
        BlackScreenDiagnostic.Reset();

        if (_testMode)
            _activeProfile = ResolveForcedOrDefaultProfile();
        else if (predetectInNonTestMode)
            _activeProfile = GameProfile.MatchForegroundProfile(_profiles, _windowDetector.GetForegroundExecutable(), _forcedGameId);
        else
            _activeProfile = null;

        if (_activeProfile is not null)
        {
            TacticalTracker.Reset(_activeProfile.Manifest.GameId);
            if (_resolveContextForProfile is not null)
                CoachContextAssembler.ApplyToConfig(config, _resolveContextForProfile(_activeProfile));
        }

        return _activeProfile;
    }

    public ProfileSwitchOutcome SyncForegroundProfile(UserConfig config, string overlayBannerText)
    {
        var fg = _windowDetector.GetForegroundExecutable() ?? string.Empty;
        var matched = GameProfile.MatchForegroundProfile(_profiles, fg, _forcedGameId);
        if (matched is null)
            return new ProfileSwitchOutcome(false, false, _activeProfile, _activeProfile, fg);

        var prev = _activeProfile;
        if (ReferenceEquals(matched, prev))
            return new ProfileSwitchOutcome(true, false, prev, prev, fg);

        if (prev is not null)
            FlushMatchSnapshot(resetMatchLog: true, config);

        Interlocked.Increment(ref _profileEpoch);
        config.RuntimeTiltRisk = false;
        Memory.Clear(clearMatchLog: true);
        FrameRingBuffer.Clear();
        TacticalTracker.Reset(matched.Manifest.GameId);
        BlackScreenDiagnostic.Reset();

        if (_resolveContextForProfile is not null)
            CoachContextAssembler.ApplyToConfig(config, _resolveContextForProfile(matched));

        _activeProfile = matched;
        _onLog?.Invoke($"Juego detectado automáticamente: {matched.Manifest.DisplayName} ({fg})");
        if (config.EnableOverlay)
            _overlay.ShowCallout(overlayBannerText, UrgencyLevel.Low, matched.Manifest.DisplayName, 3.0);

        return new ProfileSwitchOutcome(true, true, prev, matched, fg);
    }

    public void ObserveCaptureHealth(UserConfig config) =>
        BlackScreenDiagnostic.Observe(_capture, _activeProfile, _testMode, _onLog, _overlay, config.EnableOverlay);

    public TacticalEvaluation EvaluateTacticalSignal(string rawTelemetry, GameProfile? profile, UserConfig config, bool isEn)
    {
        var tactical = TacticalSignalEvaluator.Evaluate(rawTelemetry, profile, TacticalTracker, isEn);
        config.RuntimeTiltRisk = tactical.IsTiltRisk;
        if (!string.IsNullOrWhiteSpace(tactical.DeterministicFact))
            Memory.UpdateMatchAnchor(tactical.DeterministicFact);
        if (tactical.MatchEnded)
            FlushMatchSnapshot(resetMatchLog: true, config);
        return tactical;
    }

    public (string GameName, string Digest, int Count) GetMatchSessionDigest(bool isEn = false) =>
        Memory.BuildMatchReviewDigest(isEn: isEn);

    public MatchSessionSnapshot? CaptureMatchSnapshot(bool isEn = false) =>
        Memory.BuildSessionSnapshot(isEn);

    public void FlushMatchSnapshot(bool resetMatchLog, UserConfig config)
    {
        bool isEn = CoachingPersonaBuilder.IsEnglish(config, preferTextLang: true);
        var snap = Memory.BuildSessionSnapshot(isEn);
        if (snap is not null && snap.InterventionsCount > 0)
        {
            try { _onMatchCompleted?.Invoke(snap); } catch { }
        }
        if (resetMatchLog)
            Memory.Clear(clearMatchLog: true);
    }

    public void OnStop(bool clearRingBuffer = true)
    {
        if (clearRingBuffer)
            FrameRingBuffer.Clear();
    }
}
