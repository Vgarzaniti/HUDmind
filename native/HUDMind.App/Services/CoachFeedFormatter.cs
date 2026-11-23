using System.Text;
using System.Text.RegularExpressions;
using HUDMind.Core.Models;

namespace HUDMind.App;

public sealed record GameProfileOption(string GameId, string DisplayName);

public sealed record ProviderOption(string Id, string FriendlyName);

public sealed record MonitorOption(int Index, string Label);

public sealed class CoachFeedItem
{
    public required string Timestamp { get; init; }
    public required string Author { get; init; }
    public required string Message { get; init; }
    public required string AccentHex { get; init; }
    public required string CardBackgroundHex { get; init; }
    public required string AuthorForegroundHex { get; init; }
}

/// <summary>
/// Translates internal engine logs and callouts into clean, jargon-free gamer feed cards
/// while mapping technical IDs (providers, profiles) to friendly display labels.
/// </summary>
public static partial class CoachFeedFormatter
{
    [GeneratedRegex(@"^💬\s*Vos:\s*""(.+)""$")]
    private static partial Regex UserChatRegex();

    [GeneratedRegex(@"^.+?\bCoach\s*\([^)]*\)(?:\s*\[[^\]]*\])?:\s*""(.+)""$")]
    private static partial Regex CoachChatRegex();

    [GeneratedRegex(@"^\[(HIGH|MEDIUM|LOW)\]\s*""([^""]+)""")]
    private static partial Regex ClassicAdviceRegex();

    public static string FriendlyProviderName(string? providerId) => (providerId ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "groq" => "Groq (Gratis · Ultra rápido)",
        "gemini" => "Google Gemini (Gratis + Live)",
        "openai" => "OpenAI (GPT / Voz Live)",
        "claude" => "Anthropic Claude",
        "xai" => "xAI Grok",
        "openrouter" => "OpenRouter",
        "mistral" => "Mistral",
        "ollama" => "Ollama (Local en PC)",
        "pollinations" => "Pollinations (Sin clave)",
        "airforce" => "Airforce (Sin clave)",
        var other => string.IsNullOrWhiteSpace(other) ? "IA" : other
    };

    public static string FriendlyShortProvider(string? providerId) => (providerId ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "groq" => "Groq",
        "gemini" => "Gemini",
        "openai" => "OpenAI",
        "claude" => "Claude",
        "xai" => "xAI Grok",
        "openrouter" => "OpenRouter",
        "mistral" => "Mistral",
        "ollama" => "Ollama Local",
        "pollinations" => "Pollinations",
        "airforce" => "Airforce",
        var other => other
    };

    public static string BuildSafeGameId(string? displayName, IEnumerable<string> existingIds)
    {
        var normalized = (displayName ?? string.Empty).Normalize(NormalizationForm.FormD).ToLowerInvariant();
        var sb = new StringBuilder();
        bool lastUnderscore = false;

        foreach (char c in normalized)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                lastUnderscore = false;
            }
            else if (c is ' ' or '-' or '_' && sb.Length > 0 && !lastUnderscore)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }

        var slug = sb.ToString().Trim('_');
        if (string.IsNullOrEmpty(slug) || slug == "universal")
            slug = "mi_juego";
        if (slug.Length > 48)
            slug = slug[..48].TrimEnd('_');

        var taken = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(slug))
            return slug;

        for (int n = 2; n < 1000; n++)
        {
            var candidate = $"{slug}_{n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
        return $"{slug}_{Guid.NewGuid().ToString("N")[..4]}";
    }

    public static CoachFeedItem? TryFormatFeedItem(string rawMessage, string timestamp)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return null;

        var msg = rawMessage.Trim();

        // Hide noisy per-frame internal diagnostics from the main gamer feed (preserved in technical log drawer)
        if (msg.StartsWith("Silencio inteligente", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Silencio local", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Consejo repetitivo omitido", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Análisis inmediato", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("HUDMind Native (", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Perfiles cargados:", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Modo de comunicación configurado:", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Test [", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var userMatch = UserChatRegex().Match(msg);
        if (userMatch.Success)
        {
            return new CoachFeedItem
            {
                Timestamp = timestamp,
                Author = "Vos",
                Message = userMatch.Groups[1].Value.Trim(),
                AccentHex = "#6366F1",
                CardBackgroundHex = "#1E1B4B",
                AuthorForegroundHex = "#A5B4FC"
            };
        }

        var coachChat = CoachChatRegex().Match(msg);
        if (coachChat.Success)
        {
            return new CoachFeedItem
            {
                Timestamp = timestamp,
                Author = "🎙 Coach",
                Message = coachChat.Groups[1].Value.Trim(),
                AccentHex = "#38BDF8",
                CardBackgroundHex = "#0F172A",
                AuthorForegroundHex = "#38BDF8"
            };
        }

        var classic = ClassicAdviceRegex().Match(msg);
        if (classic.Success)
        {
            var urg = classic.Groups[1].Value;
            var text = classic.Groups[2].Value.Trim();
            return urg switch
            {
                "HIGH" => new CoachFeedItem
                {
                    Timestamp = timestamp,
                    Author = "🔥 Coach · Urgente",
                    Message = text,
                    AccentHex = "#EF4444",
                    CardBackgroundHex = "#1F1218",
                    AuthorForegroundHex = "#FCA5A5"
                },
                "MEDIUM" => new CoachFeedItem
                {
                    Timestamp = timestamp,
                    Author = "⚡ Coach · Consejo",
                    Message = text,
                    AccentHex = "#F59E0B",
                    CardBackgroundHex = "#1C1917",
                    AuthorForegroundHex = "#FCD34D"
                },
                _ => new CoachFeedItem
                {
                    Timestamp = timestamp,
                    Author = "💡 Coach · Tip",
                    Message = text,
                    AccentHex = "#38BDF8",
                    CardBackgroundHex = "#0F172A",
                    AuthorForegroundHex = "#7DD3FC"
                }
            };
        }

        if (msg.StartsWith("⛔", StringComparison.Ordinal) ||
            msg.StartsWith("Error ", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("⚠", StringComparison.Ordinal))
        {
            return new CoachFeedItem
            {
                Timestamp = timestamp,
                Author = "Aviso",
                Message = msg,
                AccentHex = msg.StartsWith("⚠", StringComparison.Ordinal) ? "#F59E0B" : "#EF4444",
                CardBackgroundHex = "#1A1416",
                AuthorForegroundHex = msg.StartsWith("⚠", StringComparison.Ordinal) ? "#FCD34D" : "#FCA5A5"
            };
        }

        if (msg.StartsWith("Juego detectado automáticamente:", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("LIVE LISTO", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("🚀", StringComparison.Ordinal) ||
            msg.StartsWith("✅", StringComparison.Ordinal) ||
            msg.StartsWith("💾", StringComparison.Ordinal) ||
            msg.StartsWith("📊", StringComparison.Ordinal) ||
            msg.StartsWith("Coach ", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Motor nativo", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("Sesión Live", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("[AUTO-DETECT]", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("[TEST-MODE]", StringComparison.OrdinalIgnoreCase))
        {
            var friendly = msg
                .Replace("[AUTO-DETECT] ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("[TEST-MODE] ", "Modo Prueba · ", StringComparison.OrdinalIgnoreCase);

            return new CoachFeedItem
            {
                Timestamp = timestamp,
                Author = "Estado",
                Message = friendly,
                AccentHex = "#10B981",
                CardBackgroundHex = "#0B1F1C",
                AuthorForegroundHex = "#6EE7B7"
            };
        }

        return null;
    }
}
