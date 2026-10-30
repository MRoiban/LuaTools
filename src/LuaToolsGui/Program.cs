using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using LuaToolsGui.Services;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (TryRunCabinetHelper(args)) return;
        StartMinimized = args.Contains("--minimized", StringComparer.Ordinal);
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
            if (StartMinimized) return;
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
        StartEventLog();

        var app = new App();
        app.DispatcherUnhandledException += (_, e) =>
            AppEventLog.Shared.Write(LogLevel.Error, "App", "Unhandled UI exception", e.Exception);
        app.InitializeComponent();
        app.Run();

        AppEventLog.Shared.Write(LogLevel.Information, "App", "Stopped");
        ShowWindowSignal.Dispose();
    }

    /// <summary>Record the session and every exception that would otherwise vanish with the process.</summary>
    private static void StartEventLog()
    {
        var log = AppEventLog.Shared;
        log.Start($"Started {Services.UpdateService.CurrentVersion} on {Environment.OSVersion.VersionString}, UI {CultureInfo.CurrentUICulture.Name}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Write(LogLevel.Critical, "App", "Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            log.Write(LogLevel.Error, "App", "Unobserved task exception", e.Exception);
    }

    private static bool TryRunCabinetHelper(string[] args) => CabinetEngine.HelperEntry.Handle(args);

    /// <summary>Named event a second instance sets to ask the running one to surface its window.</summary>
    private const string ShowWindowEventName = "LuaToolsGui.SecurityHardened.ShowWindow";

    /// <summary>Owned by the live instance; App wires a listener that restores the window when it's set.</summary>
    internal static EventWaitHandle? ShowWindowSignal;

    internal static bool StartMinimized { get; private set; }

    // Historical loader integration cannot force close-to-tray behavior in this fork.
    internal static bool SessionTrayLock => false;

    // Supported UI languages. Unsupported saved settings use automatic OS detection.
    // Keep in sync with the Settings language dropdown.
    private static readonly string[] SupportedLanguages = ["en", "fr"];

    /// <summary>Resolve the UI language (saved setting → OS display language → English) and apply it to
    /// the thread + WPF before any UI is built.</summary>
    private static void ApplyUiCulture()
    {
        try
        {
            string tag = ResolveLanguageTag(CultureInfo.InstalledUICulture);
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
    internal static string ResolveLanguageTag(CultureInfo os, string? settingsPath = null)
    {
        string? saved = ReadSavedLanguage(settingsPath);
        if (saved is not null && IsSupported(saved)) return saved;

        return MatchOsLanguage(os);
    }

    /// <summary>French OS cultures use French; every other OS culture uses English.</summary>
    internal static string MatchOsLanguage(CultureInfo os) =>
        os.TwoLetterISOLanguageName == "fr" ? "fr" : "en";

    private static bool IsSupported(string tag) =>
        SupportedLanguages.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Read just the "Language" key from the fork's settings.json without spinning up
    /// the DI container (this runs before App is constructed).</summary>
    private static string? ReadSavedLanguage(string? settingsPath)
    {
        try
        {
            string path = settingsPath ?? Path.Combine(
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
