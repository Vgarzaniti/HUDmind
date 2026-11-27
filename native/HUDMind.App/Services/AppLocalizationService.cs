using System.Windows;
using MediaApplication = System.Windows.Application;

namespace HUDMind.App.Services;

/// <summary>
/// Runtime ES/EN ResourceDictionary switcher and concise imperative text localizer
/// with zero third-party dependencies.
/// </summary>
public static class AppLocalizationService
{
    private const string EsUri = "Resources/Strings.es.xaml";
    private const string EnUri = "Resources/Strings.en.xaml";

    public static bool IsEnglish { get; private set; }
    public static event Action<bool>? LanguageChanged;

    public static string T(string es, string en) => IsEnglish ? en : es;

    public static void ApplyLanguage(string? languageCode)
    {
        bool useEn = string.Equals(languageCode?.Trim(), "en", StringComparison.OrdinalIgnoreCase);
        IsEnglish = useEn;

        var app = MediaApplication.Current;
        if (app is not null)
        {
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
                // Keep UI functional on fallback dictionary
            }
        }

        LanguageChanged?.Invoke(useEn);
    }

    public static string Get(string key, string fallback = "")
    {
        var app = MediaApplication.Current;
        if (app?.TryFindResource(key) is string found && !string.IsNullOrEmpty(found))
            return found;
        return fallback;
    }
}
