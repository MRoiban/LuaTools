using LuaToolsGui.Services.Cabinet;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Models;
using Microsoft.Win32;

namespace LuaToolsGui.ViewModels;

/// <summary>A selectable UI language. <see cref="Tag"/> is the BCP-47 tag ("en", "zh-Hans") or null for
/// "follow the system display language".</summary>
public record LanguageOption(string Display, string? Tag);

/// <summary>A DNS resolution mode. <see cref="Tag"/> is the stored value and stays English ("Auto",
/// "Always", "Never") because it is matched in code; only <see cref="Display"/> is localized.</summary>

public record DnsModeOption(string Display, string Tag);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ICabinetService _cabinet;
    private readonly ICabinetCloud? _cloud;
    private readonly AuthService _auth;
    private readonly SteamService _steam;
    private readonly HubcapService _hubcap;

    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _avatarUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRealUser))]
    private bool _isGuest = true;

    public bool IsRealUser => !IsGuest;

    // ── Login-required redirect banner ─────────────────────────────
    /// <summary>Set by App when navigating here from a protected action. Null = banner hidden.</summary>
    [ObservableProperty] private string? _loginRequiredMessage;

    [RelayCommand]
    private void DismissLoginRequired() => LoginRequiredMessage = null;

    // ── Bot-provisioned account re-link banner ──────────────────────
    /// <summary>True when the signed-in session is a Discord bot placeholder (@bot.lua.tools).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotLinkBanner))]
    private bool _isBotProvisioned;

    /// <summary>Session-only. Resets next launch so the banner re-checks on every startup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotLinkBanner))]
    private bool _botBannerDismissed;

    public bool ShowBotLinkBanner => false;

    // ── Steam location ──────────────────────────────────────────────
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _isSteamOverridden;
    [ObservableProperty] private string _steamSource = "";
    [ObservableProperty] private string? _steamWarning;

    // ── Install behavior ────────────────────────────────────────────
    /// <summary>Auto Update Apps (Don't Lock Manifests). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _autoUpdateApps;

    partial void OnAutoUpdateAppsChanged(bool value) => _settings.AutoUpdateApps = value;

    /// <summary>FastFetch: auto-download from the first available source. Same persisted setting the Add
    /// screen's toggle drives (both read/write SettingsService.FastFetch).</summary>
    [ObservableProperty] private bool _fastFetch;

    partial void OnFastFetchChanged(bool value) => _settings.FastFetch = value;

    [ObservableProperty] private string _cloudPairingCode = "";
    [ObservableProperty] private string _cloudServer = "";
    [ObservableProperty] private string? _cloudError;
    [ObservableProperty] private bool _cloudBusy;
    partial void OnCloudPairingCodeChanged(string value)
    {
        if (CabinetConnectInput.TryNormalizeCode(value, out var code))
        {
            string formatted = CabinetConnectInput.FormatCode(code);
            if (value != formatted) CloudPairingCode = formatted;
        }
    }
    public bool CloudConnected => _cloud?.Status.Connected == true;
    public bool CloudFriendsEnabled => CabinetEnabled && CloudConnected && !CloudBusy;
    public string CloudIdentity => string.Format(Resources.Strings.CabinetCloud_ConnectedAs, _cloud?.Status.UserName);
    public string CloudDetails => string.Format(Resources.Strings.CabinetCloud_Details, _cloud?.Status.ServerHost,
        _cloud?.Status.LastSync?.ToLocalTime().ToString("g") ?? Resources.Strings.Cabinet_Never);
    public bool CloudFriendLabels
    {
        get => _cloud?.FriendLabels == true;
        set
        {
            try { if (_cloud is not null) _cloud.FriendLabels = value; CloudError = null; }
            catch { CloudError = Resources.Strings.CabinetCloud_DebugError; }
            OnPropertyChanged();
        }
    }
    partial void OnCloudBusyChanged(bool value) => RefreshCloud();
    private void RefreshCloud()
    {
        OnPropertyChanged(nameof(CloudConnected)); OnPropertyChanged(nameof(CloudFriendsEnabled));
        OnPropertyChanged(nameof(CloudIdentity)); OnPropertyChanged(nameof(CloudDetails)); OnPropertyChanged(nameof(CloudFriendLabels));
    }
    [RelayCommand]
    private async Task ConnectCloudAsync()
    {
        if (_cloud is null || !CabinetEnabled || CloudBusy) return;
        CloudBusy = true; CloudError = null;
        try { await _cloud.ConnectAsync(CloudPairingCode.Trim(), CloudServer); CloudPairingCode = ""; }
        catch (CabinetConnectException ex) { CloudError = ex.Message; }
        catch (FormatException) { CloudError = Resources.Strings.CabinetCloud_InvalidLink; }
        catch { CloudError = Resources.Strings.CabinetCloud_ConnectError; }
        finally { CloudBusy = false; RefreshCloud(); }
    }
    [RelayCommand]
    private async Task DisconnectCloudAsync()
    {
        if (_cloud is null || CloudBusy) return;
        CloudBusy = true; CloudError = null;
        try { await _cloud.DisconnectAsync(); }
        catch { CloudError = Resources.Strings.CabinetCloud_DisconnectError; }
        finally { CloudBusy = false; RefreshCloud(); }
    }

    private bool _refreshingCabinetSettings;
    [ObservableProperty] private bool _startWithWindows;
    partial void OnStartWithWindowsChanged(bool value) { if (!_refreshingCabinetSettings) _settings.StartWithWindows = value; }
    public bool CabinetOptionsEnabled => CabinetEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CabinetOptionsEnabled))]
    private bool _cabinetEnabled;
    partial void OnCabinetEnabledChanged(bool value) { if (_refreshingCabinetSettings) return; _settings.CabinetEnabled = value; _cabinet.SettingsChanged(); RefreshCloud(); }
    [ObservableProperty] private bool _cabinetLivePlaytime;
    partial void OnCabinetLivePlaytimeChanged(bool value) { if (_refreshingCabinetSettings) return; _settings.CabinetLivePlaytime = value; _cabinet.SettingsChanged(); }
    [ObservableProperty] private bool _cabinetFetchSchemas;
    partial void OnCabinetFetchSchemasChanged(bool value) { if (_refreshingCabinetSettings) return; _settings.CabinetFetchSchemas = value; _cabinet.SettingsChanged(); }
    [ObservableProperty] private bool _cabinetWorkshopLinks;
    partial void OnCabinetWorkshopLinksChanged(bool value) { if (_refreshingCabinetSettings) return; _settings.CabinetWorkshopLinks = value; _cabinet.SettingsChanged(); }

    // Executable workflow switches persist consent only; actions remain on their original pages.
    [ObservableProperty] private bool _executableGameFixesEnabled;
    partial void OnExecutableGameFixesEnabledChanged(bool value) => _settings.ExecutableGameFixesEnabled = value;

    [ObservableProperty] private bool _betterSteamToolsEnabled;
    partial void OnBetterSteamToolsEnabledChanged(bool value) => _settings.BetterSteamToolsEnabled = value;

    [ObservableProperty] private bool _openSteamToolsEnabled;
    partial void OnOpenSteamToolsEnabledChanged(bool value) => _settings.OpenSteamToolsEnabled = value;

    [ObservableProperty] private bool _steamPluginEnabled;
    partial void OnSteamPluginEnabledChanged(bool value) => _settings.SteamPluginEnabled = value;

    [ObservableProperty] private bool _depotDownloaderEnabled;
    partial void OnDepotDownloaderEnabledChanged(bool value) => _settings.DepotDownloaderEnabled = value;

    [ObservableProperty] private bool _steamlessEnabled;
    partial void OnSteamlessEnabledChanged(bool value) => _settings.SteamlessEnabled = value;

    [ObservableProperty] private bool _steamAutoCrackEnabled;
    partial void OnSteamAutoCrackEnabledChanged(bool value) => _settings.SteamAutoCrackEnabled = value;

    [ObservableProperty] private bool _cloudRedirectEnabled;
    partial void OnCloudRedirectEnabledChanged(bool value) => _settings.CloudRedirectEnabled = value;

    /// <summary>Donate spare Steam decryption keys to the community pool. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _donateKeys;


    // ── Startup behavior ────────────────────────────────────────────
    [ObservableProperty] private bool _minimizeToTray;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        if (!value) RequestShowWindow?.Invoke(); // turning it off restores the window if it's hidden in the tray
    }

    /// <summary>Set by App: restore the main window from the tray (used when Minimize-to-tray is turned off).</summary>
    public Action? RequestShowWindow { get; set; }

    // ── Language ────────────────────────────────────────────────────
    /// <summary>English and French, with a system default (null tag) that follows Windows.</summary>
    public ObservableCollection<LanguageOption> LanguageOptions { get; } =
    [
        new(Resources.Strings.Settings_Language_SystemDefault, null),
        new("English", "en"),
        new("Français", "fr"),
    ];

    [ObservableProperty] private LanguageOption _selectedLanguage = null!;

    // ── DNS ─────────────────────────────────────────────────────────
    /// <summary>How host names are resolved. See <see cref="Services.AppHttp"/>.</summary>
    public ObservableCollection<DnsModeOption> DnsModeOptions { get; } =
    [
        new(Resources.Strings.Settings_Dns_Auto, "Auto"),
        new(Resources.Strings.Settings_Dns_Always, "Always"),
        new(Resources.Strings.Settings_Dns_Never, "Never"),
    ];

    [ObservableProperty] private DnsModeOption _selectedDnsMode = null!;

    // Takes effect on new connections; pooled ones turn over within AppHttp's connection lifetime, so
    // there is no restart prompt here (unlike the language switch, which is baked in at parse time).
    partial void OnSelectedDnsModeChanged(DnsModeOption value)
    {
        if (value is not null) _settings.DnsMode = value.Tag;
    }

    private bool _suppressLanguagePrompt; // true during ctor init so we don't prompt on first bind

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null || _suppressLanguagePrompt) return;
        _settings.Language = value.Tag; // null = follow system
        // The whole UI is built with parse-time x:Static resources, so a relaunch is needed to re-read it.
        RequestRestartPrompt?.Invoke();
    }

    // ── Hubcap API key ──────────────────────────────────────────────
    /// <summary>The key the user is typing/pasting. Starts blank. The saved key is never shown back.</summary>
    [ObservableProperty] private string _hubcapKeyInput = "";

    /// <summary>Status line under the key box (usage/expiry on success, or an error). Null = hidden.</summary>
    [ObservableProperty] private string? _hubcapKeyStatus;

    /// <summary>Color for the status line: red on error, green on success.</summary>
    [ObservableProperty] private string _hubcapKeyStatusColor = "#22c55e";
    /// <summary>True when a key is saved in settings (drives the "Clear" button + "configured" label).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHubcapStats))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsPending))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsText))]
    private bool _hubcapIsKeyConfigured;

    /// <summary>True while validating (disables the buttons via <see cref="CanEditHubcapKey"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditHubcapKey))]
    private bool _isValidatingHubcapKey;

    public bool CanEditHubcapKey => !IsValidatingHubcapKey;

    [ObservableProperty] private bool _isRefreshingHubcapStats;

    /// <summary>Latest stats for the saved key. Null = not loaded / no key / error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HubcapStatsDisplay))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsText))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsPending))]
    [NotifyPropertyChangedFor(nameof(HubcapUsagePercent))]
    [NotifyPropertyChangedFor(nameof(ShowHubcapStats))]
    private HubcapStats? _hubcapStats;

    /// <summary>"12 / 25 requests today" or "12 / 25 requests today · expires 2026-08-15".</summary>
    public string? HubcapStatsDisplay =>
        HubcapStats is not null ? FormatHubcapStats(HubcapStats) : null;

    /// <summary>Progress bar fill 0.0–1.0.</summary>
    public double HubcapUsagePercent =>
        HubcapStats is { DailyLimit: > 0 } ? (double)HubcapStats.DailyUsage / HubcapStats.DailyLimit : 0;

    /// <summary>Show the usage card whenever a key is configured. It renders a loading state until stats
    /// arrive (never blank), so the section can't look broken while the fetch is in flight or after a
    /// transient failure (the key input is collapsed once configured, so this card is the only content).</summary>
    public bool ShowHubcapStats => HubcapIsKeyConfigured;

    /// <summary>Stats not yet loaded for a configured key → show the "Loading…" placeholder + indeterminate bar.</summary>
    public bool HubcapStatsPending => HubcapIsKeyConfigured && HubcapStats is null;

    /// <summary>Placeholder text shown until real stats load.</summary>
    public string HubcapStatsText => HubcapStatsDisplay ?? Resources.Strings.Common_Loading;

    /// <summary>Set by App so the guest "Sign in" button can run the Discord flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    /// <summary>Set by App: actually relaunch the app (used after a language change).</summary>
    public Action? RequestRestart { get; set; }

    // ── Updates ─────────────────────────────────────────────────────

    public string AppVersionText => string.Format(Resources.Strings.Settings_Updates_Version, FormatVersion(UpdateService.CurrentVersion));
    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private bool _isUpdateStaged;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))] private bool _isCheckingForUpdates;

    /// <summary>Set by App: run a signed-update check now.</summary>
    public Func<Task<(UpdateCheckResult Result, Version? Staged)>>? RequestUpdateCheck { get; set; }
    /// <summary>Set by App: approve the staged update and restart into it.</summary>
    public Action? RequestApplyUpdate { get; set; }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        if (RequestUpdateCheck is null) return;
        IsCheckingForUpdates = true;
        UpdateStatus = Resources.Strings.Settings_Updates_Checking;
        try
        {
            var (result, staged) = await RequestUpdateCheck();
            ShowUpdateResult(result, staged);
        }
        catch { ShowUpdateResult(UpdateCheckResult.Unavailable, null); }
        finally { IsCheckingForUpdates = false; }
    }
    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    [RelayCommand]
    private void ApplyUpdate() => RequestApplyUpdate?.Invoke();

    public void ShowUpdateResult(UpdateCheckResult result, Version? staged)
    {
        IsUpdateStaged = result == UpdateCheckResult.Staged && staged is not null;
        UpdateStatus = result switch
        {
            UpdateCheckResult.Staged when staged is not null => string.Format(Resources.Strings.Settings_Updates_Ready, FormatVersion(staged)),
            UpdateCheckResult.UpToDate => Resources.Strings.Settings_Updates_UpToDate,
            UpdateCheckResult.Unsupported => Resources.Strings.Settings_Updates_Unsupported,
            _ => Resources.Strings.Settings_Updates_Failed,
        };
    }

    private static string FormatVersion(Version v) => v.Build < 0 ? $"{v.Major}.{v.Minor}" : $"{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>Show the "language changed. Restart now?" toast. Wired here so the VM stays UI-agnostic;
    /// App provides the toast + restart action.</summary>
    public Action? RequestRestartPrompt { get; set; }

    public SettingsViewModel(SettingsService settings, AuthService auth, SteamService steam,
        HubcapService hubcap, ICabinetService cabinet, ICabinetCloud? cloud = null)
    {
        _settings = settings;
        _cabinet = cabinet;
        _cloud = cloud;
        if (cloud is not null) cloud.StatusChanged += (_, _) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) RefreshCloud();
            else dispatcher.BeginInvoke(RefreshCloud);
        };
        _cabinetEnabled = settings.CabinetEnabled;
        _cabinetLivePlaytime = settings.CabinetLivePlaytime;
        _cabinetFetchSchemas = settings.CabinetFetchSchemas;
        _cabinetWorkshopLinks = settings.CabinetWorkshopLinks;
        _startWithWindows = settings.StartWithWindows;
        RefreshCloud();
        _auth = auth;
        _steam = steam;
        _hubcap = hubcap;
        _auth.AuthStateChanged += RefreshAccount;
        RefreshAccount();
        RefreshSteam();
        _autoUpdateApps = settings.AutoUpdateApps; // init from saved value (default ON) without triggering Save
        _fastFetch = settings.FastFetch;
        _executableGameFixesEnabled = settings.ExecutableGameFixesEnabled;
        _betterSteamToolsEnabled = settings.BetterSteamToolsEnabled;
        _openSteamToolsEnabled = settings.OpenSteamToolsEnabled;
        _steamPluginEnabled = settings.SteamPluginEnabled;
        _depotDownloaderEnabled = settings.DepotDownloaderEnabled;
        _steamlessEnabled = settings.SteamlessEnabled;
        _steamAutoCrackEnabled = settings.SteamAutoCrackEnabled;
        _cloudRedirectEnabled = settings.CloudRedirectEnabled;
        _minimizeToTray = settings.MinimizeToTray;
        _hubcapIsKeyConfigured = !string.IsNullOrEmpty(settings.HubcapApiKey);

        // Select the saved language (or "System default") without firing the restart prompt.
        _suppressLanguagePrompt = true;
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Tag == settings.Language) ?? LanguageOptions[0];
        _suppressLanguagePrompt = false;
        _selectedDnsMode = DnsModeOptions.FirstOrDefault(o => o.Tag == settings.DnsMode)
            ?? DnsModeOptions.First(o => o.Tag == "Never");

        // Assign the backing field, not the property: going through the setter would fire the change
        // handler and write the default straight back to disk on first open.
    }

    private void RefreshSteam()
    {
        string? path = _steam.EffectivePath;
        IsSteamOverridden = _steam.IsOverridden;
        SteamPath = path ?? Resources.Strings.Settings_SteamNotFound;
        SteamSource = IsSteamOverridden ? Resources.Strings.Settings_SteamSource_Custom
            : path is null ? Resources.Strings.Settings_SteamSource_NotFound
            : Resources.Strings.Settings_SteamSource_Auto;
        SteamWarning = path is not null && !_steam.IsValid ? Resources.Strings.Settings_SteamWarning_NoExe : null;
    }

    public void RefreshAccount()
    {
        IsGuest = _auth.IsGuest;
        DisplayName = _auth.DisplayName;
        Email = _auth.Email;
        AvatarUrl = _auth.AvatarUrl;
        IsBotProvisioned = _auth.IsBotProvisioned;
        if (!IsGuest) LoginRequiredMessage = null;
    }

    /// <summary>Hide the re-link banner for this session (returns next launch if still a bot account).</summary>
    [RelayCommand]
    private void DismissBotBanner() => BotBannerDismissed = true;

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (RequestSignIn is not null) await RequestSignIn();
    }

    // ── Discord bot code sign-in ────────────────────────────────────

    /// <summary>The 6-char code the user typed from the Discord <c>/login</c> DM.</summary>
    [ObservableProperty] private string _codeInput = "";

    /// <summary>True while redeeming. Disables the Redeem button via <see cref="CanRedeemCode"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedeemCode))]
    private bool _isRedeemingCode;

    /// <summary>Error shown under the code box (expired/invalid/server). Null = hidden.</summary>
    [ObservableProperty] private string? _codeError;

    public bool CanRedeemCode => !IsRedeemingCode;

    /// <summary>Redeem the Discord bot code for a session (no browser needed).</summary>
    [RelayCommand]
    private async Task SignInWithCodeAsync()
    {
        string code = CodeInput.Trim();
        if (code.Length != 6) return;

        IsRedeemingCode = true;
        CodeError = null;
        try
        {
            await _auth.SignInWithCodeAsync(code);
            CodeInput = "";
        }
        catch (Exception ex)
        {
            CodeError = ex.Message;
        }
        finally
        {
            IsRedeemingCode = false;
        }
    }

    [RelayCommand]
    private void OverrideSteamFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Settings_ChooseSteamFolder,
            InitialDirectory = _steam.EffectivePath ?? "",
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.SteamPathOverride = dialog.FolderName;
            RefreshSteam();
        }
    }

    [RelayCommand]
    private void ClearSteamOverride()
    {
        _settings.SteamPathOverride = null;
        RefreshSteam();
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        string? path = _steam.EffectivePath;
        if (path is not null && System.IO.Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenWebsite() =>
        Process.Start(new ProcessStartInfo(AppConfig.ApiBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private void OpenHubcap() =>
        Process.Start(new ProcessStartInfo(AppConfig.HubcapBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private void SignOut() => _auth.SignOut();

    // ── Hubcap key management ───────────────────────────────────────

    /// <summary>Refresh page bindings without persisting or reconfiguring the engine.</summary>
    public void RefreshCabinetSettings()
    {
        _refreshingCabinetSettings = true;
        try
        {
            CabinetEnabled = _settings.CabinetEnabled;
            CabinetLivePlaytime = _settings.CabinetLivePlaytime;
            CabinetFetchSchemas = _settings.CabinetFetchSchemas;
            CabinetWorkshopLinks = _settings.CabinetWorkshopLinks;
            StartWithWindows = _settings.StartWithWindows;
        }
        finally { _refreshingCabinetSettings = false; RefreshCloud(); }
    }

    /// <summary>Called by the View when loaded. Auto-refreshes stats if a key is saved.</summary>
    public void OnViewLoaded()
    {
        // Cabinet's Turn on action may have changed settings while this singleton page was hidden.
        RefreshCabinetSettings();
        if (HubcapIsKeyConfigured)
            RefreshHubcapStatsCommand.Execute(null);
        // Re-sync FastFetch in case the Add screen's toggle changed it this session (both are singletons
        // that only read the setting at construction). No-op if unchanged; a real change writes back the
        // same value, so no feedback loop.
        FastFetch = _settings.FastFetch;
    }

    /// <summary>Re-fetch usage stats for the saved key. Silent no-op if no key is saved.</summary>
    [RelayCommand]
    private async Task RefreshHubcapStatsAsync()
    {
        string? key = _settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key)) return;

        IsRefreshingHubcapStats = true;
        try
        {
            HubcapStats = await _hubcap.GetStatsAsync(key);
        }
        catch
        {
            HubcapStats = null;
        }
        finally
        {
            IsRefreshingHubcapStats = false;
        }
    }

    /// <summary>Validate the typed key (format first, then a live stats call) and, if good, save it.</summary>
    [RelayCommand]
    private async Task ValidateAndSaveHubcapKeyAsync()
    {
        string key = HubcapKeyInput.Trim();

        if (!HubcapService.IsValidKeyFormat(key))
        {
            ShowHubcapStatus(Resources.Strings.Settings_HubcapKeyBad, isError: true);
            return;
        }

        IsValidatingHubcapKey = true;
        try
        {
            var stats = await _hubcap.GetStatsAsync(key);
            if (stats is null)
            {
                // Could be a bad/expired key (401), or a network problem. Both surface as null.
                ShowHubcapStatus(Resources.Strings.Settings_HubcapKeyError, isError: true);
                return;
            }

            _settings.HubcapApiKey = key;
            HubcapIsKeyConfigured = true;
            HubcapKeyInput = "";
            HubcapKeyStatus = null;
            HubcapStats = stats;
        }
        finally
        {
            IsValidatingHubcapKey = false;
        }
    }

    /// <summary>Forget the saved key and reset the status line.</summary>
    [RelayCommand]
    private void ClearHubcapKey()
    {
        _settings.HubcapApiKey = null;
        HubcapIsKeyConfigured = false;
        HubcapKeyInput = "";
        HubcapKeyStatus = null;
        HubcapStats = null;
    }

    private void ShowHubcapStatus(string text, bool isError)
    {
        HubcapKeyStatus = text;
        HubcapKeyStatusColor = isError ? "#f87171" : "#22c55e";
    }

    /// <summary>"4/10 today". Appends "· expires yyyy-MM-dd" when the key has an expiry.</summary>
    private static string FormatHubcapStats(HubcapStats stats)
    {
        string usage = string.Format(Resources.Strings.Settings_HubcapKeyOk, stats.DailyUsage, stats.DailyLimit);
        if (DateTimeOffset.TryParse(stats.ApiKeyExpiresAt, out var expiry))
            return string.Format(Resources.Strings.Settings_HubcapKeyOkExpiry, stats.DailyUsage, stats.DailyLimit,
                expiry.ToString("yyyy-MM-dd"));
        return usage;
    }
}
