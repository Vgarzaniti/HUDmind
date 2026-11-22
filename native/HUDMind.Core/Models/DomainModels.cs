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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    public bool IsKeylessAllowed =>
        Provider is "pollinations" or "airforce" or "ollama";

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
            var prov = Provider.ToLowerInvariant();
            if (prov is "pollinations" or "airforce" && string.IsNullOrWhiteSpace(ApiKey))
                return false;
            if (prov is "gemini" or "claude" or "openai")
                return true;
            if (prov == "groq")
            {
                var m = Model.ToLowerInvariant();
                return m.Contains("llama-4") || m.Contains("vision") || m.Contains("scout") || m.Contains("maverick");
            }
            if (prov == "openrouter")
            {
                var m = Model.ToLowerInvariant();
                return m.Contains("vision") || m.Contains("gemma-3") || m.Contains("gemini") || m.Contains("gpt-4") || m.Contains("claude") || m.Contains("vl") || m.Contains("llama-4");
            }
            return false;
        }
    }

    /// <summary>
    /// True only for low-latency paid/free-LPU/Flash vision providers eligible for parallel hedging.
    /// Excludes slow OpenRouter `:free` community queues to avoid wasting daily RPD quota.
    /// </summary>
    public bool IsFastCloudVision =>
        SupportsVision &&
        Provider is "gemini" or "groq" or "openai" or "claude" or "openrouter" &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !(Provider == "openrouter" && Model.EndsWith(":free", StringComparison.OrdinalIgnoreCase));

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

        return Provider.ToLowerInvariant() switch
        {
            "openai" => "https://api.openai.com/v1",
            "claude" => "https://api.anthropic.com/v1",
            "groq" => "https://api.groq.com/openai/v1",
            "openrouter" => "https://openrouter.ai/api/v1",
            "pollinations" => "https://text.pollinations.ai/openai",
            "airforce" => "https://api.airforce/v1",
            "ollama" => "http://localhost:11434/v1",
            _ => string.Empty
        };
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

    [JsonPropertyName("tts_engine")]
    public string TtsEngine { get; set; } = "pyttsx3";

    [JsonPropertyName("voice_gender")]
    public string VoiceGender { get; set; } = "male";

    [JsonPropertyName("voice_style")]
    public string VoiceStyle { get; set; } = "neural";

    [JsonPropertyName("use_live_conversational")]
    public bool UseLiveConversational { get; set; } = true;

    [JsonPropertyName("enable_mic")]
    public bool EnableMic { get; set; } = true;

    [JsonPropertyName("live_auto_coach_seconds")]
    public double LiveAutoCoachSeconds { get; set; } = 12.0;

    [JsonPropertyName("tts_volume")]
    public double TtsVolume { get; set; } = 0.75;

    [JsonPropertyName("overlay_duration_seconds")]
    public double OverlayDurationSeconds { get; set; } = 6.0;

    [JsonPropertyName("overlay_position")]
    public string OverlayPosition { get; set; } = "top-right";

    [JsonPropertyName("overlay_width")]
    public int OverlayWidth { get; set; } = 440;

    [JsonPropertyName("overlay_opacity")]
    public double OverlayOpacity { get; set; } = 0.90;

    [JsonPropertyName("capture_monitor")]
    public int CaptureMonitor { get; set; } = 1;

    [JsonPropertyName("min_inference_interval_seconds")]
    public double MinInferenceIntervalSeconds { get; set; } = 2.5;

    [JsonPropertyName("image_compression_quality")]
    public int ImageCompressionQuality { get; set; } = 58;

    [JsonPropertyName("token_efficiency_mode")]
    public string TokenEfficiencyMode { get; set; } = "eco";

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
    public required GameManifest Manifest { get; init; }
    public required string DirectoryPath { get; init; }
    public string CustomRules { get; set; } = string.Empty;
    public string TacticalSummary { get; init; } = string.Empty;

    public bool MatchesProcess(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return false;
        var norm = executableName.Trim();
        return Manifest.ProcessNames.Any(p =>
            string.Equals(p.Trim(), norm, StringComparison.OrdinalIgnoreCase));
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
    string SpatialTelemetry = ""
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

    public string FormatForPrompt()
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(Phase) && Phase != "UNKNOWN")
            parts.Add($"FASE_DETECTADA={Phase}");
        if (!string.IsNullOrWhiteSpace(OcrZonesSummary))
            parts.Add($"HUD_OCR: {OcrZonesSummary}");
        if (!string.IsNullOrWhiteSpace(LogSummary))
            parts.Add($"GAME_LOG: {LogSummary}");
        if (!string.IsNullOrWhiteSpace(DeltaNote))
            parts.Add($"CAMBIO_RECIENTE: {DeltaNote}");
        return string.Join(" | ", parts);
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
