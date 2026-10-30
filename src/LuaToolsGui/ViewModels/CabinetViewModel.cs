using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Resources;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;

namespace LuaToolsGui.ViewModels;

public sealed partial class CabinetViewModel : ObservableObject, IDisposable
{
    private readonly ICabinetService _service;
    private readonly SettingsService _settings;
    private readonly Func<CabinetGame, Task<ImageSource?>>? _coverLoader;
    private CabinetStatus _status = CabinetStatus.Off;
    [ObservableProperty] private bool _manualSync;
    [ObservableProperty] private string? _error;
    public ObservableCollection<CabinetGameViewModel> Games { get; } = [];
    public CabinetStatus Status => _status;
    public bool IsOff => !_status.Enabled;
    public bool ShowChips => _status.Enabled;
    public bool IsEmpty => _status.Enabled && Games.Count == 0;
    public bool HasGames => _status.Enabled && Games.Count > 0;
    public bool IsSyncing => _status.IsSyncing || ManualSync;
    public bool CanSync => _status.Enabled && !IsSyncing && !_status.StandaloneCabinetRunning;
    public string SyncLabel => IsSyncing ? Strings.Cabinet_Syncing : Strings.Cabinet_SyncNow;
    public string TrackingLabel => _status.Enabled ? Strings.Cabinet_TrackingOn : Strings.Cabinet_TrackingOff;
    public string SteamLabel => _status.SteamRunning ? Strings.Cabinet_SteamOn : Strings.Cabinet_SteamOff;
    public string SteamColor => _status.SteamRunning ? "#4ade80" : "#6b7280";
    public bool IsPlaying => _status.Games.Any(g => g.IsPlaying);
    public string PlayingLabel => string.Format(Strings.Cabinet_Playing, string.Join(", ", _status.Games.Where(g => g.IsPlaying).Select(g => g.Name)));
    public string CloudLabel => _status.Cloud.Connected ? string.Format(Strings.Cabinet_CloudOn, _status.Cloud.UserName ?? _status.Cloud.ServerHost) : Strings.Cabinet_CloudOff;
    public string CloudColor => _status.Cloud.Connected ? "#4ade80" : "#6b7280";
    public bool IsPaused => _status.StandaloneCabinetRunning;

    // Saves go through CloudRedirect; the engine itself never uploads save folders.
    private CabinetCloudSaves _cloudSaves = CabinetCloudSaves.Hidden;
    /// <summary>Set by App: the current CloudRedirect state for the connected Cabinet server.</summary>
    public Func<CabinetCloudSaves>? CloudSavesProvider { get; set; }
    /// <summary>Set by App: ask to turn on CloudRedirect for Cabinet saves.</summary>
    public Func<Task>? RequestEnableCloudSaves { get; set; }
    public bool ShowCloudSaves => _status.Enabled && _status.Cloud.Connected && _cloudSaves != CabinetCloudSaves.Hidden;
    public bool CloudSavesActive => _cloudSaves == CabinetCloudSaves.Active;
    public bool CanEnableCloudSaves => _cloudSaves == CabinetCloudSaves.Off;
    public string CloudSavesLabel => _cloudSaves switch
    {
        CabinetCloudSaves.Active => string.Format(Strings.Cabinet_Saves_Active, _status.Cloud.ServerHost),
        CabinetCloudSaves.Unavailable => Strings.Cabinet_Saves_Unavailable,
        _ => Strings.Cabinet_Saves_Off,
    };
    public string CloudSavesColor => CloudSavesActive ? "#4ade80" : "#fbbf24";
    public void RefreshCloudSaves()
    {
        try { _cloudSaves = _status.Cloud.Connected ? CloudSavesProvider?.Invoke() ?? CabinetCloudSaves.Hidden : CabinetCloudSaves.Hidden; }
        catch { _cloudSaves = CabinetCloudSaves.Hidden; }
        OnPropertyChanged(nameof(ShowCloudSaves)); OnPropertyChanged(nameof(CloudSavesActive)); OnPropertyChanged(nameof(CanEnableCloudSaves));
        OnPropertyChanged(nameof(CloudSavesLabel)); OnPropertyChanged(nameof(CloudSavesColor));
    }
    [RelayCommand]
    private async Task EnableCloudSavesAsync()
    {
        if (RequestEnableCloudSaves is null) return;
        try { await RequestEnableCloudSaves(); }
        finally { RefreshCloudSaves(); }
    }

    public CabinetViewModel(ICabinetService service, SettingsService settings, CoverCache covers, SteamAppInfoCache appInfo)
        : this(service, settings, async game =>
        {
            if (!long.TryParse(game.AppId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !CatalogDataPolicy.IsId(id)) return null;
            var path = await LuaTileViewModel.ResolveCoverFileAsync(id, appInfo, covers).ConfigureAwait(false);
            return path is null ? null : SafeImageLoader.LoadLocal(path, 184);
        }) { }

    internal CabinetViewModel(ICabinetService service, SettingsService settings, Func<CabinetGame, Task<ImageSource?>>? coverLoader = null)
    {
        _service = service; _settings = settings; _coverLoader = coverLoader;
        _service.StatusChanged += StatusChanged;
        ApplyStatus(service.Status);
    }
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }
    private void StatusChanged(object? sender, CabinetStatus status) => OnUi(() => ApplyStatus(status));
    private void ApplyStatus(CabinetStatus status)
    {
        bool cloudChanged = status.Cloud != _status.Cloud;
        _status = status;
        if (cloudChanged) RefreshCloudSaves();
        var previous = Games.ToDictionary(g => g.Game.AppId, g => g.Cover);
        Games.Clear();
        foreach (var game in CabinetProjection.OrderGames(status.Games))
        {
            var row = new CabinetGameViewModel(game);
            if (previous.TryGetValue(game.AppId, out var cover)) row.Cover = cover;
            Games.Add(row);
            if (row.Cover is null && _coverLoader is not null && status.Enabled) _ = LoadCoverAsync(row);
        }
        RefreshState();
    }
    private async Task LoadCoverAsync(CabinetGameViewModel row)
    {
        try { var cover = await _coverLoader!(row.Game).ConfigureAwait(false); OnUi(() => row.Cover = cover); }
        catch { /* Missing artwork retains the placeholder. */ }
    }
    private void RefreshState()
    {
        OnPropertyChanged(string.Empty);
        SyncNowCommand.NotifyCanExecuteChanged();
    }
    partial void OnManualSyncChanged(bool value) => RefreshState();
    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncNowAsync()
    {
        ManualSync = true; Error = null;
        try { await AccountCabinetOperations.SyncAsync(_service); }
        catch (Exception ex) { Error = Strings.Cabinet_SyncFailed; AppEventLog.Shared.Write(Microsoft.Extensions.Logging.LogLevel.Warning, "Cabinet", Error, ex); }
        finally { ManualSync = false; }
    }
    [RelayCommand]
    private void TurnOn() { _settings.CabinetEnabled = true; _service.SettingsChanged(); }
    public void Dispose() => _service.StatusChanged -= StatusChanged;
}

public sealed partial class CabinetGameViewModel(CabinetGame game) : ObservableObject
{
    public CabinetGame Game { get; } = game;
    [ObservableProperty] private ImageSource? _cover;
    public string Name => Game.Name;
    public string OpenLabel => string.Format(Strings.Cabinet_OpenGame, Name);
    public bool HasAchievements => Game.HasAchievementList && Game.AchievementsUnlocked.HasValue && Game.AchievementsTotal.HasValue;
    public string Unlocked => HasAchievements ? Game.AchievementsUnlocked!.Value.ToString(CultureInfo.CurrentCulture) : Strings.Cabinet_NoAchievements;
    public string Total => HasAchievements ? $" / {Game.AchievementsTotal}" : "";
    public double Progress => HasAchievements && Game.AchievementsTotal > 0 ? Math.Clamp(100d * Game.AchievementsUnlocked!.Value / Game.AchievementsTotal.Value, 0, 100) : 0;
    public string Hours => string.Format(CultureInfo.CurrentCulture, Strings.Cabinet_Hours, Game.HoursPlayed);
    public void RefreshLastSync() => OnPropertyChanged(nameof(LastSync));
    public string LastSync => FormatLastSync(Game.LastSynced, DateTimeOffset.Now);
    public bool HasPlayingBadge => Game.IsPlaying;
    public string PlayingBadge => Game.LivePlaytime ? Strings.Cabinet_Live : Strings.Cabinet_PlayingNow;
    public bool HasPendingBadge => Game.PendingPlaytimeMinutes > 0;
    public string PendingBadge => string.Format(Strings.Cabinet_Pending, Game.PendingPlaytimeMinutes);
    public bool HasSchemaBadge => !Game.HasAchievementList;
    internal static string FormatLastSync(DateTimeOffset? last, DateTimeOffset now) => CabinetProjection.FormatLastSync(last, now);
    [RelayCommand]
    private void OpenGame()
    {
        if (uint.TryParse(Game.AppId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            Process.Start(new ProcessStartInfo($"steam://nav/games/details/{id}") { UseShellExecute = true });
    }
}
