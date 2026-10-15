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
    CabinetCloudStatus Cloud)
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
