using System.Globalization;
using System.IO;
using System.Text;
using LuaToolsGui.Services.Cabinet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuaToolsGui.Services.GameTools;

/// <summary>
/// Achievements, stats and playtime for one game on the Tools page.
///
/// <para><b>Steam running</b>: achievement and stat edits go through a hidden <c>--game-stats</c> helper
/// process bound to Steam as the game. If Steam reports a failure the stats cache is re-read a few times in
/// case it recorded the change anyway; otherwise the caller may offer to close Steam and write the files.</para>
/// <para><b>Steam closed</b>: the stats cache file is edited directly (one-time backup, atomic replace, Steam
/// re-checked before each commit).</para>
/// <para><b>CloudRedirect</b>: when CloudRedirect manages the game (<c>cloud_redirect\stats\&lt;acct&gt;\&lt;app&gt;.json</c>
/// exists), a lock must be written with Steam closed, together with CloudRedirect's copy and
/// <c>pending_edits.json</c>, or CloudRedirect would merge the bit back from its cloud copy.</para>
/// <para>Nothing here stops Steam on its own: <see cref="RunWithSteamClosedAsync{T}"/> is only called after the
/// user confirmed it.</para>
/// </summary>
public class GameToolsService
{
    // Error codes the view model localizes. Anything else is a raw exception message.
    public const string ErrNoSteam = "no-steam", ErrNoAccount = "no-account", ErrNoSchema = "no-schema",
        ErrSteamRunning = "steam-running", ErrSteamStarted = "steam-started", ErrSteamStop = "steam-stop-failed",
        ErrNoLocalConfig = "no-localconfig", ErrNoAppsSection = "no-apps-section", ErrCloudRedirectFile = "cr-malformed";

    private readonly Func<string?> _steamPath;
    private readonly Func<bool> _steamRunning;
    private readonly Action _stopSteam;
    private readonly Func<bool> _startSteam;
    private readonly Func<string, uint?> _account;
    private readonly Func<long, GameStatsEdit, GameStatsSchema, CancellationToken, Task<StatsSaveResult>> _saveLive;
    private readonly Func<long, CancellationToken, Task> _fetchSchema;
    private readonly Func<long, string, bool> _cabinetSyncs;
    private readonly Func<string> _language;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _backupDir;
    private readonly ILogger _log;
    private readonly TimeSpan _confirmDelay;
    private readonly SemaphoreSlim _writes = new(1, 1);

    public GameToolsService(SteamService steam, ICabinetSettings cabinetSettings, ICabinetService cabinet, ILogger<GameToolsService> log)
        : this(
            () => steam.EffectivePath,
            () => SteamRunningOrUnknown(steam),
            steam.StopSteam,
            steam.StartSteam,
            SteamAccounts.Current,
            GameStatsHelperClient.SaveLiveAsync,
            GameStatsHelperClient.FetchSchemaAsync,
            (appId, steamPath) => CabinetSyncScope.Syncs(
                cabinetSettings.CabinetEnabled && cabinet.Status.Enabled,
                cabinet.Status.Cloud.Connected,
                cabinet.Status.StandaloneCabinetRunning,
                Directory.Exists(Path.Combine(CabinetEngine.Syncer.OnlineFixDir, appId.ToString(CultureInfo.InvariantCulture))),
                File.Exists(Path.Combine(steamPath, "config", "stplug-in", appId.ToString(CultureInfo.InvariantCulture) + ".lua"))),
            () => SteamService.SteamLanguage ?? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "french" : "english"),
            () => DateTimeOffset.UtcNow,
            Path.Combine(CabinetEngine.App.DataDir, "backup"),
            log,
            TimeSpan.FromMilliseconds(500))
    { }

    internal GameToolsService(
        Func<string?> steamPath, Func<bool> steamRunning, Action stopSteam, Func<bool> startSteam,
        Func<string, uint?> account,
        Func<long, GameStatsEdit, GameStatsSchema, CancellationToken, Task<StatsSaveResult>> saveLive,
        Func<long, CancellationToken, Task> fetchSchema,
        Func<long, string, bool> cabinetSyncs, Func<string> language, Func<DateTimeOffset> now,
        string backupDir, ILogger? log = null, TimeSpan? confirmDelay = null)
    {
        _steamPath = steamPath; _steamRunning = steamRunning; _stopSteam = stopSteam; _startSteam = startSteam;
        _account = account; _saveLive = saveLive; _fetchSchema = fetchSchema; _cabinetSyncs = cabinetSyncs;
        _language = language; _now = now; _backupDir = backupDir; _log = log ?? NullLogger.Instance;
        _confirmDelay = confirmDelay ?? TimeSpan.Zero;
    }

    /// <summary>An unreadable Steam process identity counts as running: never write under a live Steam.</summary>
    private static bool SteamRunningOrUnknown(SteamService steam)
    {
        try { return steam.EffectivePath is not null && steam.IsSelectedSteamRunning(); }
        catch (InvalidOperationException) { return true; }
    }

    public bool IsSteamRunning() => _steamRunning();

    // ── Paths ───────────────────────────────────────────────────────

    private static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Id(uint v) => v.ToString(CultureInfo.InvariantCulture);
    private static string StatsDir(string steam) => Path.Combine(steam, "appcache", "stats");
    private static string SchemaPath(string steam, long app) => Path.Combine(StatsDir(steam), $"UserGameStatsSchema_{Id(app)}.bin");
    private static string UserStatsPath(string steam, uint account, long app) => Path.Combine(StatsDir(steam), $"UserGameStats_{Id(account)}_{Id(app)}.bin");
    private static string CloudRedirectStatsPath(string steam, uint account, long app) => Path.Combine(steam, "cloud_redirect", "stats", Id(account), Id(app) + ".json");
    private static string PendingEditsPath(string steam) => Path.Combine(steam, "cloud_redirect", "pending_edits.json");
    private static string LocalConfigPath(string steam, uint account) => Path.Combine(steam, "userdata", Id(account), "config", "localconfig.vdf");

    private GameStatsSchema? ReadSchema(string steam, long appId)
    {
        var bytes = GameStatsFiles.ReadShared(SchemaPath(steam, appId));
        return bytes is null ? null : GameStatsFiles.ReadSchema(bytes, appId, _language());
    }

    // ── Read ────────────────────────────────────────────────────────

    public GameStatsSnapshot Load(long appId)
    {
        string? steam = _steamPath();
        if (steam is null) return new(appId, null, false, false, false, [], []);
        uint? account = _account(steam);
        bool cloudRedirect = account is { } a && File.Exists(CloudRedirectStatsPath(steam, a, appId));
        bool cabinet = _cabinetSyncs(appId, steam);
        var schema = ReadSchema(steam, appId);
        if (schema is null) return new(appId, account, false, cloudRedirect, cabinet, [], []);
        byte[]? user = account is { } acct ? GameStatsFiles.ReadShared(UserStatsPath(steam, acct, appId)) : null;
        var (achievements, stats) = GameStatsFiles.ReadState(schema, user);
        return new(appId, account, true, cloudRedirect, cabinet, achievements, stats);
    }

    /// <summary>Ask Steam (running) to download a missing achievement list. True once the schema exists.</summary>
    public async Task<bool> FetchSchemaAsync(long appId, CancellationToken ct = default)
    {
        string? steam = _steamPath();
        if (steam is null || !_steamRunning()) return false;
        if (File.Exists(SchemaPath(steam, appId))) return true;
        await _fetchSchema(appId, ct).ConfigureAwait(false);
        return File.Exists(SchemaPath(steam, appId));
    }

    // ── Achievements and stats ──────────────────────────────────────

    /// <summary>
    /// Save with Steam in whatever state it is in now. Never stops Steam: when that is needed the result is
    /// <see cref="StatsSaveOutcome.NeedsSteamClosed"/> (CloudRedirect lock) or <see cref="StatsSaveOutcome.LiveRejected"/>,
    /// and the caller asks the user before calling <see cref="SaveWithSteamClosedAsync"/>.
    /// </summary>
    public async Task<StatsSaveResult> SaveAsync(long appId, GameStatsEdit edit, CancellationToken ct = default)
    {
        if (edit.IsEmpty) return new(StatsSaveOutcome.SavedToFile);
        string? steam = _steamPath();
        if (steam is null) return new(StatsSaveOutcome.Failed, ErrNoSteam);
        if (!_steamRunning()) return await Task.Run(() => SaveOffline(appId, edit), ct).ConfigureAwait(false);

        uint? account = _account(steam);
        if (edit.LocksAny && account is { } a && File.Exists(CloudRedirectStatsPath(steam, a, appId)))
            return new(StatsSaveOutcome.NeedsSteamClosed);
        var schema = ReadSchema(steam, appId);
        if (schema is null) return new(StatsSaveOutcome.Failed, ErrNoSchema);

        var live = await _saveLive(appId, edit, schema, ct).ConfigureAwait(false);
        if (live.Outcome == StatsSaveOutcome.SavedLive)
        {
            _log.LogInformation("Tools: saved {Count} change(s) for app {AppId} through Steam", edit.Achievements.Count + edit.Stats.Count, appId);
            return live;
        }
        // Steam may have recorded the change even though the helper saw no success: check its cache.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) await Task.Delay(_confirmDelay, ct).ConfigureAwait(false);
            if (GameStatsFiles.IsApplied(Load(appId), edit)) return new(StatsSaveOutcome.SavedLive);
        }
        _log.LogWarning("Tools: Steam did not accept the change for app {AppId}: {Error}", appId, live.Error);
        return live.Outcome == StatsSaveOutcome.Failed ? live : new(StatsSaveOutcome.LiveRejected, live.Error);
    }

    /// <summary>Close Steam (the user agreed), write the files, start Steam again.</summary>
    public Task<SteamClosedRun<StatsSaveResult>> SaveWithSteamClosedAsync(long appId, GameStatsEdit edit) =>
        RunWithSteamClosedAsync(() => SaveOffline(appId, edit), error => new StatsSaveResult(StatsSaveOutcome.Failed, error));

    /// <summary>Write the edit to Steam's files. Only while Steam is closed; Steam is re-checked before each commit.</summary>
    internal StatsSaveResult SaveOffline(long appId, GameStatsEdit edit)
    {
        _writes.Wait();
        try
        {
            string? steam = _steamPath();
            if (steam is null) return new(StatsSaveOutcome.Failed, ErrNoSteam);
            if (_steamRunning()) return new(StatsSaveOutcome.Failed, ErrSteamRunning);
            if (_account(steam) is not { } account) return new(StatsSaveOutcome.Failed, ErrNoAccount);
            var schema = ReadSchema(steam, appId);
            if (schema is null) return new(StatsSaveOutcome.Failed, ErrNoSchema);

            var now = _now();
            string userPath = UserStatsPath(steam, account, appId);
            byte[]? statsFile = GameStatsFiles.ApplyEdit(GameStatsFiles.ReadShared(userPath), schema, edit, now);

            // Prepare CloudRedirect's files first: a malformed one aborts before anything is written.
            string crPath = CloudRedirectStatsPath(steam, account, appId), pendingPath = PendingEditsPath(steam);
            string? crJson = null, pending = null;
            if (File.Exists(crPath))
            {
                try { crJson = CloudRedirectStatsFiles.ApplyEdit(File.ReadAllText(crPath, Encoding.UTF8), schema, edit, now); }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
                { return new(StatsSaveOutcome.Failed, ErrCloudRedirectFile); }
                pending = CloudRedirectStatsFiles.MergePendingEdits(
                    File.Exists(pendingPath) ? File.ReadAllText(pendingPath, Encoding.UTF8) : null, account, appId, schema, edit);
            }

            if (statsFile is not null)
            {
                SteamFileWriter.BackupOnce(userPath, _backupDir, Path.GetFileName(userPath));
                SteamFileWriter.WriteAtomic(userPath, statsFile, _steamRunning);
            }
            if (crJson is not null && pending is not null)
            {
                SteamFileWriter.BackupOnce(crPath, _backupDir, $"cloud_redirect_{Id(account)}_{Id(appId)}.json");
                SteamFileWriter.WriteAtomic(crPath, new UTF8Encoding(false).GetBytes(crJson), _steamRunning);
                SteamFileWriter.WriteAtomic(pendingPath, new UTF8Encoding(false).GetBytes(pending), _steamRunning);
            }
            _log.LogInformation("Tools: wrote {Count} change(s) for app {AppId} to Steam's files{CloudRedirect}",
                edit.Achievements.Count + edit.Stats.Count, appId, crJson is null ? "" : " and CloudRedirect's");
            return new(StatsSaveOutcome.SavedToFile);
        }
        catch (SteamStartedException) { return new(StatsSaveOutcome.Failed, ErrSteamStarted); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.LogWarning(ex, "Tools: writing stats for app {AppId} failed", appId);
            return new(StatsSaveOutcome.Failed, ex.Message);
        }
        finally { _writes.Release(); }
    }

    // ── Playtime ────────────────────────────────────────────────────

    /// <summary>Minutes Steam shows for the game, or null when localconfig.vdf has none.</summary>
    public int? GetPlaytimeMinutes(long appId)
    {
        try
        {
            if (_steamPath() is not { } steam || _account(steam) is not { } account) return null;
            var bytes = GameStatsFiles.ReadShared(LocalConfigPath(steam, account));
            return bytes is null ? null : PlaytimeConfig.GetMinutes(PlaytimeConfig.Decode(bytes).Text, appId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Set the playtime in localconfig.vdf. Null on success, else an error code or message.</summary>
    internal string? SetPlaytimeOffline(long appId, int minutes)
    {
        _writes.Wait();
        try
        {
            if (_steamPath() is not { } steam) return ErrNoSteam;
            if (_steamRunning()) return ErrSteamRunning;
            if (_account(steam) is not { } account) return ErrNoAccount;
            string path = LocalConfigPath(steam, account);
            var bytes = GameStatsFiles.ReadShared(path);
            if (bytes is null) return ErrNoLocalConfig;
            var (text, bom) = PlaytimeConfig.Decode(bytes);
            if (PlaytimeConfig.SetMinutes(text, appId, minutes) is not { } updated) return ErrNoAppsSection;
            if (updated == text) return null;
            SteamFileWriter.BackupOnce(path, _backupDir, $"localconfig_{Id(account)}.vdf");
            SteamFileWriter.WriteAtomic(path, PlaytimeConfig.Encode(updated, bom), _steamRunning);
            _log.LogInformation("Tools: set playtime of app {AppId} to {Minutes} min", appId, minutes);
            return null;
        }
        catch (SteamStartedException) { return ErrSteamStarted; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Tools: writing playtime for app {AppId} failed", appId);
            return ex.Message;
        }
        finally { _writes.Release(); }
    }

    public Task<string?> SetPlaytimeAsync(long appId, int minutes) => Task.Run(() => SetPlaytimeOffline(appId, minutes));

    public Task<SteamClosedRun<string?>> SetPlaytimeWithSteamClosedAsync(long appId, int minutes) =>
        RunWithSteamClosedAsync<string?>(() => SetPlaytimeOffline(appId, minutes), error => error);

    // ── Steam stop/start (only after the user confirmed) ────────────

    /// <summary>Stop Steam if it runs, do the work, then start Steam again if it was running.</summary>
    internal Task<SteamClosedRun<T>> RunWithSteamClosedAsync<T>(Func<T> work, Func<string, T> failed) => Task.Run(async () =>
    {
        bool wasRunning = _steamRunning();
        if (wasRunning)
        {
            try { _stopSteam(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Tools: closing Steam failed");
                return new SteamClosedRun<T>(failed(ErrSteamStop), false);
            }
            var until = DateTime.UtcNow.AddSeconds(15);
            while (_steamRunning() && DateTime.UtcNow < until) await Task.Delay(250).ConfigureAwait(false);
            if (_steamRunning()) return new SteamClosedRun<T>(failed(ErrSteamStop), false);
        }
        T result;
        bool restartFailed = false;
        try { result = work(); }
        finally
        {
            if (wasRunning && !SafeStart())
            {
                restartFailed = true;
                _log.LogWarning("Tools: Steam did not start again after the edit");
            }
        }
        return new SteamClosedRun<T>(result, restartFailed);
    });

    private bool SafeStart()
    {
        try { return _startSteam(); }
        catch (Exception ex) { _log.LogWarning(ex, "Tools: starting Steam failed"); return false; }
    }
}

/// <summary>Result of work done with Steam closed; <see cref="SteamRestartFailed"/> = Steam was closed for it
/// and could not be started again.</summary>
public sealed record SteamClosedRun<T>(T Result, bool SteamRestartFailed);
