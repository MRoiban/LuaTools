using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.GameTools;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// The Achievements, Stats and Playtime tabs of the Tools page for the game picked on the left.
/// Edits are staged in the rows and written by Save. Anything that needs Steam closed goes through the
/// in-page confirm overlay first; declining writes nothing.
/// </summary>
public partial class GameToolsViewModel : ObservableObject
{
    private readonly GameToolsService _service;
    private readonly AchievementIconCache _icons;
    private readonly ToastService _toast;
    private readonly ILogger<GameToolsViewModel>? _log;
    private readonly HashSet<long> _schemaRequested = [];
    private CancellationTokenSource? _iconLoad;
    private long _loadToken;
    private long? _loadedApp;
    private int? _originalMinutes;
    private GameStatsSnapshot? _snapshot;
    private Func<Task>? _pendingConfirm;

    public GameToolsViewModel(GameToolsService service, AchievementIconCache icons, ToastService toast, ILogger<GameToolsViewModel>? log = null)
    {
        _service = service;
        _icons = icons;
        _toast = toast;
        _log = log;
        AchievementsView = CollectionViewSource.GetDefaultView(Achievements);
        AchievementsView.Filter = o => o is AchievementRowViewModel row && row.Matches(AchievementSearchText);
    }

    public ObservableCollection<AchievementRowViewModel> Achievements { get; } = [];
    public ICollectionView AchievementsView { get; }
    public ObservableCollection<StatRowViewModel> Stats { get; } = [];

    // ── Game and visibility ─────────────────────────────────────────

    public long? AppId { get; private set; }

    /// <summary>True while one of this view model's tabs is shown. Loading waits until then, so browsing
    /// depots never reads stats files or downloads icons.</summary>
    [ObservableProperty] private bool _isVisible;
    partial void OnIsVisibleChanged(bool value)
    {
        if (value && AppId is { } app && _loadedApp != app) _ = LoadAsync();
    }

    /// <summary>Called by the page when the selected game changes.</summary>
    public void SetGame(long? appId)
    {
        if (AppId == appId) return;
        AppId = appId;
        AchievementSearchText = "";
        CancelConfirm();
        Clear();
        if (IsVisible && appId is not null) _ = LoadAsync();
    }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _steamRunning;
    [ObservableProperty] private string? _achievementsMessage;
    [ObservableProperty] private string? _statsMessage;
    [ObservableProperty] private string? _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand), nameof(UnlockAllCommand), nameof(LockAllCommand), nameof(SavePlaytimeCommand))]
    private bool _isSaving;

    /// <summary>Toggles, stat boxes and the game list are locked while a save runs, so nothing changes under it.</summary>
    public bool IsEditable => !IsSaving;

    [ObservableProperty] private string _achievementSearchText = "";
    partial void OnAchievementSearchTextChanged(string value)
    {
        AchievementsView.Refresh();
        OnPropertyChanged(nameof(HasNoMatches));
    }

    public bool HasAchievements => Achievements.Count > 0;
    public bool HasStats => Stats.Count > 0;
    public bool HasNoMatches => HasAchievements && !Achievements.Any(a => a.Matches(AchievementSearchText));
    public bool HasChanges => Achievements.Any(a => a.IsDirty) || Stats.Any(s => s.IsDirty);
    private bool LocksPending => Achievements.Any(a => a.IsDirty && !a.Unlocked);

    /// <summary>Cabinet would add a locked achievement back at its next sync.</summary>
    public bool ShowCabinetNote => _snapshot?.CabinetSyncs == true && LocksPending;

    /// <summary>A lock in a CloudRedirect game with Steam running will ask to close Steam.</summary>
    public bool ShowCloudRedirectNote => _snapshot?.CloudRedirectManaged == true && LocksPending;

    /// <summary>CloudRedirect would overwrite stats written with Steam closed, so they are only saved live.</summary>
    public bool ShowCloudRedirectStatsNote => _snapshot?.CloudRedirectManaged == true && HasStats;

    public string SteamStateText => SteamRunning ? Resources.Strings.Tools_SteamRunning : Resources.Strings.Tools_SteamClosed;
    partial void OnSteamRunningChanged(bool value) => OnPropertyChanged(nameof(SteamStateText));

    [RelayCommand]
    public Task Reload() => AppId is null || IsSaving ? Task.CompletedTask : LoadAsync();

    private void Clear()
    {
        _iconLoad?.Cancel();
        foreach (var a in Achievements) a.PropertyChanged -= OnRowChanged;
        foreach (var s in Stats) s.PropertyChanged -= OnRowChanged;
        Achievements.Clear();
        Stats.Clear();
        _snapshot = null;
        _loadedApp = null;
        _originalMinutes = null;
        AchievementsMessage = StatsMessage = Summary = PlaytimeMessage = null;
        PlaytimeHours = "";
        RowsChanged();
    }

    private async Task LoadAsync()
    {
        if (AppId is not { } app) return;
        long token = ++_loadToken;
        IsLoading = true;
        try
        {
            var (running, snapshot, minutes) = await Task.Run(() => (_service.IsSteamRunning(), _service.Load(app), _service.GetPlaytimeMinutes(app)));
            if (token != _loadToken) return;
            SteamRunning = running;
            Show(snapshot);
            ShowPlaytime(minutes);
            _loadedApp = app;

            // Steam has no achievement list yet: ask it once per game per session (only while it runs).
            if (!snapshot.HasSchema && snapshot.AccountId is not null && running && _schemaRequested.Add(app))
            {
                AchievementsMessage = StatsMessage = Resources.Strings.Tools_FetchingSchema;
                bool fetched = await _service.FetchSchemaAsync(app);
                if (token != _loadToken) return;
                if (fetched) Show(await Task.Run(() => _service.Load(app)));
                else Show(snapshot);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Tools: loading app {AppId} failed", app);
            if (token == _loadToken) AchievementsMessage = StatsMessage = string.Format(Resources.Strings.Tools_SaveFailed, ex.Message);
        }
        finally { if (token == _loadToken) IsLoading = false; }
    }

    private void Show(GameStatsSnapshot snapshot)
    {
        _iconLoad?.Cancel();
        foreach (var a in Achievements) a.PropertyChanged -= OnRowChanged;
        foreach (var s in Stats) s.PropertyChanged -= OnRowChanged;
        Achievements.Clear();
        Stats.Clear();
        _snapshot = snapshot;

        string? NoneMessage(string empty) =>
            snapshot.AccountId is null ? Resources.Strings.Tools_NoAccount
            : !snapshot.HasSchema ? Resources.Strings.Tools_NoSchema
            : empty;
        AchievementsMessage = snapshot.Achievements.Count == 0 ? NoneMessage(Resources.Strings.Tools_NoAchievements) : null;
        StatsMessage = snapshot.Stats.Count == 0 ? NoneMessage(Resources.Strings.Tools_NoStats) : null;

        foreach (var info in snapshot.Achievements)
        {
            var row = new AchievementRowViewModel(info);
            row.PropertyChanged += OnRowChanged;
            Achievements.Add(row);
        }
        foreach (var info in snapshot.Stats)
        {
            var row = new StatRowViewModel(info);
            row.PropertyChanged += OnRowChanged;
            Stats.Add(row);
        }
        RowsChanged();
        _ = LoadIconsAsync(Achievements.ToList());
    }

    private async Task LoadIconsAsync(IReadOnlyList<AchievementRowViewModel> rows)
    {
        var cts = _iconLoad = new CancellationTokenSource();
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                row.GrayIcon = await _icons.GetAsync(row.Info.IconGrayUrl, cts.Token);
                row.ColorIcon = await _icons.GetAsync(row.Info.IconUrl, cts.Token);
            }));
        }
        catch (OperationCanceledException) { }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AchievementRowViewModel.IsDirty) or nameof(StatRowViewModel.IsValid)) RowsChanged();
    }

    private void RowsChanged()
    {
        Summary = Achievements.Count == 0 ? null
            : string.Format(Resources.Strings.Tools_Summary, Achievements.Count(a => a.Unlocked), Achievements.Count);
        OnPropertyChanged(nameof(HasAchievements));
        OnPropertyChanged(nameof(HasStats));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ShowCabinetNote));
        OnPropertyChanged(nameof(ShowCloudRedirectNote));
        OnPropertyChanged(nameof(ShowCloudRedirectStatsNote));
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        UnlockAllCommand.NotifyCanExecuteChanged();
        LockAllCommand.NotifyCanExecuteChanged();
    }

    // ── Achievements and stats ──────────────────────────────────────

    private bool CanBulk() => !IsSaving && Achievements.Count > 0;

    /// <summary>Stage every toggleable achievement the search shows.</summary>
    [RelayCommand(CanExecute = nameof(CanBulk))]
    private void UnlockAll() { foreach (var row in BulkTargets()) row.Unlocked = true; }

    [RelayCommand(CanExecute = nameof(CanBulk))]
    private void LockAll() { foreach (var row in BulkTargets()) row.Unlocked = false; }

    private IEnumerable<AchievementRowViewModel> BulkTargets() =>
        Achievements.Where(r => r.CanToggle && r.Matches(AchievementSearchText)).ToList();

    private bool CanSave() => !IsSaving && HasChanges && Stats.All(s => s.IsReadOnly || s.IsValid);
    private bool CanDiscard() => !IsSaving && HasChanges;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard() { if (_snapshot is { } s) Show(s); }

    private GameStatsEdit BuildEdit() => new(
        Achievements.Where(a => a.IsDirty && a.CanToggle).ToDictionary(a => a.Info.ApiName, a => a.Unlocked, StringComparer.Ordinal),
        Stats.Where(s => s.IsDirty && !s.IsReadOnly && s.Parsed.HasValue).ToDictionary(s => s.Info.ApiName, s => s.Parsed!.Value, StringComparer.Ordinal));

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (AppId is not { } app) return;
        var edit = BuildEdit();
        if (edit.IsEmpty) return;
        IsSaving = true;
        try { await HandleSaveAsync(app, edit, await Task.Run(() => _service.SaveAsync(app, edit))); }
        catch (Exception ex) { Failed(ex.Message, ex); }
        finally { IsSaving = false; }
    }

    private async Task HandleSaveAsync(long app, GameStatsEdit edit, StatsSaveResult result)
    {
        switch (result.Outcome)
        {
            case StatsSaveOutcome.SavedLive:
                // Steam may write its cache file later; record what it accepted instead of re-reading it. Only
                // the submitted rows, and only while that game is still shown.
                if (AppId == app)
                {
                    foreach (var a in Achievements)
                        if (edit.Achievements.TryGetValue(a.Info.ApiName, out bool unlocked)) a.CommitSaved(unlocked);
                    foreach (var s in Stats)
                        if (edit.Stats.TryGetValue(s.Info.ApiName, out double value)) s.CommitSaved(value);
                    RowsChanged();
                }
                _toast.Show(Resources.Strings.Tools_Title, Resources.Strings.Tools_Saved_Live);
                break;
            case StatsSaveOutcome.SavedToFile:
                _toast.Show(Resources.Strings.Tools_Title, Resources.Strings.Tools_Saved_File);
                if (AppId == app) await LoadAsync();
                break;
            case StatsSaveOutcome.NeedsSteamClosed when AppId != app:
                // Another game is shown now: never raise a close-Steam prompt over it.
                Failed(Describe(GameToolsService.ErrSteamRunning));
                break;
            case StatsSaveOutcome.LiveRejected when AppId != app:
                Failed(Describe(result.Error));
                break;
            case StatsSaveOutcome.NeedsSteamClosed:
                Ask(Resources.Strings.Tools_Confirm_CloudRedirect_Title, Resources.Strings.Tools_Confirm_CloudRedirect_Body,
                    () => SaveWithSteamClosedAsync(app, edit));
                break;
            case StatsSaveOutcome.LiveRejected:
                Ask(Resources.Strings.Tools_Confirm_LiveRejected_Title,
                    string.Format(Resources.Strings.Tools_Confirm_LiveRejected_Body, Describe(result.Error)),
                    () => SaveWithSteamClosedAsync(app, edit));
                break;
            default:
                Failed(Describe(result.Error));
                break;
        }
    }

    private async Task SaveWithSteamClosedAsync(long app, GameStatsEdit edit)
    {
        IsSaving = true;
        try
        {
            var run = await _service.SaveWithSteamClosedAsync(app, edit);
            if (run.SteamRestartFailed) _toast.Show(Resources.Strings.Tools_Title, Resources.Strings.Tools_SteamRestartFailed, error: true);
            // A closed-Steam write ends in SavedToFile or Failed, so this cannot ask again.
            await HandleSaveAsync(app, edit, run.Result);
        }
        catch (Exception ex) { Failed(ex.Message, ex); }
        finally { IsSaving = false; }
    }

    // ── Playtime ────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaytimeInvalid))]
    [NotifyCanExecuteChangedFor(nameof(SavePlaytimeCommand))]
    private string _playtimeHours = "";

    [ObservableProperty] private string? _playtimeMessage;

    public bool PlaytimeInvalid => PlaytimeHours.Length > 0 && ParseHours(PlaytimeHours) is null;

    private void ShowPlaytime(int? minutes)
    {
        _originalMinutes = minutes;
        PlaytimeHours = ((minutes ?? 0) / 60.0).ToString("0.##", CultureInfo.CurrentCulture);
        PlaytimeMessage = minutes is { } m
            ? string.Format(Resources.Strings.Tools_Playtime_Current, (m / 60.0).ToString("0.##", CultureInfo.CurrentCulture))
            : Resources.Strings.Tools_Playtime_None;
        SavePlaytimeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Hours (decimals allowed, in the user's or the invariant format) to whole minutes.</summary>
    internal static int? ParseHours(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double h)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out h)) return null;
        if (!double.IsFinite(h) || h < 0 || h * 60 > int.MaxValue) return null;
        return (int)Math.Round(h * 60);
    }

    private bool CanSavePlaytime() => !IsSaving && AppId is not null && _loadedApp == AppId
        && ParseHours(PlaytimeHours) is { } m && m != (_originalMinutes ?? 0);

    [RelayCommand(CanExecute = nameof(CanSavePlaytime))]
    private async Task SavePlaytime()
    {
        if (AppId is not { } app || ParseHours(PlaytimeHours) is not { } minutes) return;
        IsSaving = true;
        try
        {
            bool running = await Task.Run(_service.IsSteamRunning);
            if (AppId != app) return; // never prompt over another game
            SteamRunning = running;
            if (running)
            {
                Ask(Resources.Strings.Tools_Confirm_Playtime_Title, Resources.Strings.Tools_Confirm_Playtime_Body, async () =>
                {
                    IsSaving = true;
                    try
                    {
                        var run = await _service.SetPlaytimeWithSteamClosedAsync(app, minutes);
                        if (run.SteamRestartFailed) _toast.Show(Resources.Strings.Tools_Title, Resources.Strings.Tools_SteamRestartFailed, error: true);
                        await PlaytimeDoneAsync(app, run.Result);
                    }
                    finally { IsSaving = false; }
                });
                return;
            }
            await PlaytimeDoneAsync(app, await _service.SetPlaytimeAsync(app, minutes));
        }
        catch (Exception ex) { Failed(ex.Message, ex); }
        finally { IsSaving = false; }
    }

    private async Task PlaytimeDoneAsync(long app, string? error)
    {
        if (error is not null) { Failed(Describe(error)); return; }
        _toast.Show(Resources.Strings.Tools_Title, Resources.Strings.Tools_Saved_File);
        if (AppId == app) await LoadAsync();
    }

    // ── Confirm overlay (Steam has to close) ────────────────────────

    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string? _confirmTitle;
    [ObservableProperty] private string? _confirmBody;

    private void Ask(string title, string body, Func<Task> onAccept)
    {
        ConfirmTitle = title;
        ConfirmBody = body;
        _pendingConfirm = onAccept;
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        _pendingConfirm = null;
    }

    [RelayCommand]
    private async Task AcceptConfirm()
    {
        var action = _pendingConfirm;
        IsConfirming = false;
        _pendingConfirm = null;
        if (action is null) return;
        try { await action(); }
        catch (Exception ex) { Failed(ex.Message, ex); }
    }

    // ── Errors ──────────────────────────────────────────────────────

    private void Failed(string message, Exception? ex = null)
    {
        if (ex is not null) _log?.LogWarning(ex, "Tools: save failed");
        _toast.Show(Resources.Strings.Tools_Title, string.Format(Resources.Strings.Tools_SaveFailed, message), error: true);
    }

    /// <summary>Localize a service or helper error code; unknown text (an exception message) is shown as is.</summary>
    internal static string Describe(string? error)
    {
        if (string.IsNullOrEmpty(error)) return Resources.Strings.Tools_Err_Helper;
        int colon = error.IndexOf(':');
        string code = colon < 0 ? error : error[..colon], detail = colon < 0 ? "" : error[(colon + 1)..].Trim();
        return code switch
        {
            GameToolsService.ErrNoSteam => Resources.Strings.Tools_Err_NoSteam,
            GameToolsService.ErrNoAccount => Resources.Strings.Tools_NoAccount,
            GameToolsService.ErrNoSchema => Resources.Strings.Tools_Err_NoSchema,
            GameToolsService.ErrSteamRunning => Resources.Strings.Tools_Err_SteamRunning,
            GameToolsService.ErrSteamStarted => Resources.Strings.Tools_Err_SteamStarted,
            GameToolsService.ErrSteamStop => Resources.Strings.Tools_Err_SteamStop,
            GameToolsService.ErrNoLocalConfig => Resources.Strings.Tools_Err_NoLocalConfig,
            GameToolsService.ErrNoAppsSection => Resources.Strings.Tools_Err_NoAppsSection,
            GameToolsService.ErrCloudRedirectFile => Resources.Strings.Tools_Err_CloudRedirectFile,
            GameToolsService.ErrSteamStartedPartial => Resources.Strings.Tools_Err_SteamStartedPartial,
            GameToolsService.ErrCloudRedirectStats => Resources.Strings.Tools_Err_CloudRedirectStats,
            GameToolsService.ErrCloudRedirectStatsRejected => string.Format(Resources.Strings.Tools_Err_CloudRedirectStatsRejected, Describe(detail)),
            GameToolsService.ErrCloudRedirectMixed => Resources.Strings.Tools_Err_CloudRedirectMixed,
            "busy" => Resources.Strings.Tools_Err_Busy,
            "steam-closed" => Resources.Strings.Tools_Err_SteamClosed,
            "stats-unavailable" => Resources.Strings.Tools_Err_StatsUnavailable + Suffix(detail),
            "refused" => string.Format(Resources.Strings.Tools_Err_Refused, detail),
            "store-rejected" or "store-failed" => Resources.Strings.Tools_Err_Store + Suffix(detail),
            "store-timeout" or "timeout" or "cancelled" => Resources.Strings.Tools_Err_Timeout,
            "helper-failed" => Resources.Strings.Tools_Err_Helper,
            "exception" => detail.Length > 0 ? detail : Resources.Strings.Tools_Err_Helper,
            _ => error,
        };
        static string Suffix(string d) => d.Length > 0 ? " (" + d + ")" : "";
    }
}
