using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace LuaToolsGui;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A signed update restart waits for the old single-instance owner to exit.
        if (args.Length == 2 && args[0] == "--fork-update-wait" && int.TryParse(args[1], out int parentId))
        {
            try { using var parent = System.Diagnostics.Process.GetProcessById(parentId); if (!parent.WaitForExit(30000)) return; }
            catch (ArgumentException) { /* Parent already exited. */ }
            catch (System.ComponentModel.Win32Exception) { return; }
            catch (InvalidOperationException) { return; }
        }
        if (Services.UpdateService.TryLaunchApproved()) return;

        // Set the UI culture before any WPF element is created, so x:Static resource lookups (which
        // resolve once at parse time) pick up the right language from the first frame.
        ApplyUiCulture();

        // Loader flags and external protocol URLs are deliberately not interpreted.
        using var single = new Mutex(initiallyOwned: true,
            "LuaToolsGui.SecurityHardened.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
                {
                    signal.Set();
                    signal.Dispose();
                }
            }
            catch { /* The existing instance remains usable if signalling fails. */ }
            return;
        }

        ShowWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);

        var app = new App();
        app.InitializeComponent();
        app.Run();

        ShowWindowSignal.Dispose();
    }

    /// <summary>Named event a second instance sets to ask the running one to surface its window.</summary>
    private const string ShowWindowEventName = "LuaToolsGui.SecurityHardened.ShowWindow";

    /// <summary>Owned by the live instance; App wires a listener that restores the window when it's set.</summary>
    internal static EventWaitHandle? ShowWindowSignal;

    // Historical loader integration cannot force close-to-tray behavior in this fork.
    internal static bool SessionTrayLock => false;

    // Supported UI languages (a Strings.<tag>.resx exists for each). A saved/OS culture not in here
    // falls back to English. Keep in sync with the Settings language dropdown.
    private static readonly string[] SupportedLanguages =
    [
        "en", "zh-Hans", "zh-Hant", "ja", "ko",
        "es", "es-419", "pt-BR", "pt-PT", "fr", "de", "it", "nl", "pl",
        "ru", "uk", "tr", "ar",
        "cs", "hu", "ro", "el", "bg", "th", "vi", "id", "da", "fi", "nb", "sv",
    ];

    /// <summary>Resolve the UI language (saved setting → OS display language → English) and apply it to
    /// the thread + WPF before any UI is built.</summary>
    private static void ApplyUiCulture()
    {
        try
        {
            string tag = ResolveLanguageTag();
            var culture = new CultureInfo(tag);

            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;

            // Make WPF date/number formatting (and any xml:lang-aware rendering) follow the UI culture.
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }
        catch
        {
            // Never let a culture problem stop the app from launching. Fall through to default (English).
        }
    }

    /// <summary>Saved language from settings.json if set + supported; else the OS display language if
    /// supported; else "en".</summary>
    private static string ResolveLanguageTag()
    {
        string? saved = ReadSavedLanguage();
        if (saved is not null && IsSupported(saved)) return saved;

        return MatchOsLanguage(CultureInfo.InstalledUICulture);
    }

    /// <summary>Map an OS culture to one of our supported tags. Tries the exact tag (e.g. "pt-BR"), then
    /// our regional variants, then the two-letter neutral parent (e.g. "es-ES" → "es", "fr-FR" → "fr").
    /// Falls back to "en". Internal-visible for tests.</summary>
    internal static string MatchOsLanguage(CultureInfo os)
    {
        // 1. Exact match: handles tags we ship verbatim (e.g. "pt-BR", "pt-PT", "zh-Hant", "es-419").
        if (IsSupported(os.Name)) return os.Name;

        string two = os.TwoLetterISOLanguageName; // "es", "fr", "pt", "zh", "nb"/"nn"...

        // 2. Chinese: script (Hans/Hant) matters more than region. zh-CN/zh-SG → Hans, zh-TW/HK/MO → Hant.
        if (two == "zh")
        {
            if (os.Name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0) return "zh-Hant";
            if (os.Name.IndexOf("Hans", StringComparison.OrdinalIgnoreCase) >= 0) return "zh-Hans";
            return os.Name is "zh-TW" or "zh-HK" or "zh-MO" ? "zh-Hant" : "zh-Hans";
        }

        // 3. Portuguese: we ship pt-BR + pt-PT but no neutral "pt". Default neutral/unknown → pt-PT (European).
        if (two == "pt") return "pt-PT";

        // 4. Spanish: we ship neutral "es" (Spain) + "es-419" (Latin America). Map LatAm regions to es-419.
        if (two == "es") return IsLatinAmericanSpanish(os.Name) ? "es-419" : "es";

        // 5. Norwegian: Bokmål ("nb") and Nynorsk ("nn") both fall back to our "nb".
        if (two is "nb" or "nn" or "no") return "nb";

        // 6. Generic: try the two-letter neutral parent (es-ES→es, fr-FR→fr, de-DE→de, ja-JP→ja, ...).
        if (IsSupported(two)) return two;

        return "en";
    }

    /// <summary>True for Spanish locales of Latin America (anything except Spain), so they map to es-419.</summary>
    private static bool IsLatinAmericanSpanish(string name) =>
        name.StartsWith("es-", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("es-ES", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupported(string tag) =>
        SupportedLanguages.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Read just the "Language" key from %AppData%\LuaToolsGui\settings.json without spinning up
    /// the DI container (this runs before App is constructed).</summary>
    private static string? ReadSavedLanguage()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppConfig.DataDirectoryName, "settings.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Language", out var el) && el.ValueKind == JsonValueKind.String)
            {
                string? v = el.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch { /* corrupt/missing → fall back to OS */ }
        return null;
    }
}
