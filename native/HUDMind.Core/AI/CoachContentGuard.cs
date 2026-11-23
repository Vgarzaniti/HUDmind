using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HUDMind.Core.Models;
using HUDMind.Core.Security;

namespace HUDMind.Core.AI;

public enum SafetyCategory
{
    Allowed = 0,
    SevereSexualOrIllegal = 1,
    ProfanityOrInsult = 2,
    PromptInjectionOrJailbreak = 3,
    OffTopicNonGaming = 4
}

public sealed record ContentGuardResult(
    bool IsAllowed,
    SafetyCategory Category,
    string SafeRedirectReply
);

public sealed record OutputGuardResult(
    bool IsSafe,
    SafetyCategory Category,
    string CleanText,
    string FallbackReplacement
);

/// <summary>
/// Pluggable rule in the Chain-of-Responsibility content safety pipeline.
/// Implement to block additional languages, phrases, topics, or compliance domains without modifying core orchestrators.
/// </summary>
public interface IContentSafetyRule
{
    SafetyCategory Category { get; }
    bool AppliesToOutput { get; }
    bool IsMatch(string normalizedText);
}

/// <summary>
/// Regex-backed <see cref="IContentSafetyRule"/> with safe execution timeout.
/// </summary>
public sealed class RegexContentSafetyRule : IContentSafetyRule
{
    private readonly Regex _regex;

    public SafetyCategory Category { get; }
    public bool AppliesToOutput { get; }

    public RegexContentSafetyRule(SafetyCategory category, Regex regex, bool appliesToOutput = true)
    {
        Category = category;
        _regex = regex ?? throw new ArgumentNullException(nameof(regex));
        AppliesToOutput = appliesToOutput;
    }

    public RegexContentSafetyRule(
        SafetyCategory category,
        string pattern,
        bool appliesToOutput = true,
        TimeSpan? timeout = null)
        : this(
            category,
            new Regex(
                string.IsNullOrWhiteSpace(pattern) ? @"(?!x)x" : pattern,
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                timeout ?? TimeSpan.FromMilliseconds(80)),
            appliesToOutput)
    {
    }

    public bool IsMatch(string normalizedText)
    {
        try
        {
            return !string.IsNullOrEmpty(normalizedText) && _regex.IsMatch(normalizedText);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// Evaluates player prompts, model responses, and customizable rules/soul markdown through an ordered safety chain.
/// </summary>
public interface ICoachSafetyPipeline
{
    ContentGuardResult EvaluateUserInput(string? rawInput, bool isEn);
    OutputGuardResult InspectAndCleanModelOutput(string? rawOutput, bool isEn);
    CoachResponse SanitizeCoachResponse(CoachResponse? response, bool isEn);
    bool ContainsUnsafeInstruction(string? rawText);
    string SanitizeInstructionMarkdown(string? rawMarkdown);
}

/// <summary>
/// Chain-of-Responsibility implementation executing an ordered sequence of <see cref="IContentSafetyRule"/> instances.
/// </summary>
public sealed class CompositeContentSafetyPipeline : ICoachSafetyPipeline
{
    private readonly IReadOnlyList<IContentSafetyRule> _rules;
    private readonly Func<SafetyCategory, bool, string> _redirectFormatter;

    public CompositeContentSafetyPipeline(
        IReadOnlyList<IContentSafetyRule> rules,
        Func<SafetyCategory, bool, string>? redirectFormatter = null)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _redirectFormatter = redirectFormatter ?? CoachContentGuard.BuildRedirectMessage;
    }

    /// <summary>
    /// Returns a new pipeline appending an additional rule at the end of the evaluation chain.
    /// </summary>
    public CompositeContentSafetyPipeline WithAdditionalRule(IContentSafetyRule extraRule)
    {
        ArgumentNullException.ThrowIfNull(extraRule);
        var next = new List<IContentSafetyRule>(_rules.Count + 1);
        next.AddRange(_rules);
        next.Add(extraRule);
        return new CompositeContentSafetyPipeline(next, _redirectFormatter);
    }

    public ContentGuardResult EvaluateUserInput(string? rawInput, bool isEn)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return new ContentGuardResult(true, SafetyCategory.Allowed, string.Empty);

        string norm = CoachContentGuard.NormalizeForInspection(rawInput);
        foreach (var rule in _rules)
        {
            if (rule.IsMatch(norm))
                return new ContentGuardResult(false, rule.Category, _redirectFormatter(rule.Category, isEn));
        }

        return new ContentGuardResult(true, SafetyCategory.Allowed, string.Empty);
    }

    public OutputGuardResult InspectAndCleanModelOutput(string? rawOutput, bool isEn)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return new OutputGuardResult(true, SafetyCategory.Allowed, string.Empty, string.Empty);

        string trimmed = rawOutput.Trim();
        string norm = CoachContentGuard.NormalizeForInspection(trimmed);
        foreach (var rule in _rules)
        {
            if (!rule.AppliesToOutput)
                continue;

            if (rule.IsMatch(norm))
                return new OutputGuardResult(false, rule.Category, string.Empty, _redirectFormatter(rule.Category, isEn));
        }

        return new OutputGuardResult(true, SafetyCategory.Allowed, trimmed, trimmed);
    }

    public CoachResponse SanitizeCoachResponse(CoachResponse? response, bool isEn)
    {
        if (response is null || !response.ShouldIntervene)
            return response ?? CoachResponse.Silent();

        var ovGuard = InspectAndCleanModelOutput(response.OverlayText, isEn);
        var voGuard = InspectAndCleanModelOutput(response.VoiceLine, isEn);

        if (!ovGuard.IsSafe || !voGuard.IsSafe)
            return CoachResponse.Silent("[SAFETY-FILTER]");

        return response;
    }

    public bool ContainsUnsafeInstruction(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return false;

        string norm = CoachContentGuard.NormalizeForInspection(rawText);
        foreach (var rule in _rules)
        {
            if (rule.IsMatch(norm))
                return true;
        }
        return false;
    }

    public string SanitizeInstructionMarkdown(string? rawMarkdown)
    {
        if (string.IsNullOrWhiteSpace(rawMarkdown))
            return string.Empty;

        var lines = rawMarkdown.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (ContainsUnsafeInstruction(line))
                continue;
            kept.Add(line);
        }

        return string.Join(Environment.NewLine, kept).Trim();
    }
}

/// <summary>
/// Defense-in-depth guardrail enforcing zero profanity/insults in any language,
/// zero sexual/NSFW/moan/photo/illegal compliance, anti-jailbreak filtering for rules/soul markdown,
/// strict gaming-coach domain boundaries (rejecting weather &amp; non-gaming requests),
/// and clean TTS speech sanitization. Patterns and safety blocks are unsealed on demand from the encrypted binary vault.
/// </summary>
public static partial class CoachContentGuard
{
    private static readonly Lazy<ICoachSafetyPipeline> LazyDefaultPipeline = new(
        CreateDefaultPipeline,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Default Chain-of-Responsibility safety pipeline loaded from encrypted vault patterns.
    /// </summary>
    public static ICoachSafetyPipeline DefaultPipeline => LazyDefaultPipeline.Value;

    private static ICoachSafetyPipeline CreateDefaultPipeline()
    {
        var timeout = TimeSpan.FromMilliseconds(80);
        IContentSafetyRule[] rules =
        [
            new RegexContentSafetyRule(
                SafetyCategory.SevereSexualOrIllegal,
                ProfileVaultReader.ReadProtectedString("rx_severe_sexual_illegal"),
                appliesToOutput: true,
                timeout),
            new RegexContentSafetyRule(
                SafetyCategory.ProfanityOrInsult,
                ProfileVaultReader.ReadProtectedString("rx_profanity_multilang"),
                appliesToOutput: true,
                timeout),
            new RegexContentSafetyRule(
                SafetyCategory.PromptInjectionOrJailbreak,
                ProfileVaultReader.ReadProtectedString("rx_jailbreak"),
                appliesToOutput: false,
                timeout),
            new RegexContentSafetyRule(
                SafetyCategory.OffTopicNonGaming,
                ProfileVaultReader.ReadProtectedString("rx_offtopic"),
                appliesToOutput: true,
                timeout)
        ];

        return new CompositeContentSafetyPipeline(rules, BuildRedirectMessage);
    }

    public static ContentGuardResult EvaluateUserInput(string? rawInput, bool isEn) =>
        DefaultPipeline.EvaluateUserInput(rawInput, isEn);

    public static OutputGuardResult InspectAndCleanModelOutput(string? rawOutput, bool isEn) =>
        DefaultPipeline.InspectAndCleanModelOutput(rawOutput, isEn);

    public static CoachResponse SanitizeCoachResponse(CoachResponse? response, bool isEn) =>
        DefaultPipeline.SanitizeCoachResponse(response, isEn);

    public static bool ContainsUnsafeInstruction(string? rawText) =>
        DefaultPipeline.ContainsUnsafeInstruction(rawText);

    /// <summary>
    /// Strips any line from `custom_rules.md`, `coach_soul.md`, or `player_profile.md` that tries to inject
    /// profanity, NSFW/moans/photos, illegal actions, off-topic assistant duties, or instruction overrides.
    /// Safe lines are preserved intact.
    /// </summary>
    public static string SanitizeInstructionMarkdown(string? rawMarkdown) =>
        DefaultPipeline.SanitizeInstructionMarkdown(rawMarkdown);

    /// <summary>
    /// Normalizes text for smooth Windows TTS pronunciation without stuttering over markdown tokens,
    /// pipes, brackets, backticks, or repeated ellipses.
    /// </summary>
    public static string SanitizeForSpeech(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        var s = rawText
            .Replace("**", " ", StringComparison.Ordinal)
            .Replace("__", " ", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal)
            .Replace("###", " ", StringComparison.Ordinal)
            .Replace("##", " ", StringComparison.Ordinal)
            .Replace("->", " a ", StringComparison.Ordinal)
            .Replace("|", ", ", StringComparison.Ordinal)
            .Replace("...", ", ", StringComparison.Ordinal);

        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (char ch in s)
        {
            if (char.IsControl(ch))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
                continue;
            }
            if (ch is '*' or '#' or '[' or ']' or '<' or '>')
                continue;

            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    public static string BuildImmutableSafetyBlock(bool isEn, bool forLiveAudio)
    {
        string key = (isEn, forLiveAudio) switch
        {
            (true, true) => "safety_live_en",
            (true, false) => "safety_compact_en",
            (false, true) => "safety_live_es",
            (false, false) => "safety_compact_es"
        };

        string fallback = isEn
            ? "NON-NEGOTIABLE BOUNDARY: Strict respectful eSports coaching only."
            : "LÍMITE ESTRICTO INQUEBRANTABLE: Solo coaching gamer respetuoso.";

        return ProfileVaultReader.ReadProtectedString(key, fallback);
    }

    public static string BuildRedirectMessage(SafetyCategory category, bool isEn) => (isEn, category) switch
    {
        (true, SafetyCategory.SevereSexualOrIllegal) =>
            "I cannot comply with sexual, photo, cheat, or illegal requests. I am here strictly to coach your gameplay.",
        (true, SafetyCategory.ProfanityOrInsult) =>
            "I never use profanity or insults in any language. Let's keep a cool head and focus on your next play.",
        (true, SafetyCategory.PromptInjectionOrJailbreak) =>
            "My coaching safety boundaries stay active. What play or mechanic would you like to review?",
        (true, _) =>
            "I only handle live eSports coaching and game mechanics—not off-topic requests like weather or general chat.",
        (false, SafetyCategory.SevereSexualOrIllegal) =>
            "No puedo responder pedidos sexuales, gemidos, fotos, cheats ni cosas ilegales. Estoy solo para coachear tu partida.",
        (false, SafetyCategory.ProfanityOrInsult) =>
            "No uso insultos ni puteadas en ningún idioma. Mantengamos la cabeza fría y enfoquémonos en ganar la próxima jugada.",
        (false, SafetyCategory.PromptInjectionOrJailbreak) =>
            "Mis reglas de coach y respeto siguen activas. ¿Qué jugada o mecánica de la partida querés revisar?",
        _ =>
            "Solo puedo ayudarte con coaching de videojuegos y mecánicas en vivo, no con temas externos como el clima."
    };

    internal static string NormalizeForInspection(string raw)
    {
        var decomposed = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        bool prevSpace = false;
        foreach (char c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format)
                continue;

            char low = char.ToLowerInvariant(c);
            if (char.IsWhiteSpace(low))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else
            {
                sb.Append(low);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
