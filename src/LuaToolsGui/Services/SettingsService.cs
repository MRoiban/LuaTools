using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LuaToolsGui.Services;

public class AppSettings
{
    public string? SteamPathOverride { get; set; }

    // ── Unlocker mode (Mode page). User's chosen backend ────────────
    // "Ost" | "Bst" | "Custom". Older builds wrote "SteamTools" | "OpenSteamTools" |
    // "OpenSteamToolsNightly" | "CloudRedirect"; see ModeMigration, which rewrites those on startup.
    public string? SelectedMode { get; set; }

    // ── Install behavior ─────────────────────────────────────────────
    // When true (default), installs comment out setManifestid() lines and skip copying .manifest
    // files, so games aren't pinned to a version and Steam keeps them updated. Nullable so we can
    // tell "never set" (→ default ON) from an explicit user choice.
    public bool? AutoUpdateApps { get; set; }

    // Legacy serialized field; always ignored and normalized to false by fork policy.
    public bool? DonateKeys { get; set; }

    // Manage-page results-per-page. 0 = "All" (single infinite scroll). Nullable so "never set"
    // (→ default 24) is distinguishable from an explicit choice.
    public int? ManagePageSize { get; set; }

    // Fixes-page results-per-page. 0 = "All" (single infinite scroll). Nullable so "never set"
    // (→ default 24) is distinguishable from an explicit choice.
    public int? FixesPageSize { get; set; }

    // Builds-page game-list results-per-page. 0 = "All". Kept separate from ManagePageSize. The Builds
    // list is a narrow sidebar, so a size that suits the Manage grid rarely suits both.
    public int? BuildsPageSize { get; set; }

    // UI language as a BCP-47 tag ("en", "zh-Hans"). Null = follow the Windows display language.
    public string? Language { get; set; }

    // The user's own Hubcap (hubcapmanifest.com) API key ("smm_…"). Null = not configured; key-gated
    // sources stay locked until set. Stored as CurrentUser DPAPI ciphertext, never plaintext.
    public string? HubcapApiKey { get; set; }

    // When true, register the app to launch on Windows sign-in (HKCU …\Run). Nullable so "never set"
    // (→ default OFF) is distinguishable from an explicit choice.
    public bool? StartWithWindows { get; set; }
    public bool? CabinetEnabled { get; set; }
    public bool? CabinetLivePlaytime { get; set; }
    public bool? CabinetFetchSchemas { get; set; }
    public bool? CabinetWorkshopLinks { get; set; }

    // When true, minimizing hides the window to the system tray instead of the taskbar. Nullable so
    // "never set" (→ default OFF) is distinguishable from an explicit choice.
    public bool? MinimizeToTray { get; set; }

    // When true, FastFetch auto-picks the first available source and downloads immediately.
    // Nullable so "never set" (→ default OFF) is distinguishable from an explicit choice.
    public bool? FastFetch { get; set; }

    public bool ExecutableGameFixesEnabled { get; set; }
    public bool BetterSteamToolsEnabled { get; set; }
    public bool OpenSteamToolsEnabled { get; set; }
    public bool SteamPluginEnabled { get; set; }
    public bool DepotDownloaderEnabled { get; set; }
    public bool SteamlessEnabled { get; set; }
    public bool SteamAutoCrackEnabled { get; set; }
    public bool CloudRedirectEnabled { get; set; }

    // Optional DNS-over-HTTPS preference; missing or invalid values use system DNS.
    public string? DnsMode { get; set; }
}

public class SettingsService : Cabinet.ICabinetSettings
{
    private readonly string Dir;
    private readonly string FilePath;
    private readonly string TmpPath;
    private readonly string BakPath;
    private readonly bool _persist;
    private readonly IStartupRegistration _startup;
    private AppSettings _settings = new();

    public SettingsService() : this(null, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirectoryName), new WindowsStartupRegistration()) { }

    // Without a directory, policy tests remain entirely in memory. A supplied directory isolates
    // persistence tests from the real user profile and exercises the normal atomic save/load path.
    internal SettingsService(AppSettings? settings, string? directory = null, IStartupRegistration? startup = null)
    {
        _startup = startup ?? new MemoryStartupRegistration();
        _persist = directory is not null;
        Dir = directory is null ? string.Empty : Path.GetFullPath(directory);
        FilePath = Path.Combine(Dir, "settings.json");
        TmpPath = FilePath + ".tmp";
        BakPath = FilePath + ".bak";
        if (settings is not null) _settings = settings;
        else if (_persist) Load();
        EnforceForkPolicy();
    }

    private void EnforceForkPolicy()
    {
        _settings.DonateKeys = false;
        if (UnprotectApiKey(_settings.HubcapApiKey) is null) _settings.HubcapApiKey = null;
    }

    /// <summary>User-chosen Steam folder. Null = auto-detect from registry. Persisted only when set.</summary>
    public string? SteamPathOverride
    {
        get => _settings.SteamPathOverride;
        set
        {
            _settings.SteamPathOverride = string.IsNullOrWhiteSpace(value) ? null : value;
            Save();
        }
    }

    /// <summary>Selected unlocker backend ("SteamTools" | "OpenSteamTools"), or null if never chosen.</summary>
    public string? SelectedMode
    {
        get => _settings.SelectedMode;
        set { _settings.SelectedMode = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>When true (default), installs don't lock manifests so apps keep auto-updating.</summary>
    public bool AutoUpdateApps
    {
        get => _settings.AutoUpdateApps ?? true; // default ON
        set { _settings.AutoUpdateApps = value; Save(); }
    }

    /// <summary>Donation is permanently disabled, including for existing settings.</summary>
    public bool DonateKeys
    {
        get => false;
        set { _settings.DonateKeys = false; Save(); }
    }

    /// <summary>Manage-page results-per-page (default 24). 0 = "All" (single infinite scroll).</summary>
    public int ManagePageSize
    {
        get => _settings.ManagePageSize ?? 24; // default 24
        set { _settings.ManagePageSize = value; Save(); }
    }

    /// <summary>Fixes-page results-per-page (default 24). 0 = "All" (single infinite scroll).</summary>
    public int FixesPageSize
    {
        get => _settings.FixesPageSize ?? 24; // default 24
        set { _settings.FixesPageSize = value; Save(); }
    }

    /// <summary>Builds-page game-list results-per-page (default 10). 0 = "All". Smaller than the other
    /// pages' 24. This list is a narrow sidebar next to the build detail, not a full-width grid.</summary>
    public int BuildsPageSize
    {
        get => _settings.BuildsPageSize ?? 10; // default 10
        set { _settings.BuildsPageSize = value; Save(); }
    }

    /// <summary>UI language tag ("en" | "zh-Hans"), or null to follow the Windows display language.</summary>
    public string? Language
    {
        get => _settings.Language;
        set { _settings.Language = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>The user's Hubcap API key ("smm_…"), or null if not configured.</summary>
    public string? HubcapApiKey
    {
        get => UnprotectApiKey(_settings.HubcapApiKey);
        set { _settings.HubcapApiKey = ProtectApiKey(value); Save(); }
    }

    /// <summary>When true, the app is registered to launch on Windows sign-in (default OFF).</summary>
    public bool StartWithWindows
    {
        get => _startup.IsEnabled;
        set { _startup.SetEnabled(value); _settings.StartWithWindows = value; Save(); }
    }

    // Optional Cabinet settings preserve the contract defaults until explicitly changed.
    public bool CabinetEnabled
    {
        get => _settings.CabinetEnabled ?? false;
        set { _settings.CabinetEnabled = value; Save(); }
    }

    public bool CabinetLivePlaytime
    {
        get => _settings.CabinetLivePlaytime ?? true;
        set { _settings.CabinetLivePlaytime = value; Save(); }
    }

    public bool CabinetFetchSchemas
    {
        get => _settings.CabinetFetchSchemas ?? true;
        set { _settings.CabinetFetchSchemas = value; Save(); }
    }

    public bool CabinetWorkshopLinks
    {
        get => _settings.CabinetWorkshopLinks ?? false;
        set { _settings.CabinetWorkshopLinks = value; Save(); }
    }



    /// <summary>When true, minimizing hides the window to the system tray (default OFF).</summary>
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray ?? false; // default OFF
        set { _settings.MinimizeToTray = value; Save(); }
    }

    /// <summary>When true, FastFetch auto-picks the first available source and downloads immediately (default OFF).</summary>
    public bool FastFetch
    {
        get => _settings.FastFetch ?? false;
        set { _settings.FastFetch = value; Save(); }
    }

    /// <summary>Validated DNS preference; system DNS is the default, DNS-over-HTTPS requires opt-in.</summary>
    public string DnsMode
    {
        get => _settings.DnsMode is "Auto" or "Always" or "Never" ? _settings.DnsMode : "Never";
        set { _settings.DnsMode = value is "Auto" or "Always" or "Never" ? value : "Never"; Save(); }
    }

    public bool ExecutableGameFixesEnabled
    {
        get => _settings.ExecutableGameFixesEnabled;
        set
        {
            if (_settings.ExecutableGameFixesEnabled == value) return;
            _settings.ExecutableGameFixesEnabled = value;
            try { Save(); }
            catch { _settings.ExecutableGameFixesEnabled = !value; throw; }
            ExecutableWorkflowChanged?.Invoke("DenuvoFix", value);
        }
    }

    public bool BetterSteamToolsEnabled
    {
        get => _settings.BetterSteamToolsEnabled;
        set
        {
            if (_settings.BetterSteamToolsEnabled == value) return;
            _settings.BetterSteamToolsEnabled = value;
            try { Save(); }
            catch { _settings.BetterSteamToolsEnabled = !value; throw; }
            ExecutableWorkflowChanged?.Invoke("BetterSteamTools", value);
        }
    }

    public bool OpenSteamToolsEnabled
    {
        get => _settings.OpenSteamToolsEnabled;
        set
        {
            if (_settings.OpenSteamToolsEnabled == value) return;
            _settings.OpenSteamToolsEnabled = value;
            try { Save(); }
            catch { _settings.OpenSteamToolsEnabled = !value; throw; }
            ExecutableWorkflowChanged?.Invoke("OpenSteamTools", value);
        }
    }

    public bool SteamPluginEnabled
    {
        get => _settings.SteamPluginEnabled;
        set
        {
            if (_settings.SteamPluginEnabled == value) return;
            _settings.SteamPluginEnabled = value;
            try { Save(); }
            catch { _settings.SteamPluginEnabled = !value; throw; }
            ExecutableWorkflowChanged?.Invoke("LuaToolsPlugin", value);
        }
    }

    public bool DepotDownloaderEnabled
    {
        get => _settings.DepotDownloaderEnabled;
        set
        {
            if (_settings.DepotDownloaderEnabled == value) return;
            _settings.DepotDownloaderEnabled = value;
            try { Save(); }
            catch { _settings.DepotDownloaderEnabled = !value; throw; }
            ExecutableWorkflowChanged?.Invoke("DepotDownloader", value);
        }
    }

    public bool SteamlessEnabled
    {
        get => _settings.SteamlessEnabled;
        set
        {
            if (_settings.SteamlessEnabled == value) return;
            _settings.SteamlessEnabled = value;
            try { Save(); }
            catch { _settings.SteamlessEnabled = !value; throw; }
            ExecutableWorkflowChanged?.Invoke("Steamless", value);
        }
    }

    public bool SteamAutoCrackEnabled
    {
        get => _settings.SteamAutoCrackEnabled;
        set
        {
            if (_settings.SteamAutoCrackEnabled == value) return;
            _settings.SteamAutoCrackEnabled = value;
            try { Save(); }
            catch { _settings.SteamAutoCrackEnabled = !value; throw; }
            ExecutableWorkflowChanged?.Invoke("SteamAutoCrack", value);
        }
    }

    public bool CloudRedirectEnabled
    {
        get => _settings.CloudRedirectEnabled;
        set
        {
            if (_settings.CloudRedirectEnabled == value) return;
            _settings.CloudRedirectEnabled = value;
            try { Save(); }
            catch { _settings.CloudRedirectEnabled = !value; throw; }
            ExecutableWorkflowChanged?.Invoke("CloudRedirect", value);
        }
    }

    public event Action<string, bool>? ExecutableWorkflowChanged;

    public bool IsExecutableWorkflowEnabled(string component) => component switch
    {
        "DenuvoFix" => ExecutableGameFixesEnabled,
        "BetterSteamTools" => BetterSteamToolsEnabled,
        "OpenSteamTools" => OpenSteamToolsEnabled,
        "LuaToolsPlugin" or "LuaToolsPluginFrontend" => SteamPluginEnabled,
        "DepotDownloader" => DepotDownloaderEnabled,
        "Steamless" => SteamlessEnabled,
        "SteamAutoCrack" => SteamAutoCrackEnabled,
        "CloudRedirect" or "CloudRedirectAddon" => CloudRedirectEnabled,
        _ => false,
    };

    private const string ApiKeyPrefix = "dpapi:v1:";

    internal static string? ProtectApiKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        byte[] plain = Encoding.UTF8.GetBytes(value);
        try
        {
            return ApiKeyPrefix + Convert.ToBase64String(
                ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static string? UnprotectApiKey(string? value)
    {
        if (value is null || !value.StartsWith(ApiKeyPrefix, StringComparison.Ordinal)) return null;
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(Convert.FromBase64String(value[ApiKeyPrefix.Length..]),
                null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private void Load()
    {
        // Prefer the primary file; fall back to the last-good .bak. Crucially, NEVER silently reset a
        // corrupt-but-present file to defaults (a later Save would then overwrite it and lose real data).
        // Move it aside to .corrupt so it's preserved and can't be clobbered.
        if (TryLoad(FilePath)) return;
        PreserveCorrupt(FilePath);
        if (TryLoad(BakPath)) return;
        _settings = new AppSettings();
    }

    private bool TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) is { } loaded)
            {
                _settings = loaded;
                return true;
            }
        }
        catch { /* missing / truncated / invalid JSON → caller falls through */ }
        return false;
    }

    /// <summary>A present-but-unparseable settings file is moved aside (not deleted) so its contents survive
    /// for manual recovery and a subsequent Save can't overwrite it.</summary>
    private static void PreserveCorrupt(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch { /* best effort */ }
    }

    private void Save()
    {
        EnforceForkPolicy();
        if (!_persist) return;

        // Nothing worth persisting → don't leave a settings file behind.
        // Every persisted field must be listed here. A field left out is treated as "nothing worth
        // keeping", so a user whose ONLY change was that setting gets the file deleted and the setting
        // silently lost on the next save. (FixesPageSize was missing.)
        bool empty = _settings.SteamPathOverride is null
            && _settings.SelectedMode is null
            && _settings.AutoUpdateApps is null
            && _settings.DonateKeys is null
            && _settings.ManagePageSize is null
            && _settings.FixesPageSize is null
            && _settings.BuildsPageSize is null
            && _settings.Language is null
            && _settings.HubcapApiKey is null
            && _settings.StartWithWindows is null
            && _settings.CabinetEnabled is null
            && _settings.CabinetLivePlaytime is null
            && _settings.CabinetFetchSchemas is null
            && _settings.CabinetWorkshopLinks is null
            && _settings.MinimizeToTray is null
            && _settings.FastFetch is null
            && _settings.DnsMode is null
            && !_settings.ExecutableGameFixesEnabled
            && !_settings.BetterSteamToolsEnabled
            && !_settings.OpenSteamToolsEnabled
            && !_settings.SteamPluginEnabled
            && !_settings.DepotDownloaderEnabled
            && !_settings.SteamlessEnabled
            && !_settings.SteamAutoCrackEnabled
            && !_settings.CloudRedirectEnabled;
        if (empty)
        {
            foreach (var p in new[] { FilePath, BakPath, TmpPath })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
            return;
        }

        Directory.CreateDirectory(Dir);
        string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });

        // Atomic write: fill a temp file, then rename it over the target. A crash/kill mid-write can only
        // ever truncate the .tmp. The live settings.json is replaced by an atomic move (same-volume rename)
        // and is therefore never left half-written. (This class of loss is exactly what a forced kill during
        // a plain WriteAllText caused.) A .bak of the last good file is kept as a second recovery source.
        File.WriteAllText(TmpPath, json);
        try { if (File.Exists(FilePath)) File.Copy(FilePath, BakPath, overwrite: true); } catch { /* best effort */ }
        File.Move(TmpPath, FilePath, overwrite: true);
    }
}
