using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;

namespace LuaToolsGui.ViewModels;

/// <summary>One card on the Mode page. A single unlocker backend's name, status, and action button.</summary>
public partial class ModeCardViewModel(UnlockerMode mode, string title, string description) : ObservableObject
{
    public UnlockerMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Description { get; } = description;

    [ObservableProperty] private string _statusText = Resources.Strings.Mode_Checking;
    [ObservableProperty] private string _buttonText = Resources.Strings.Mode_Btn_Install;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActionButton))]
    private bool _isActive;

    /// <summary>
    /// Custom has nothing to install, update or reinstall, so once it's the active mode there is no
    /// action left to offer and the button is hidden. The ACTIVE badge already says it's selected.
    /// Every other card always has something to do (install, update, or switch to it).
    /// </summary>
    public bool ShowActionButton => !(Mode == UnlockerMode.Custom && IsActive);

    /// <summary>Our own fork is the steer-here option.</summary>
    public bool IsRecommended => Mode == UnlockerMode.Bst;

    /// <summary>OST is the nightly channel, so it carries the amber warning.</summary>
    public bool IsExperimental => Mode == UnlockerMode.Ost;

    /// <summary>Both OpenSteamTool-derived builds carry native CloudRedirect support.</summary>
    public bool SupportsCloudRedirect => Mode is UnlockerMode.Ost or UnlockerMode.Bst;
}

/// <summary>
/// "Mode" page: OpenSteamTools / BetterSteamTools / Custom. Mutually exclusive, one active at a time.
/// Checks status on page open; each card installs/switches after a Steam-shutdown confirmation, then
/// relaunches Steam so the new mode takes effect.
/// </summary>
public partial class ModeViewModel : ObservableObject
{
    private readonly UnlockerService _unlocker;
    private readonly ModeCoordinator _operations;
    private readonly ToastService _toast;
    private readonly SteamService _steam;
    private readonly CloudRedirectService _cloudRedirect;
    private readonly CabinetService? _cabinet;

    public ObservableCollection<ModeCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    // ── Steam-shutdown confirmation overlay ──────────────────────────
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    private ModeCardViewModel? _pendingCard;

    public ModeViewModel(UnlockerService unlocker, ToastService toast, SteamService steam,
        CloudRedirectService cloudRedirect, CabinetService? cabinet = null, ModeCoordinator? operations = null)
    {
        _unlocker = unlocker;
        _toast = toast;
        _steam = steam;
        _cloudRedirect = cloudRedirect;
        _cabinet = cabinet;
        _operations = operations ?? new(unlocker, steam, cloudRedirect, cabinet);
    }

    /// <summary>CloudRedirect "Manage" (add-on panel): download (cache) the CloudRedirect GUI and launch it.</summary>
    [RelayCommand]
    private async Task ManageCloudRedirect()
    {
        try
        {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            bool ok = await _operations.ManageCloudAsync(prog);
            if (!ok)
                _toast.Show(Resources.Strings.Mode_CloudRedirect_Manage,
                    Resources.Strings.Mode_CloudRedirect_LaunchFailed, error: true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
            }
        catch (Exception ex) { GuiOperationFeedback.Report(_toast, Resources.Strings.Mode_Toast_InstallFailed, Resources.Strings.Mode_Toast_InstallFailed_Body, ex); }
    }

    // ── CloudRedirect add-on (bottom panel; usable when either OST or BST is active) ───
    private const string CloudRedirectTitle = "CloudRedirect"; // product name, not localized

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
    private bool _cloudRedirectUnlocked;

    /// <summary>Buttons on the add-on panel are usable only when unlocked (Nightly active) and idle.</summary>
    public bool CanUseCloudRedirect => CloudRedirectUnlocked && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloudRedirectToggleText))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    private bool _cloudRedirectEnabled;

    [ObservableProperty] private bool _cloudRedirectInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    private bool _cloudRedirectUpdateAvailable;

    [ObservableProperty] private string _cloudRedirectStatusText = "";
    [ObservableProperty] private string _cloudRedirectCabinetText = "";

    public string CloudRedirectToggleText => CloudRedirectEnabled
        ? Resources.Strings.Mode_CloudRedirect_Disable
        : Resources.Strings.Mode_CloudRedirect_Enable;
    public bool ShowCloudRedirectUpdate => CloudRedirectUnlocked && CloudRedirectUpdateAvailable;
    public bool ShowCloudRedirectManage => CloudRedirectUnlocked && CloudRedirectEnabled;

    /// <summary>Refresh the add-on panel state. Reads dll/toml from disk (cheap); checks GitHub for an
    /// update only when unlocked (Nightly active), respecting forceRefresh.</summary>
    private async Task RefreshCloudRedirectAsync(bool forceRefresh, CloudRedirectAddonState? state = null)
    {
        CloudRedirectUnlocked = _unlocker.SelectedMode is UnlockerMode.Ost or UnlockerMode.Bst;
        var s = state ?? await _operations.ReadCloudAsync(forceRefresh);
        CloudRedirectInstalled = s.Installed;
        CloudRedirectEnabled = s.Enabled;
        CloudRedirectUpdateAvailable = s.UpdateAvailable;

        CloudRedirectStatusText = !CloudRedirectUnlocked ? Resources.Strings.Mode_CloudRedirect_Locked
            : !s.Installed ? Resources.Strings.Mode_CloudRedirect_Status_NotInstalled
            : s.UpdateAvailable ? Resources.Strings.Mode_CloudRedirect_Status_UpdateAvailable
            : s.Enabled ? Resources.Strings.Mode_CloudRedirect_Status_Enabled
            : Resources.Strings.Mode_CloudRedirect_Status_Disabled;
        try
        {
            var cabinet = new CloudRedirectCabinetConfig().Status();
            CloudRedirectCabinetText = cabinet.Configured && cabinet.Server is { } server
                ? string.Format(Resources.Strings.Mode_CloudRedirect_Cabinet, new Uri(server).Host)
                : _cabinet?.Status.Cloud is { Connected: true, ServerHost: { } host }
                    ? string.Format(Resources.Strings.Mode_CloudRedirect_CabinetReady, host)
                    : Resources.Strings.Mode_CloudRedirect_ConnectCabinet;
        }
        catch { CloudRedirectCabinetText = Resources.Strings.CabinetCloud_ConfigError; }
    }

    /// <summary>Enable/disable the add-on (edits opensteamtool.toml; first enable also downloads the dll).
    /// Lightweight, no Steam close; the change applies on the next Steam launch.</summary>
    [RelayCommand]
    private async Task ToggleCloudRedirect()
    {
        try
        {
        if (IsBusy || !CloudRedirectUnlocked) return;
        bool enabling = !CloudRedirectEnabled;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = await _operations.UpdateCloudAsync(enabling, false, prog);

            if (result.CabinetConfigFailed)
                _toast.Show(CloudRedirectTitle, Resources.Strings.CabinetCloud_ConfigError, error: true);
            if (result.Success)
                _toast.Show(CloudRedirectTitle, enabling
                    ? Resources.Strings.Mode_CloudRedirect_Toast_Enabled
                    : Resources.Strings.Mode_CloudRedirect_Toast_Disabled);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
            }
        catch (Exception ex) { GuiOperationFeedback.Report(_toast, Resources.Strings.Mode_Toast_InstallFailed, Resources.Strings.Mode_Toast_InstallFailed_Body, ex); }
    }

    /// <summary>Cabinet's cloud-saves offer, after the user agreed: enable the add-on (first enable
    /// downloads the verified dll) and point it at the connected Cabinet server.</summary>
    internal async Task EnableCloudRedirectForCabinetAsync()
    {
        try
        {
            var result = await _operations.EnableCloudForCabinetAsync();
            if (!result.Success) _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);
            else if (result.CabinetConfigFailed) _toast.Show(CloudRedirectTitle, Resources.Strings.CabinetCloud_ConfigError, error: true);
        }
        catch { _toast.Show(CloudRedirectTitle, Resources.Strings.CabinetCloud_ConfigError, error: true); }
        await LoadAsync();
    }

    /// <summary>Replace cloud_redirect.dll with the latest. Reports "close Steam" if it's locked.</summary>
    [RelayCommand]
    private async Task UpdateCloudRedirect()
    {
        try
        {
        if (IsBusy || !CloudRedirectUnlocked) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = await _operations.UpdateCloudAsync(null, true, prog);
            if (result.Success)
                _toast.Show(CloudRedirectTitle, Resources.Strings.Mode_CloudRedirect_Toast_Updated);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
            }
        catch (Exception ex) { GuiOperationFeedback.Report(_toast, Resources.Strings.Mode_Toast_InstallFailed, Resources.Strings.Mode_Toast_InstallFailed_Body, ex); }
    }

    /// <summary>True if a hidden mode should be shown: its reveal-file exists, or it's the active mode.</summary>
    private bool IsModeVisible(ModeDefinition def)
    {
        if (def.HiddenUnlessFile is null) return true;          // always-visible mode
        if (_unlocker.SelectedMode == def.Mode) return true;    // active → keep visible even if file gone
        string? root = _steam.EffectivePath;
        return root is not null && File.Exists(Path.Combine(root, def.HiddenUnlessFile));
    }

    /// <summary>Rebuild the visible card list (hidden modes appear only when revealed). Preserves
    /// existing cards so their bound state isn't reset; adds/removes as visibility changes.</summary>
    private void SyncCards()
    {
        var visible = _unlocker.Modes
            .Where(IsModeVisible)
            .ToList();

        // Remove cards no longer visible.
        for (int i = Cards.Count - 1; i >= 0; i--)
            if (!visible.Any(d => d.Mode == Cards[i].Mode))
                Cards.RemoveAt(i);

        // Add newly-visible cards in definition order.
        foreach (var def in visible)
            if (!Cards.Any(c => c.Mode == def.Mode))
            {
                int idx = visible.IndexOf(def);
                idx = Math.Min(idx, Cards.Count);
                Cards.Insert(idx, new ModeCardViewModel(def.Mode, def.DisplayName, def.Description));
            }
    }

    /// <summary>
    /// Page open / refresh. Only the ACTIVE mode is checked against GitHub (inactive cards just show
    /// "Switch to this": switching re-fetches anyway, so pinging for them is wasted, and their hash
    /// check would be misleading since modes share filenames). Active check is cached briefly.
    /// </summary>

    public async Task LoadAsync(bool forceRefresh = false)
    {
        try
        {
        var installation = new InstallationBinding(_steam, _steam.EffectivePath);
        var snapshot = await _operations.ReadAsync(forceRefresh, adoptDetection: true);
        if (!installation.MatchesSelection) return;
        SyncCards();
        foreach (var card in Cards)
            if (snapshot.States.FirstOrDefault(s => s.Mode == card.Mode) is { } state) Apply(card, state);

        // Bottom CloudRedirect add-on panel (locked unless Nightly BST is the active mode).
        await RefreshCloudRedirectAsync(forceRefresh, snapshot.Cloud);
            }
        catch (Exception ex) { GuiOperationFeedback.Report(_toast, Resources.Strings.Mode_Toast_InstallFailed, Resources.Strings.Mode_Toast_InstallFailed_Body, ex); }
    }

    private DateTime _lastCheck;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        // 30s cooldown: a forced refresh hits GitHub, and the unauthenticated API allows only 60/hr.
        if (IsBusy || DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(30)) return;
        _lastCheck = DateTime.UtcNow;
        await LoadAsync(forceRefresh: true);
    }

    private void Apply(ModeCardViewModel card, ModeState s)
    {
        // Only the ACTIVE mode shows real install/update status. Inactive modes share filenames with
        // the active one (different contents), so their hash check is meaningless. Show them simply
        // as the switch target instead of a misleading "Update available".
        if (s.Status == ModeStatus.UserManaged)
            // Custom: there's nothing to be up to date WITH, so the status never varies and the only
            // action is switching to it. Checked before IsActive so it never reads "Not active".
            card.StatusText = Resources.Strings.Mode_UserManaged;
        else if (s.Status == ModeStatus.Unknown)
            card.StatusText = Resources.Strings.Mode_StatusUnavailable;
        else if (!s.IsActive)
            card.StatusText = Resources.Strings.Mode_NotActive;
        else
            card.StatusText = s.Status switch
            {
                ModeStatus.NotInstalled => Resources.Strings.Mode_NotInstalled,
                ModeStatus.UpToDate => Resources.Strings.Mode_UpToDate,
                ModeStatus.UpdateAvailable => Resources.Strings.Mode_UpdateAvailable,
                _ => Resources.Strings.Mode_StatusUnavailable,
            };

        card.ButtonText = (s.IsActive, s.Status) switch
        {
            // Never rendered: ShowActionButton hides the button in this exact case. Mapped anyway so
            // a regression in that binding surfaces a harmless "Switch to this" rather than falling
            // through to "Install" on a mode that installs nothing.
            (true, ModeStatus.UserManaged) => Resources.Strings.Mode_Btn_Switch,
            (true, ModeStatus.UpToDate) => Resources.Strings.Mode_Btn_Reinstall,
            (true, ModeStatus.UpdateAvailable) => Resources.Strings.Mode_Btn_Update,
            (true, _) => Resources.Strings.Mode_Btn_Install,
            (false, _) => Resources.Strings.Mode_Btn_Switch,
        };
        card.IsActive = s.IsActive;
    }

    // ── Install with confirmation ────────────────────────────────────

    /// <summary>Card button → ask the user to confirm (Steam will be closed) before doing anything.</summary>
    [RelayCommand]
    private void Install(ModeCardViewModel card)
    {
        if (IsBusy) return;
        _pendingCard = card;
        ConfirmTitle = card.IsActive
            ? string.Format(Resources.Strings.Mode_Confirm_Reinstall, card.Title)
            : string.Format(Resources.Strings.Mode_Confirm_Switch, card.Title);
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        _pendingCard = null;
    }

    [RelayCommand]
    private async Task ConfirmInstall()
    {
        IsConfirming = false;
        var card = _pendingCard;
        _pendingCard = null;
        if (card is null) return;

        await RunInstall(card.Mode);
    }

    private async Task RunInstall(UnlockerMode mode)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var (result, started) = await _operations.InstallAsync(mode, prog);

            if (result.Success)
            {
                _toast.Show(Resources.Strings.Mode_Toast_Updated, started
                    ? string.Format(Resources.Strings.Mode_Toast_Updated_Restarting, mode)
                    : string.Format(Resources.Strings.Mode_Toast_Updated_Start, mode));
            }
            else
            {
                // Install failed: bring Steam back up anyway so the user isn't left without it.
                _toast.Show(Resources.Strings.Mode_Toast_InstallFailed, result.Error ?? Resources.Strings.Mode_Toast_InstallFailed_Body, error: true);
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _toast.Show(Resources.Strings.Mode_Toast_InstallFailed, ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }
}
