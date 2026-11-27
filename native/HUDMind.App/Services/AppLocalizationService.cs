using System.Windows;
using MediaApplication = System.Windows.Application;

namespace HUDMind.App.Services;

/// <summary>
/// Runtime ES/EN ResourceDictionary switcher with zero third-party dependencies.
/// Swaps MergedDictionaries dynamically cleanly so DynamicResource bindings update live.
/// </summary>
public static class AppLocalizationService
{
    private const string EsUri = "Resources/Strings.es.xaml";
    private const string EnUri = "Resources/Strings.en.xaml";

    public static bool IsEnglish { get; private set; }

    public static void ApplyLanguage(string? languageCode)
    {
        bool useEn = string.Equals(languageCode?.Trim(), "en", StringComparison.OrdinalIgnoreCase);
        IsEnglish = useEn;

        var app = MediaApplication.Current;
        if (app is null)
            return;

        string targetRelative = useEn ? EnUri : EsUri;
        try
        {
            var nextDict = new ResourceDictionary
            {
                Source = new Uri(targetRelative, UriKind.Relative)
            };

            var merged = app.Resources.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var src = merged[i].Source?.OriginalString ?? string.Empty;
                if (src.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase))
                    merged.RemoveAt(i);
            }
            merged.Add(nextDict);
        }
        catch
        {
            // Keep UI functional on existing fallback dictionary if resource lookup fails
        }
    }

    public static string Get(string key, string fallback = "")
    {
        var app = MediaApplication.Current;
        if (app?.TryFindResource(key) is string found && !string.IsNullOrEmpty(found))
            return found;
        return fallback;
    }
}
