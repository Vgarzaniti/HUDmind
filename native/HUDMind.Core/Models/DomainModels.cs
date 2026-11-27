using System.Text.Json;
using System.Text.Json.Serialization;

namespace HUDMind.Core.Models;

public enum UrgencyLevel
{
    Low,
    Medium,
    High
}

public sealed record CoachResponse(
    bool ShouldIntervene,
    UrgencyLevel Urgency,
    string OverlayText,
    string VoiceLine,
    string MatchContext = ""
)
{
    public static CoachResponse Silent(string matchContext = "") =>
        new(false, UrgencyLevel.Low, string.Empty, string.Empty, matchContext);
}

public sealed class ProviderAccount
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];

    [JsonPropertyName("label")]
    public string Label { get; set; } = "Nueva Cuenta IA";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "gemini";

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gemini-2.5-flash";

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 1;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("supports_vision")]
    public bool? SupportsVisionOverride { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    public bool IsKeylessAllowed =>
        AI.ProviderRegistry.IsKeylessAllowed(Provider);

    public bool IsReady() =>
        Enabled && (IsKeylessAllowed || !string.IsNullOrWhiteSpace(ApiKey));

    /// <summary>
    /// True when this provider/model accepts image pixels natively.
    /// False for text-only ("blind") models that rely on our lazy WinRT OCR + Game Log sensor layer.
    /// </summary>
    public bool SupportsVision
    {
        get
        {
            if (SupportsVisionOverride.HasValue)
                return SupportsVisionOverride.Value;

            var prov = (Provider ?? string.Empty).Trim().ToLowerInvariant();
            var m = (Model ?? string.Empty).Trim().ToLowerInvariant();

            if (prov is "pollinations" or "airforce" && string.IsNullOrWhiteSpace(ApiKey))
                return false;

            // Explicit text-only exclusions (even on OpenAI/xAI/Gemini/OpenRouter vendors)
            if (m.Contains("o1-mini") || m.Contains("o3-mini") || m.Contains("gpt-3.5") ||
                m.Contains("gemma-2") || m.Contains("deepseek-r1") || m.Contains("deepseek-chat") ||
                m.Contains("mistral-7b") || m.Contains("open-mistral-nemo") ||
                m.Contains("grok-2-1212") || m.Contains("grok-3-mini") ||
                ((m.Contains("grok-2") || m.Contains("grok-3")) && !m.Contains("vision") && m.Contains("-text")) ||
                (m.Contains("llama-3.1") && !m.Contains("vision")) ||
                (m.Contains("llama-3.3") && !m.Contains("vision")))
            {
                return false;
            }

            if (m.Contains("vision") || m.Contains("-vl") || m.Contains("vl-") || m.Contains("pixtral") ||
                m.Contains("gemma-3") || m.Contains("gemma3") || m.Contains("gemini") ||
                m.Contains("claude-3") || m.Contains("claude-4") || m.Contains("claude-sonnet-4") ||
                m.Contains("claude-opus-4") || m.Contains("claude-haiku-4") ||
                m.Contains("gpt-4o") || m.Contains("gpt-4.1") || m.Contains("gpt-5") ||
                m.StartsWith("o1", StringComparison.Ordinal) || m.StartsWith("o3", StringComparison.Ordinal) || m.StartsWith("o4", StringComparison.Ordinal) ||
                m.Contains("/o1", StringComparison.Ordinal) || m.Contains("/o3", StringComparison.Ordinal) || m.Contains("/o4", StringComparison.Ordinal) ||
                m.Contains("llama-4") || m.Contains("scout") || m.Contains("maverick") ||
                m.Contains("llama-3.2-11b") || m.Contains("llama-3.2-90b") ||
                m.Contains("grok-4") || m.Contains("grok-2-vision") ||
                m.Contains("qvq") || m.Contains("molmo") || m.Contains("internvl") ||
                m.Contains("mistral-small-3") || m.Contains("nova-lite") || m.Contains("nova-pro") || m.Contains("paligemma"))
            {
                return true;
            }

            return prov is "gemini" or "claude";
        }
    }

    /// <summary>
    /// True only for low-latency paid/free-LPU/Flash vision providers eligible for parallel hedging.
    /// Excludes slow OpenRouter `:free` community queues and local Ollama to avoid stalling primary racing.
    /// </summary>
    public bool IsFastCloudVision =>
        SupportsVision &&
        AI.ProviderRegistry.SupportsFastHedgedVision(Provider) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !((Provider ?? string.Empty).Equals("openrouter", StringComparison.OrdinalIgnoreCase) &&
          Model.EndsWith(":free", StringComparison.OrdinalIgnoreCase));

    public string MaskedKey()
    {
        var k = (ApiKey ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(k))
            return IsKeylessAllowed ? "(gratis sin login)" : "(sin key)";
        if (k.Length <= 8)
            return "****";
        return $"{k[..4]}...{k[^4..]}";
    }

    public string ResolvedBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
            return BaseUrl.Trim().TrimEnd('/');

        return AI.ProviderRegistry.ResolveBaseUrl(Provider);
    }
}

public sealed class UserConfig
{
    [JsonPropertyName("text_language")]
    public string TextLanguage { get; set; } = "es";

    [JsonPropertyName("voice_language")]
    public string VoiceLanguage { get; set; } = "es";

    [JsonPropertyName("coach_mode")]
    public string CoachMode { get; set; } = "auto";

    [JsonPropertyName("enable_voice")]
    public bool EnableVoice { get; set; } = true;

    [JsonPropertyName("enable_overlay")]
    public bool EnableOverlay { get; set; } = true;

    [JsonPropertyName("enable_hotkeys")]
    public bool EnableHotkeys { get; set; } = true;

    [JsonPropertyName("hotkey_toggle_pause")]
    public string HotkeyTogglePause { get; set; } = "F9";

    [JsonPropertyName("hotkey_analyze_now")]
    public string HotkeyAnalyzeNow { get; set; } = "F10";

    [JsonPropertyName("hotkey_toggle_mic")]
    public string HotkeyToggleMic { get; set; } = "F8";

    [JsonPropertyName("hotkey_quick_chat")]
    public string HotkeyQuickChat { get; set; } = "F7";

    [JsonPropertyName("launch_with_windows")]
    public bool LaunchWithWindows { get; set; }

    [JsonPropertyName("tts_engine")]
    public string TtsEngine { get; set; } = "pyttsx3";

    [JsonPropertyName("tts_rate")]
    public string TtsRate { get; set; } = "+5%";

    [JsonPropertyName("voice_gender")]
    public string VoiceGender { get; set; } = "male";

    [JsonPropertyName("voice_style")]
    public string VoiceStyle { get; set; } = "neural";

    [JsonPropertyName("use_live_conversational")]
    public bool UseLiveConversational { get; set; } = true;

    [JsonPropertyName("enable_mic")]
    public bool EnableMic { get; set; } = true;

    [JsonPropertyName("mic_input_mode")]
    public string MicInputMode { get; set; } = "open_mic";

    [JsonPropertyName("push_to_talk_key")]
    public string PushToTalkKey { get; set; } = "V";

    [JsonPropertyName("audio_input_device")]
    public string AudioInputDeviceName { get; set; } = string.Empty;

    [JsonPropertyName("audio_output_device")]
    public string AudioOutputDeviceName { get; set; } = string.Empty;

    [JsonPropertyName("minimize_to_tray")]
    public bool MinimizeToTray { get; set; }

    [JsonIgnore]
    public bool IsPushToTalkMode =>
        string.Equals(MicInputMode, "push_to_talk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Independent communication channel mode:
    /// "conversation" (mic + coach voice), "coach_voice" (coach speaks only, mic off), "text_only" (silent chat + overlay).
    /// </summary>
    [JsonPropertyName("communication_mode")]
    public string CommunicationMode { get; set; } = "conversation";

    [JsonIgnore]
    public bool IsConversationMode =>
        string.Equals(CommunicationMode, "conversation", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsCoachVoiceOnlyMode =>
        string.Equals(CommunicationMode, "coach_voice", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsTextOnlyMode =>
        string.Equals(CommunicationMode, "text_only", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsCoachVoiceActive => !IsTextOnlyMode && EnableVoice;

    [JsonIgnore]
    public bool IsMicActive => IsConversationMode && EnableMic;

    public void SetCommunicationMode(string? mode)
    {
        var norm = (mode ?? "conversation").Trim().ToLowerInvariant();
        switch (norm)
        {
            case "text_only":
                CommunicationMode = "text_only";
                EnableVoice = false;
                EnableMic = false;
                break;
            case "coach_voice":
                CommunicationMode = "coach_voice";
                EnableVoice = true;
                EnableMic = false;
                if (LiveAutoCoachSeconds <= 0)
                    LiveAutoCoachSeconds = 6.0;
                break;
            default:
                CommunicationMode = "conversation";
                EnableVoice = true;
                EnableMic = true;
                break;
        }
    }

    [JsonPropertyName("live_auto_coach_seconds")]
    public double LiveAutoCoachSeconds { get; set; } = 6.0;

    [JsonPropertyName("player_skill_tier")]
    public string PlayerSkillTier { get; set; } = "intermediate";

    [JsonPropertyName("player_rank_label")]
    public string PlayerRankLabel { get; set; } = string.Empty;

    [JsonPropertyName("coach_style")]
    public string CoachStyle { get; set; } = "friendly";

    [JsonPropertyName("coaching_pacing_mode")]
    public string CoachingPacingMode { get; set; } = "balanced";

    [JsonPropertyName("player_role_tag")]
    public string PlayerRoleTag { get; set; } = "auto";

    [JsonIgnore]
    public bool IsEventFirstMode =>
        string.Equals(CoachingPacingMode, "event_first", StringComparison.OrdinalIgnoreCase);

    [JsonPropertyName("tts_volume")]
    public double TtsVolume { get; set; } = 0.75;

    [JsonPropertyName("overlay_duration_seconds")]
    public double OverlayDurationSeconds { get; set; } = 6.0;

    [JsonPropertyName("overlay_position")]
    public string OverlayPosition { get; set; } = "top-right";

    [JsonPropertyName("overlay_width")]
    public int OverlayWidth { get; set; } = 440;

    public const string OverlayStyleTransparentText = "transparent_text";
    public const string OverlayStyleDiscordPill = "discord_pill";
    public const string OverlayStyleCard = "card";

    [JsonPropertyName("overlay_opacity")]
    public double OverlayOpacity { get; set; } = 0.55;

    [JsonPropertyName("overlay_scale")]
    public double OverlayScale { get; set; } = 1.0;

    [JsonPropertyName("overlay_compact")]
    public bool OverlayCompact { get; set; }

    [JsonPropertyName("overlay_style")]
    public string OverlayStyle { get; set; } = OverlayStyleDiscordPill;

    [JsonPropertyName("overlay_max_messages")]
    public int OverlayMaxMessages { get; set; } = 3;

    [JsonPropertyName("overlay_follow_active_monitor")]
    public bool OverlayFollowActiveMonitor { get; set; } = true;

    [JsonPropertyName("enable_companion_hud")]
    public bool EnableCompanionHud { get; set; }

    [JsonPropertyName("companion_hud_port")]
    public int CompanionHudPort { get; set; } = 17890;

    [JsonPropertyName("companion_hud_lan")]
    public bool CompanionHudAllowLan { get; set; }

    [JsonPropertyName("enable_ingame_overlay_bridge")]
    public bool EnableInGameOverlayBridge { get; set; }

    [JsonPropertyName("external_telemetry_ws_enabled")]
    public bool ExternalTelemetryWsEnabled { get; set; } = true;

    [JsonPropertyName("external_telemetry_ws_port")]
    public int ExternalTelemetryWsPort { get; set; } = Overlay.InGameOverlayBridgeProtocol.DefaultTelemetryWsPort;

    [JsonIgnore]
    public bool IsAnyOverlayEnabled => EnableOverlay || EnableCompanionHud || EnableInGameOverlayBridge;

    public static string NormalizeOverlayStyle(string? style, bool compactFallback = true)
    {
        var norm = (style ?? string.Empty).Trim().ToLowerInvariant();
        return norm switch
        {
            "transparent_text" or "transparent" or "floating" or "floating_text" or "none" or "text" => OverlayStyleTransparentText,
            "card" or "full" or "tactical_card" => OverlayStyleCard,
            "discord_pill" or "pill" or "compact" or "glass" or "discord" => OverlayStyleDiscordPill,
            "" => compactFallback ? OverlayStyleDiscordPill : OverlayStyleCard,
            _ => OverlayStyleDiscordPill
        };
    }

    public string ResolveOverlayStyle() =>
        NormalizeOverlayStyle(OverlayStyle, compactFallback: OverlayCompact || string.IsNullOrWhiteSpace(OverlayStyle));

    public int ResolveOverlayMaxMessages() =>
        Math.Clamp(OverlayMaxMessages <= 0 ? 3 : OverlayMaxMessages, 1, 4);

    [JsonPropertyName("capture_monitor")]
    public int CaptureMonitor { get; set; } = 1;

    [JsonPropertyName("min_inference_interval_seconds")]
    public double MinInferenceIntervalSeconds { get; set; } = 2.5;

    [JsonPropertyName("image_compression_quality")]
    public int ImageCompressionQuality { get; set; } = 58;

    [JsonPropertyName("token_efficiency_mode")]
    public string TokenEfficiencyMode { get; set; } = "eco";

    [JsonPropertyName("enable_audio_cues")]
    public bool EnableAudioCues { get; set; } = true;

    [JsonPropertyName("check_updates_on_startup")]
    public bool CheckUpdatesOnStartup { get; set; } = true;

    [JsonPropertyName("memory_match_window")]
    public int MemoryMatchWindow { get; set; } = 10;

    [JsonPropertyName("has_completed_onboarding")]
    public bool HasCompletedOnboarding { get; set; } = false;

    [JsonPropertyName("overlay_custom_left")]
    public double? OverlayCustomLeft { get; set; }

    [JsonPropertyName("overlay_custom_top")]
    public double? OverlayCustomTop { get; set; }

    [JsonPropertyName("window_left")]
    public double? WindowLeft { get; set; }

    [JsonPropertyName("window_top")]
    public double? WindowTop { get; set; }

    [JsonPropertyName("window_width")]
    public double? WindowWidth { get; set; }

    [JsonPropertyName("window_height")]
    public double? WindowHeight { get; set; }

    [JsonPropertyName("window_maximized")]
    public bool WindowMaximized { get; set; } = false;

    [JsonPropertyName("last_seen_app_version")]
    public string LastSeenAppVersion { get; set; } = string.Empty;

    [JsonIgnore]
    public string PreviousMatchFocus { get; set; } = string.Empty;

    [JsonIgnore]
    public string CoachSoulSummary { get; set; } = string.Empty;

    [JsonIgnore]
    public string PlayerProfileSummary { get; set; } = string.Empty;

    [JsonIgnore]
    public bool RuntimeTiltRisk { get; set; }

    [JsonIgnore]
    public string RuntimeLeakFocus { get; set; } = string.Empty;

    /// <summary>
    /// Synchronizes mutable runtime preferences when a caller provides a separate UserConfig instance.
    /// </summary>
    public void CopyRuntimePreferencesFrom(UserConfig source)
    {
        if (ReferenceEquals(this, source))
            return;
        TextLanguage = source.TextLanguage;
        VoiceLanguage = source.VoiceLanguage;
        CoachMode = source.CoachMode;
        EnableOverlay = source.EnableOverlay;
        VoiceGender = source.VoiceGender;
        VoiceStyle = source.VoiceStyle;
        TtsVolume = source.TtsVolume;
        TtsRate = source.TtsRate;
        MicInputMode = source.MicInputMode;
        PushToTalkKey = source.PushToTalkKey;
        AudioInputDeviceName = source.AudioInputDeviceName;
        AudioOutputDeviceName = source.AudioOutputDeviceName;
        LiveAutoCoachSeconds = source.LiveAutoCoachSeconds;
        PlayerSkillTier = source.PlayerSkillTier;
        PlayerRankLabel = source.PlayerRankLabel;
        CoachStyle = source.CoachStyle;
        CoachingPacingMode = source.CoachingPacingMode;
        PlayerRoleTag = source.PlayerRoleTag;
        RuntimeLeakFocus = source.RuntimeLeakFocus;
        OverlayDurationSeconds = source.OverlayDurationSeconds;
        OverlayPosition = source.OverlayPosition;
        OverlayOpacity = source.OverlayOpacity;
        OverlayScale = source.OverlayScale;
        OverlayCompact = source.OverlayCompact;
        OverlayStyle = source.OverlayStyle;
        OverlayMaxMessages = source.OverlayMaxMessages;
        OverlayFollowActiveMonitor = source.OverlayFollowActiveMonitor;
        OverlayCustomLeft = source.OverlayCustomLeft;
        OverlayCustomTop = source.OverlayCustomTop;
        WindowLeft = source.WindowLeft;
        WindowTop = source.WindowTop;
        WindowWidth = source.WindowWidth;
        WindowHeight = source.WindowHeight;
        WindowMaximized = source.WindowMaximized;
        LastSeenAppVersion = source.LastSeenAppVersion;
        HasCompletedOnboarding = source.HasCompletedOnboarding;
        HotkeyTogglePause = source.HotkeyTogglePause;
        HotkeyAnalyzeNow = source.HotkeyAnalyzeNow;
        HotkeyToggleMic = source.HotkeyToggleMic;
        HotkeyQuickChat = source.HotkeyQuickChat;
        EnableHotkeys = source.EnableHotkeys;
        MinimizeToTray = source.MinimizeToTray;
        EnableCompanionHud = source.EnableCompanionHud;
        CompanionHudPort = Math.Clamp(source.CompanionHudPort <= 0 ? 17890 : source.CompanionHudPort, 1024, 65535);
        CompanionHudAllowLan = source.CompanionHudAllowLan;
        EnableInGameOverlayBridge = source.EnableInGameOverlayBridge;
        ExternalTelemetryWsEnabled = source.ExternalTelemetryWsEnabled;
        ExternalTelemetryWsPort = Math.Clamp(
            source.ExternalTelemetryWsPort <= 0 ? Overlay.InGameOverlayBridgeProtocol.DefaultTelemetryWsPort : source.ExternalTelemetryWsPort,
            1024,
            65535);
        CaptureMonitor = source.CaptureMonitor;
        MinInferenceIntervalSeconds = source.MinInferenceIntervalSeconds;
        ImageCompressionQuality = source.ImageCompressionQuality;
        TokenEfficiencyMode = source.TokenEfficiencyMode;
        EnableAudioCues = source.EnableAudioCues;
        CheckUpdatesOnStartup = source.CheckUpdatesOnStartup;
        LaunchWithWindows = source.LaunchWithWindows;
        MemoryMatchWindow = Math.Clamp(source.MemoryMatchWindow, 3, 30);
        PreviousMatchFocus = source.PreviousMatchFocus;
        CoachSoulSummary = source.CoachSoulSummary;
        PlayerProfileSummary = source.PlayerProfileSummary;
        RuntimeTiltRisk = source.RuntimeTiltRisk;
        SetCommunicationMode(source.CommunicationMode);
    }

    /// <summary>
    /// Restores UI, overlay, voice, hotkey, and capture preferences to factory defaults
    /// while preserving account onboarding completion status and runtime context strings.
    /// </summary>
    public void ResetPreferencesToDefaults()
    {
        bool completedOnboarding = HasCompletedOnboarding;
        string lastSeenVersion = LastSeenAppVersion;
        string prevFocus = PreviousMatchFocus;
        string soul = CoachSoulSummary;
        string profile = PlayerProfileSummary;
        string leakFocus = RuntimeLeakFocus;
        bool tiltRisk = RuntimeTiltRisk;

        var defaults = new UserConfig
        {
            HasCompletedOnboarding = completedOnboarding,
            LastSeenAppVersion = lastSeenVersion,
            PreviousMatchFocus = prevFocus,
            CoachSoulSummary = soul,
            PlayerProfileSummary = profile,
            RuntimeLeakFocus = leakFocus,
            RuntimeTiltRisk = tiltRisk
        };
        UseLiveConversational = defaults.UseLiveConversational;
        CopyRuntimePreferencesFrom(defaults);
    }

    /// <summary>
    /// Preserves all Python-specific config fields (tts_voice, overlay_theme, etc.) on round-trip save.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

public sealed class GameManifest
{
    [JsonPropertyName("game_id")]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("process_names")]
    public List<string> ProcessNames { get; set; } = [];

    [JsonPropertyName("capture_fps")]
    public double CaptureFps { get; set; } = 1.5;

    [JsonPropertyName("phash_threshold")]
    public int PhashThreshold { get; set; } = 8;

    [JsonPropertyName("cooldown_seconds")]
    public double CooldownSeconds { get; set; } = 8.0;

    [JsonPropertyName("max_resolution")]
    public int[] MaxResolution { get; set; } = [1280, 720];

    [JsonPropertyName("language")]
    public string Language { get; set; } = "es";

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;
}

public sealed class GameProfile
{
    private static readonly HashSet<string> NonGameDesktopProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "HUDMind.exe", "HUDMind.Native.exe", "explorer.exe", "discord.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe",
        "Code.exe", "devenv.exe", "rider64.exe", "Taskmgr.exe",
        "ApplicationFrameHost.exe", "SearchHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SystemSettings.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "WindowsTerminal.exe"
    };

    public static bool IsDesktopNonGameProcess(string? executableName) =>
        string.IsNullOrWhiteSpace(executableName) ||
        NonGameDesktopProcesses.Contains(executableName.Trim()) ||
        executableName.Trim().StartsWith("HUDMind", StringComparison.OrdinalIgnoreCase);

    public required GameManifest Manifest { get; init; }
    public required string DirectoryPath { get; init; }
    public string CustomRules { get; set; } = string.Empty;
    public string TacticalSummary { get; init; } = string.Empty;
    public string KnowledgeGuide { get; init; } = string.Empty;

    public bool MatchesProcess(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return false;
        var norm = executableName.Trim();
        return Manifest.ProcessNames.Any(p =>
            string.Equals(p.Trim(), norm, StringComparison.OrdinalIgnoreCase));
    }

    public static GameProfile? MatchForegroundProfile(
        IReadOnlyList<GameProfile> profiles,
        string? foregroundExecutable,
        string? forcedGameId = null)
    {
        var matched = profiles.FirstOrDefault(p => p.MatchesProcess(foregroundExecutable));
        if (matched is null &&
            string.Equals(forcedGameId, "universal", StringComparison.OrdinalIgnoreCase) &&
            !IsDesktopNonGameProcess(foregroundExecutable))
        {
            matched = profiles.FirstOrDefault(p => p.Manifest.GameId == "universal");
        }
        return matched;
    }
}

public sealed record CapturedFrameData(
    byte[] JpegBytes,
    ulong PerceptualHash,
    long FrameId,
    double CaptureMs,
    double EncodeMs,
    DateTimeOffset CapturedAtUtc,
    bool IsForced = false,
    string SpatialTelemetry = "",
    double? TargetCooldownSeconds = null,
    long SessionEpoch = 0,
    double SparseLumaDelta = -1.0
);

public sealed record HudSensorSnapshot(
    string Phase,
    string OcrZonesSummary,
    string LogSummary,
    string DeltaNote,
    double OcrMs,
    bool HasUsefulData
)
{
    public static HudSensorSnapshot Empty { get; } = new("UNKNOWN", string.Empty, string.Empty, string.Empty, 0, false);

    public string FormatForPrompt(bool isEn = false)
    {
        static string SafeField(string s) => string.IsNullOrWhiteSpace(s)
            ? string.Empty
            : new string(s.Replace('|', '/').Replace('<', '(').Replace('>', ')').Where(c => !char.IsControl(c)).ToArray()).Trim();

        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(Phase) && Phase != "UNKNOWN")
            parts.Add(isEn ? $"PHASE={SafeField(Phase)}" : $"FASE_DETECTADA={SafeField(Phase)}");
        if (!string.IsNullOrWhiteSpace(OcrZonesSummary))
            parts.Add($"HUD_OCR: {SafeField(OcrZonesSummary)}");
        if (!string.IsNullOrWhiteSpace(LogSummary))
            parts.Add($"GAME_LOG: {SafeField(LogSummary)}");
        if (!string.IsNullOrWhiteSpace(DeltaNote))
            parts.Add(isEn ? $"RECENT_CHANGE: {SafeField(DeltaNote)}" : $"CAMBIO_RECIENTE: {SafeField(DeltaNote)}");
        return string.Join(" ; ", parts);
    }
}

public sealed record CoachRuntimeStats(
    long FramesCaptured,
    long FramesFiltered,
    long AiCalls,
    long Interventions,
    double LastCaptureMs,
    double LastAiMs,
    double LastVoiceMs,
    string ActiveProvider,
    string ActiveGame,
    bool IsPaused = false
);

public sealed record MatchSessionSnapshot(
    string MatchId,
    string GameId,
    string DisplayName,
    int InterventionsCount,
    string Digest,
    IReadOnlyList<string> AdvicesSample
);
