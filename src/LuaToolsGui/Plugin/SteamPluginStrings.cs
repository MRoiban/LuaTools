using System.Globalization;
using System.Resources;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>
/// Shared, source-owned app translations for the isolated Steam frontend.
/// Keys are the frontend's English copy. New plugin-specific sentences currently
/// fall back to authored English; this is deliberately partial localization.
/// No reference-plugin strings or translation tables are used.
/// </summary>
public static class SteamPluginStrings
{
    private static readonly ResourceManager Resources =
        new("LuaToolsGui.Resources.Strings", typeof(LuaToolsGui.Resources.Strings).Assembly);

    // Keep aligned with Program.SupportedLanguages and Resources/Strings.*.resx.
    private static readonly string[] Languages =
    [
        "en", "zh-Hans", "zh-Hant", "ja", "ko", "es", "es-419", "pt-BR", "pt-PT",
        "fr", "de", "it", "nl", "pl", "ru", "uk", "tr", "ar", "cs", "hu", "ro",
        "el", "bg", "th", "vi", "id", "da", "fi", "nb", "sv",
    ];

    private static readonly (string Text, string Resource)[] Shared =
    [
        ("Cancel", "Mode_Cancel"),
        ("Remove", "Downloads_Action_Remove"),
        ("Download", "Add_Download"),
        ("Restart Steam", "Nav_RestartSteam"),
        ("Failed", "Downloads_Status_Failed"),
        ("Not found", "Settings_SteamSource_NotFound"),
        ("Dismiss", "Settings_BotLink_Dismiss"),
        ("Settings", "Nav_Settings"),
    ];

    /// <summary>Returns JSON data, suitable for assignment to __LuaToolsStrings.</summary>
    public static string BuildJson()
    {
        var languages = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (string language in Languages)
        {
            var culture = CultureInfo.GetCultureInfo(language);
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (text, resource) in Shared)
            {
                var translated = Resources.GetString(resource, culture) ?? text;
                strings[text] = translated;
                strings[text + "…"] = translated + "…";
            }
            languages[language.ToLowerInvariant()] = strings;
        }

        // Steam may expose regional page tags rather than our resource tags.
        foreach (var (alias, target) in new[]
        {
            ("zh-cn", "zh-hans"), ("zh-sg", "zh-hans"),
            ("zh-tw", "zh-hant"), ("zh-hk", "zh-hant"), ("no", "nb"),
            ("pt", "pt-pt"),
        }) languages[alias] = languages[target];

        // Default escaping prevents resource data becoming executable script text.
        return JsonSerializer.Serialize(languages);
    }
}
