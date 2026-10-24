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
    private readonly ICabinetSaves? _saves;
    private readonly ICabinetSaveDialogs _dialogs;
    [ObservableProperty] private string? _saveError;
    [ObservableProperty] private bool _saveBusy;
    public ObservableCollection<CabinetSaveGameViewModel> SaveGames { get; } = [];
    public bool ShowSaves => _status.Enabled;
    public bool HasSaveGames => _status.Enabled && SaveGames.Count > 0;
    public bool NoSaveGames => _status.Enabled && SaveGames.Count == 0;
    public string SaveAttribution => string.Format(Strings.CabinetSaves_Attribution,
        _status.Saves?.Select(g => g.ManifestVersion).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? Strings.CabinetSaves_ManifestUnavailable);
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

    public CabinetViewModel(ICabinetService service, SettingsService settings, CoverCache covers, SteamAppInfoCache appInfo, ICabinetSaves saves)
        : this(service, settings, async game =>
        {
            if (!long.TryParse(game.AppId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !CatalogDataPolicy.IsId(id)) return null;
            var path = await LuaTileViewModel.ResolveCoverFileAsync(id, appInfo, covers).ConfigureAwait(false);
            return path is null ? null : SafeImageLoader.LoadLocal(path, 184);
        }, saves) { }

    internal CabinetViewModel(ICabinetService service, SettingsService settings, Func<CabinetGame, Task<ImageSource?>>? coverLoader = null,
        ICabinetSaves? saves = null, ICabinetSaveDialogs? dialogs = null)
    {
        _service = service; _settings = settings; _coverLoader = coverLoader; _saves = saves; _dialogs = dialogs ?? new WpfCabinetSaveDialogs();
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
        _status = status;
        var previous = Games.ToDictionary(g => g.Game.AppId, g => g.Cover);
        Games.Clear();
        foreach (var game in status.Games.OrderByDescending(g => g.IsPlaying).ThenByDescending(g => g.LastSynced).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new CabinetGameViewModel(game);
            if (previous.TryGetValue(game.AppId, out var cover)) row.Cover = cover;
            Games.Add(row);
            if (row.Cover is null && _coverLoader is not null && status.Enabled) _ = LoadCoverAsync(row);
        }
        SaveGames.Clear();
        foreach (var save in status.Saves ?? Array.Empty<CabinetSaveGame>())
            SaveGames.Add(new CabinetSaveGameViewModel(save, this));
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
        try { await _service.SyncNowAsync(); }
        catch (Exception) { Error = Strings.Cabinet_SyncFailed; }
        finally { ManualSync = false; }
    }
    [RelayCommand]
    private void TurnOn() { _settings.CabinetEnabled = true; _service.SettingsChanged(); }

    // ── Cloud saves ──
    private async Task RunSaveAction(Func<ICabinetSaves, Task> action)
    {
        if (_saves is null) return;
        SaveBusy = true; SaveError = null;
        try { await action(_saves); }
        catch (Exception ex) { SaveError = string.Format(Strings.CabinetSaves_ActionFailed, ex.Message); }
        finally { SaveBusy = false; }
    }
    [RelayCommand]
    private Task RescanSavesAsync() => RunSaveAction(s => s.RescanAsync());

    internal Task EditLocationAsync(CabinetSaveGameViewModel row) => RunSaveAction(async s =>
    {
        string? folder = _dialogs.PickFolder(row.Game.Roots.FirstOrDefault(r => r.Path is not null && r.Files > 0)?.Path);
        if (folder is null) return;
        bool? replace = _dialogs.AskReplace(folder);
        if (replace is null) return;
        var folders = replace.Value ? new List<string>() : row.OverrideFolders.ToList();
        if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
        await s.SetOverrideAsync(row.Game.AppId, replace.Value ? "replace" : "supplement", folders, null);
    });
    internal Task ResetLocationAsync(CabinetSaveGameViewModel row) => RunSaveAction(s => s.ResetOverrideAsync(row.Game.AppId));
    internal Task ToggleSyncAsync(CabinetSaveGameViewModel row) => RunSaveAction(s =>
        s.SetOverrideAsync(row.Game.AppId, row.Game.OverrideMode ?? "supplement", row.OverrideFolders.ToList(), row.Game.State == CabinetSaveState.Off));
    internal Task RestoreAsync(CabinetSaveGameViewModel row) => RunSaveAction(async s =>
    {
        var preview = await s.PreviewRestoreAsync(row.Game.AppId);
        if (!preview.CanApply)
        {
            _dialogs.Inform(Strings.CabinetSaves_RestoreTitle, string.Format(Strings.CabinetSaves_PlanBlocked, string.Join("\n", preview.Problems)));
            return;
        }
        if (!_dialogs.Confirm(Strings.CabinetSaves_RestoreTitle, string.Format(Strings.CabinetSaves_RestoreConfirm, FormatPreview(preview)))) return;
        await s.RestoreAsync(row.Game.AppId);
    });
    internal Task KeepLocalAsync(CabinetSaveGameViewModel row) => RunSaveAction(async s =>
    {
        if (_dialogs.Confirm(Strings.CabinetSaves_KeepLocal, string.Format(Strings.CabinetSaves_KeepLocalConfirm, row.Name))) await s.KeepLocalAsync(row.Game.AppId);
    });
    internal Task DeleteCloudAsync(CabinetSaveGameViewModel row) => RunSaveAction(async s =>
    {
        if (_dialogs.Confirm(Strings.CabinetSaves_DeleteCloud, string.Format(Strings.CabinetSaves_DeleteConfirm, row.Name))) await s.DeleteCloudAsync(row.Game.AppId);
    });
    internal static string FormatPreview(CabinetRestorePreview preview)
    {
        var lines = new List<string> { preview.BackupPath is null ? Strings.CabinetSaves_PlanNoBackup : string.Format(Strings.CabinetSaves_PlanBackup, preview.BackupPath) };
        foreach (var item in preview.Items)
            lines.Add(string.Format(item.Action switch
            {
                CabinetRestoreAction.Create => Strings.CabinetSaves_PlanCreate,
                CabinetRestoreAction.Replace => Strings.CabinetSaves_PlanReplace,
                _ => Strings.CabinetSaves_PlanDelete,
            }, item.Destination));
        return string.Join("\n", lines);
    }
    public void Dispose() => _service.StatusChanged -= StatusChanged;
}

/// <summary>Dialogs used by the cloud-saves actions; replaced in tests.</summary>
public interface ICabinetSaveDialogs
{
    string? PickFolder(string? initial);
    /// <summary>true = use only this folder, false = add it, null = cancel.</summary>
    bool? AskReplace(string folder);
    bool Confirm(string title, string message);
    void Inform(string title, string message);
}

internal sealed class WpfCabinetSaveDialogs : ICabinetSaveDialogs
{
    public string? PickFolder(string? initial)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.CabinetSaves_ReplaceTitle };
        if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    public bool? AskReplace(string folder) => MessageBox.Show(string.Format(Strings.CabinetSaves_ReplaceQuestion, folder), Strings.CabinetSaves_ReplaceTitle,
        MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch { MessageBoxResult.Yes => true, MessageBoxResult.No => false, _ => null };
    public bool Confirm(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    public void Inform(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}

public sealed partial class CabinetSaveGameViewModel(CabinetSaveGame game, CabinetViewModel owner) : ObservableObject
{
    public CabinetSaveGame Game { get; } = game;
    public string Name => Game.Name;
    public string StatusText => Game.State switch
    {
        CabinetSaveState.Synced => Strings.CabinetSaves_State_Synced,
        CabinetSaveState.Pending => Strings.CabinetSaves_State_Pending,
        CabinetSaveState.Conflict => Strings.CabinetSaves_State_Conflict,
        CabinetSaveState.Error => string.Format(Strings.CabinetSaves_State_Error, Game.Error ?? "?"),
        CabinetSaveState.NoFiles => Strings.CabinetSaves_State_NoFiles,
        CabinetSaveState.Ambiguous => Strings.CabinetSaves_State_Ambiguous,
        CabinetSaveState.LocalMissing => Strings.CabinetSaves_State_LocalMissing,
        CabinetSaveState.CloudDeleted => Strings.CabinetSaves_State_CloudDeleted,
        CabinetSaveState.Deferred => Strings.CabinetSaves_State_Deferred,
        CabinetSaveState.NotConnected => Strings.CabinetSaves_State_NotConnected,
        CabinetSaveState.Off => Strings.CabinetSaves_State_Off,
        CabinetSaveState.CloudOnly => Strings.CabinetSaves_State_CloudOnly,
        _ => Strings.CabinetSaves_State_NotFound,
    };
    public string StatusColor => Game.State switch
    {
        CabinetSaveState.Synced => "#4ade80",
        CabinetSaveState.Pending or CabinetSaveState.Deferred or CabinetSaveState.CloudOnly => "#60a5fa",
        CabinetSaveState.Conflict or CabinetSaveState.Error or CabinetSaveState.LocalMissing or CabinetSaveState.Ambiguous => "#fbbf24",
        _ => "#9ca3af",
    };
    public IEnumerable<string> OverrideFolders => Game.Roots.Where(r => r.Source == "override" && r.Path is not null).Select(r => r.Path!);
    public string Locations => string.Join("\n", Game.Roots.Where(r => r.Path is not null || r.Note is not null).Select(r =>
        string.Format(Strings.CabinetSaves_Location, Source(r.Source), r.Path ?? "—", RootState(r))));
    public bool HasLocations => Game.Roots.Count > 0;
    public string Totals => string.Format(Strings.CabinetSaves_Totals, Game.Files, CabinetFormat.Bytes(Game.Bytes));
    public bool HasCloud => Game.CloudFiles > 0;
    public string CloudTotals => string.Format(Strings.CabinetSaves_CloudTotals, Game.CloudFiles, CabinetFormat.Bytes(Game.CloudBytes));
    public string LastUpload => string.Format(Strings.CabinetSaves_LastUpload, CabinetGameViewModel.FormatLastSync(Game.LastUpload, DateTimeOffset.Now));
    public string LastDownload => string.Format(Strings.CabinetSaves_LastDownload, CabinetGameViewModel.FormatLastSync(Game.LastDownload, DateTimeOffset.Now));
    public string? OverrideText => Game.OverrideMode switch { "replace" => Strings.CabinetSaves_Override_replace, "supplement" => Strings.CabinetSaves_Override_supplement, _ => null };
    public bool HasOverride => Game.OverrideMode is not null || Game.State == CabinetSaveState.Off;
    public string Notes => string.Join("\n", Game.Notes);
    public bool HasNotes => Game.Notes.Count > 0;
    public bool ShowConflictActions => Game.State == CabinetSaveState.Conflict;
    public bool ShowRestore => Game.State != CabinetSaveState.Conflict && (Game.CloudFiles > 0 || Game.State is CabinetSaveState.LocalMissing or CabinetSaveState.CloudOnly);
    public bool ShowDelete => Game.CloudFiles > 0 && Game.State != CabinetSaveState.CloudDeleted;
    public string ToggleLabel => Game.State == CabinetSaveState.Off ? Strings.CabinetSaves_TurnOn : Strings.CabinetSaves_TurnOff;
    private static string Source(string source) => source switch
    {
        "manifest" => Strings.CabinetSaves_Source_manifest,
        "onlinefix" => Strings.CabinetSaves_Source_onlinefix,
        _ => Strings.CabinetSaves_Source_override,
    };
    private static string RootState(CabinetSaveRoot r) => r.State switch
    {
        "found" => string.Format(Strings.CabinetSaves_RootFiles, r.Files, CabinetFormat.Bytes(r.Bytes)),
        "empty" => Strings.CabinetSaves_Root_empty,
        "missing" => Strings.CabinetSaves_Root_missing,
        "unavailable" => Strings.CabinetSaves_Root_unavailable,
        "denied" => Strings.CabinetSaves_Root_denied,
        "tooBroad" => Strings.CabinetSaves_Root_tooBroad,
        "overlapped" => Strings.CabinetSaves_Root_overlapped,
        "ambiguous" => Strings.CabinetSaves_Root_ambiguous,
        _ => Strings.CabinetSaves_Root_unsupported,
    };
    [RelayCommand] private Task EditLocationAsync() => owner.EditLocationAsync(this);
    [RelayCommand] private Task ResetLocationAsync() => owner.ResetLocationAsync(this);
    [RelayCommand] private Task ToggleSyncAsync() => owner.ToggleSyncAsync(this);
    [RelayCommand] private Task RestoreAsync() => owner.RestoreAsync(this);
    [RelayCommand] private Task KeepLocalAsync() => owner.KeepLocalAsync(this);
    [RelayCommand] private Task DeleteCloudAsync() => owner.DeleteCloudAsync(this);
}

internal static class CabinetFormat
{
    internal static string Bytes(long bytes)
    {
        if (bytes < 1024) return string.Format(CultureInfo.CurrentCulture, "{0} B", bytes);
        if (bytes < 1024 * 1024) return string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024d);
        if (bytes < 1024L * 1024 * 1024) return string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / 1024d / 1024d);
        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} GB", bytes / 1024d / 1024d / 1024d);
    }
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
    internal static string FormatLastSync(DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is null) return Strings.Cabinet_Never;
        var elapsed = now - last.Value;
        if (elapsed.TotalMinutes < 1) return Strings.Cabinet_JustNow;
        if (elapsed.TotalHours < 1) return string.Format(Strings.Cabinet_MinutesAgo, (int)elapsed.TotalMinutes);
        var local = last.Value.LocalDateTime;
        if (local.Date == now.LocalDateTime.Date.AddDays(-1)) return string.Format(Strings.Cabinet_Yesterday, local.ToString("HH:mm", CultureInfo.CurrentCulture));
        return local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }
    [RelayCommand]
    private void OpenGame()
    {
        if (uint.TryParse(Game.AppId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            Process.Start(new ProcessStartInfo($"steam://nav/games/details/{id}") { UseShellExecute = true });
    }
}
