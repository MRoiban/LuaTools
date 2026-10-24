namespace LuaToolsGui.Services.Cabinet;

// Shared contract between the Cabinet engine (Services/Cabinet) and its UI (CabinetView, Settings).
// Ported from github.com/MRoiban/cabinet. Change only by agreement of both owners.

/// <summary>Persisted Cabinet switches. Implemented by SettingsService; the engine reads, never writes.</summary>
public interface ICabinetSettings
{
    /// <summary>Master switch: sync OnlineFix (Spacewar) achievements, stats and playtime. Default false.</summary>
    bool CabinetEnabled { get; }
    /// <summary>Hidden helper process connects to Steam as the real game while it runs. Default true.</summary>
    bool CabinetLivePlaytime { get; }
    /// <summary>Ask Steam to download missing achievement lists (schemas). Default true.</summary>
    bool CabinetFetchSchemas { get; }
    /// <summary>Link subscribed Workshop items into OnlineFix games. Default false.</summary>
    bool CabinetWorkshopLinks { get; }
}

public sealed record CabinetGame(
    string AppId,
    string Name,
    int? AchievementsUnlocked,      // null when no achievement list (schema) is available
    int? AchievementsTotal,
    double HoursPlayed,             // Steam-recorded playtime for the real game
    DateTimeOffset? LastSynced,     // null = never
    bool IsPlaying,
    bool LivePlaytime,              // a live helper is crediting Steam right now
    int PendingPlaytimeMinutes,     // fallback minutes waiting for Steam to close
    bool HasAchievementList);

public sealed record CabinetCloudStatus(bool Connected, string? UserName, string? ServerHost, DateTimeOffset? LastSync)
{
    public static readonly CabinetCloudStatus Disconnected = new(false, null, null, null);
}

public sealed record CabinetStatus(
    bool Enabled,
    bool SteamRunning,
    bool IsSyncing,
    bool StandaloneCabinetRunning,  // Cabinet.exe is running: engine pauses to avoid double-crediting
    IReadOnlyList<CabinetGame> Games,
    CabinetCloudStatus Cloud,
    IReadOnlyList<CabinetSaveGame>? Saves = null)  // Cabinet save backups (not CloudRedirect storage)
{
    public static readonly CabinetStatus Off = new(false, false, false, false, Array.Empty<CabinetGame>(), CabinetCloudStatus.Disconnected);
}

public enum CabinetNotificationKind
{
    AchievementsSynced,        // Items: "<game>|<count>" per game
    AlreadyInSync,             // manual sync only
    PlaytimeAdded,             // Items: "<game>|<minutes>"
    AchievementListsFound,     // Count
    WorkshopChanged,           // Items: game names; Count = links created
    Message,                   // Items[0]: Cabinet's own already-localized (en/fr) balloon text
}

/// <summary>Language-neutral event; the UI localizes it (en/fr) and shows a toast/tray balloon.</summary>
public sealed record CabinetNotification(CabinetNotificationKind Kind, IReadOnlyList<string> Items, int Count);

public interface ICabinetService
{
    CabinetStatus Status { get; }
    /// <summary>Raised on any thread; subscribers marshal to the UI thread.</summary>
    event EventHandler<CabinetStatus>? StatusChanged;
    event EventHandler<CabinetNotification>? Notification;
    /// <summary>Full sync now (no-op when disabled or paused). Raises AlreadyInSync when nothing changed.</summary>
    Task SyncNowAsync(CancellationToken cancellationToken = default);
    /// <summary>Re-read ICabinetSettings and start/stop components accordingly.</summary>
    void SettingsChanged();
}

// ── Cabinet save backups (format-2 archives on the Cabinet server; see the cabinet repo's
//    docs/save-sync.md). Separate from CloudRedirect's Steam Cloud file storage. ──

public enum CabinetSaveState
{
    Synced,         // local saves match the newest cloud backup
    Pending,        // local changes waiting for the next upload
    Conflict,       // this PC and the cloud both changed; nothing was overwritten
    Error,          // last attempt failed; retried next round
    NotFound,       // "Save location not found": missing, unavailable or unsupported
    NoFiles,        // "No save files found": the folder exists but is empty
    Ambiguous,      // several folders hold saves for one location
    LocalMissing,   // saves were synced before and are gone locally; cloud kept
    CloudDeleted,   // the cloud backup was deleted on purpose (tombstone)
    Deferred,       // the game is running
    NotConnected,   // found locally, Cabinet Cloud not connected
    Off,            // sync switched off for this game
    CloudOnly,      // cloud backup exists, game not set up for sync here
}

public sealed record CabinetSaveRoot(string Id, string Source, string? Path, string State, int Files, long Bytes, string? Note);

public sealed record CabinetSaveGame(
    string AppId,
    string Name,
    CabinetSaveState State,
    string? Discovery,                  // found / empty / missing / unavailable / ambiguous / unsupported
    IReadOnlyList<CabinetSaveRoot> Roots,
    int Files,
    long Bytes,
    int CloudFiles,
    long CloudBytes,
    DateTimeOffset? LastUpload,
    DateTimeOffset? LastDownload,
    string? Error,
    string? OverrideMode,               // null, "supplement" or "replace"
    string? ManifestVersion,
    IReadOnlyList<string> Notes);

public enum CabinetRestoreAction { Create, Replace, Delete }
public sealed record CabinetRestoreItem(CabinetRestoreAction Action, string Destination, long Bytes);
/// <summary>Problems are the engine's own (English) reasons, e.g. an unmapped save location.</summary>
public sealed record CabinetRestorePreview(string? BackupPath, IReadOnlyList<CabinetRestoreItem> Items, IReadOnlyList<string> Problems, bool CanApply);

public interface ICabinetSaves
{
    Task RescanAsync(CancellationToken cancellationToken = default);
    /// <summary>mode: "supplement" adds folders to the discovered ones, "replace" uses only them. sync=false turns the game off.</summary>
    Task SetOverrideAsync(string appId, string mode, IReadOnlyList<string> folders, bool? sync, CancellationToken cancellationToken = default);
    Task ResetOverrideAsync(string appId, CancellationToken cancellationToken = default);
    /// <summary>Dry run: every destination path and the local backup file. Writes nothing.</summary>
    Task<CabinetRestorePreview> PreviewRestoreAsync(string appId, CancellationToken cancellationToken = default);
    Task RestoreAsync(string appId, CancellationToken cancellationToken = default);
    /// <summary>Conflict: upload this PC's saves over the current cloud version (kept in history).</summary>
    Task KeepLocalAsync(string appId, CancellationToken cancellationToken = default);
    /// <summary>Intentional deletion: records a tombstone; history and local saves are kept.</summary>
    Task DeleteCloudAsync(string appId, CancellationToken cancellationToken = default);
}
