using System.Collections.Concurrent;
using HUDMind.Core.Models;

namespace HUDMind.Core.AI;

public sealed record ProviderPreset(
    string Id,
    string DefaultLabel,
    string DefaultModel,
    string BaseUrl,
    bool SupportsLiveWebSocket = false,
    bool IsKeylessAllowed = false,
    bool SupportsFastHedgedVision = false
);

/// <summary>
/// Single extensible registry for all AI providers (presets, base URLs, HTTP adapters &amp; Live WebSocket backends).
/// Adding or overriding a provider requires zero edits to DomainModels, MainWindow, or FailoverRouter.
/// </summary>
public static class ProviderRegistry
{
    private static readonly object PresetLock = new();
    private static readonly List<ProviderPreset> Presets =
    [
        new("groq", "Groq LPU Gratis (~0.4s)", "meta-llama/llama-4-scout-17b-16e-instruct", "https://api.groq.com/openai/v1", SupportsFastHedgedVision: true),
        new("gemini", "Gemini 2.5 Flash (~0.8s)", "gemini-2.5-flash", "https://generativelanguage.googleapis.com/v1beta", SupportsLiveWebSocket: true, SupportsFastHedgedVision: true),
        new("openai", "OpenAI GPT-4o-mini / Realtime", "gpt-4o-mini", "https://api.openai.com/v1", SupportsLiveWebSocket: true, SupportsFastHedgedVision: true),
        new("xai", "xAI Grok", "grok-2-vision-1212", "https://api.x.ai/v1", SupportsLiveWebSocket: true, SupportsFastHedgedVision: true),
        new("claude", "Anthropic Claude Haiku", "claude-3-5-haiku-latest", "https://api.anthropic.com/v1", SupportsFastHedgedVision: true),
        new("openrouter", "OpenRouter :free", "google/gemma-3-27b-it:free", "https://openrouter.ai/api/v1", SupportsFastHedgedVision: true),
        new("mistral", "Mistral Pixtral", "pixtral-12b-2409", "https://api.mistral.ai/v1", SupportsFastHedgedVision: true),
        new("pollinations", "Pollinations Gratis Sin Login", "openai-fast", "https://text.pollinations.ai/openai", IsKeylessAllowed: true),
        new("airforce", "Airforce Gratis Sin Login", "open-mistral-nemo", "https://api.airforce/v1", IsKeylessAllowed: true),
        new("ollama", "Ollama Local", "llama3.2-vision", "http://localhost:11434/v1", IsKeylessAllowed: true)
    ];

    private static readonly ConcurrentDictionary<string, ProviderPreset> PresetMap = CreateInitialPresetMap();
    private static readonly ConcurrentDictionary<string, Func<ProviderAccount, IAIProvider>> HttpFactories = CreateInitialHttpFactories();
    private static readonly ConcurrentDictionary<string, Func<ProviderAccount, ILiveRealtimeBackend?>> LiveFactories = CreateInitialLiveFactories();

    private static ConcurrentDictionary<string, ProviderPreset> CreateInitialPresetMap()
    {
        var map = new ConcurrentDictionary<string, ProviderPreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Presets)
            map[p.Id] = p;
        return map;
    }

    private static ConcurrentDictionary<string, Func<ProviderAccount, IAIProvider>> CreateInitialHttpFactories()
    {
        var map = new ConcurrentDictionary<string, Func<ProviderAccount, IAIProvider>>(StringComparer.OrdinalIgnoreCase);
        map["gemini"] = acc => new GeminiFastProvider(acc);
        map["claude"] = acc => new AnthropicClaudeProvider(acc);
        return map;
    }

    private static ConcurrentDictionary<string, Func<ProviderAccount, ILiveRealtimeBackend?>> CreateInitialLiveFactories()
    {
        var map = new ConcurrentDictionary<string, Func<ProviderAccount, ILiveRealtimeBackend?>>(StringComparer.OrdinalIgnoreCase);
        map["gemini"] = acc => !string.IsNullOrWhiteSpace(acc.ApiKey) ? new LiveBackends.GeminiLiveBackend(acc.ApiKey, acc.Model) : null;
        map["openai"] = acc => !string.IsNullOrWhiteSpace(acc.ApiKey) ? new LiveBackends.OpenAIRealtimeBackend("openai", acc.ApiKey, acc.Model) : null;
        map["xai"] = acc => !string.IsNullOrWhiteSpace(acc.ApiKey) ? new LiveBackends.OpenAIRealtimeBackend("xai", acc.ApiKey, acc.Model) : null;
        return map;
    }

    public static IReadOnlyList<ProviderPreset> AllPresets
    {
        get { lock (PresetLock) return [.. Presets]; }
    }

    public static ProviderPreset? FindPreset(string? providerId) =>
        !string.IsNullOrWhiteSpace(providerId) && PresetMap.TryGetValue(providerId.Trim(), out var p) ? p : null;

    public static string ResolveBaseUrl(string? providerId) =>
        FindPreset(providerId)?.BaseUrl ?? string.Empty;

    public static bool IsKeylessAllowed(string? providerId) =>
        FindPreset(providerId)?.IsKeylessAllowed == true;

    public static bool SupportsFastHedgedVision(string? providerId) =>
        FindPreset(providerId)?.SupportsFastHedgedVision == true;

    public static void RegisterPreset(ProviderPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Id))
            throw new ArgumentException("Provider preset Id cannot be empty.", nameof(preset));
        var norm = preset with { Id = preset.Id.Trim() };
        lock (PresetLock)
        {
            Presets.RemoveAll(p => string.Equals(p.Id, norm.Id, StringComparison.OrdinalIgnoreCase));
            Presets.Add(norm);
        }
        PresetMap[norm.Id] = norm;
    }

    public static void RegisterHttpAdapter(string providerId, Func<ProviderAccount, IAIProvider> factory) =>
        HttpFactories[providerId.Trim()] = factory;

    public static void RegisterLiveBackend(string providerId, Func<ProviderAccount, ILiveRealtimeBackend?> factory) =>
        LiveFactories[providerId.Trim()] = factory;

    public static IAIProvider CreateHttpAdapter(ProviderAccount account)
    {
        var key = (account.Provider ?? string.Empty).Trim();
        return HttpFactories.TryGetValue(key, out var fn)
            ? fn(account)
            : new OpenAICompatibleProvider(account);
    }

    public static ILiveRealtimeBackend? TryCreateLiveBackend(ProviderAccount account)
    {
        var key = (account.Provider ?? string.Empty).Trim();
        return LiveFactories.TryGetValue(key, out var fn) ? fn(account) : null;
    }
}
