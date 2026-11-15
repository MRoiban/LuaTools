namespace LuaToolsGui.Services.GameTools;

/// <summary>An achievement whose unlock depends on a stat ("progress" in Steam's schema).</summary>
public sealed record AchievementProgress(string StatName, double Min, double Max, double Value);

/// <summary>One achievement from Steam's schema, with the user's state from the stats cache.</summary>
public sealed record AchievementInfo(
    string ApiName, int StatId, int Bit, string Name, string Description, bool Hidden, bool Protected,
    string? IconUrl, string? IconGrayUrl, bool Unlocked = false, DateTimeOffset? UnlockedAt = null,
    AchievementProgress? Progress = null);

/// <summary>One plain stat. <see cref="Protected"/> = only the game's server may change it; AVGRATE stats
/// are listed read-only because Steam only updates them through UpdateAvgRateStat.</summary>
public sealed record StatInfo(int StatId, string ApiName, string Name, bool IsFloat, double Value, bool Protected, bool IncrementOnly);

/// <summary>A game's achievement schema (UserGameStatsSchema_&lt;app&gt;.bin) without user state.</summary>
public sealed record GameStatsSchema(IReadOnlyList<AchievementInfo> Achievements, IReadOnlyList<StatInfo> Stats);

/// <summary>What the Tools page shows for one game.</summary>
public sealed record GameStatsSnapshot(
    long AppId, uint? AccountId, bool HasSchema, bool CloudRedirectManaged, bool CabinetSyncs,
    IReadOnlyList<AchievementInfo> Achievements, IReadOnlyList<StatInfo> Stats);

/// <summary>Requested changes: achievement API name -> unlocked, stat API name -> value.</summary>
public sealed record GameStatsEdit(IReadOnlyDictionary<string, bool> Achievements, IReadOnlyDictionary<string, double> Stats)
{
    public bool IsEmpty => Achievements.Count == 0 && Stats.Count == 0;
    public bool LocksAny => Achievements.Values.Any(unlocked => !unlocked);
}

public enum StatsSaveOutcome
{
    /// <summary>Steam accepted the change while running.</summary>
    SavedLive,
    /// <summary>Written to Steam's files while Steam was closed.</summary>
    SavedToFile,
    /// <summary>Steam was running and didn't accept it; writing the files needs Steam closed.</summary>
    LiveRejected,
    /// <summary>Steam is running and the change can only be written with Steam closed (CloudRedirect lock).</summary>
    NeedsSteamClosed,
    Failed,
}

/// <summary><see cref="Error"/> is a stable code (see GameToolsService) or a raw exception message.</summary>
public sealed record StatsSaveResult(StatsSaveOutcome Outcome, string? Error = null);
