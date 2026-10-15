using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services.Cabinet;

/// <summary>Serializes the five-second engine cadence and manual sync without WPF dependencies.</summary>
public sealed class CabinetService : ICabinetService, IHostedService, IDisposable
{
    private readonly ICabinetSettings _settings;
    private readonly CabinetCloud? _cloud;
    private readonly CabinetPaths _paths;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycle = new();
    private Task _transition = Task.CompletedTask;
    private Task? _loop;
    private CancellationTokenSource? _cancel;
    private bool _hosted, _disposed;
    private long _revision;
    private CabinetStatus _status = CabinetStatus.Off;
    private Playtime? _playtime;
    private Shadows? _helpers;
    private SchemaFill? _schemas;
    private bool _workshop;
    private readonly Dictionary<string, string> _fingerprints = new();
    private readonly Dictionary<string, long> _syncTimes = new();

    public CabinetService(ICabinetSettings settings, ILogger<CabinetService> logger, CabinetCloud cloud)
        : this(settings, CabinetPaths.Default, line => logger.LogInformation("Cabinet: {Detail}", line), cloud) { }
    public CabinetService(ICabinetSettings settings, CabinetPaths paths, Action<string>? log = null, CabinetCloud? cloud = null)
    {
        _settings = settings; _paths = paths; _log = log ?? PluginLog.Log; _cloud = cloud;
        _cloud?.BindEngineSync(SyncNowAsync);
        if (_cloud is not null) _cloud.StatusChanged += (_, status) => Publish(Status with { Cloud = status });
    }
    public CabinetStatus Status => Volatile.Read(ref _status);
    public event EventHandler<CabinetStatus>? StatusChanged;
    public event EventHandler<CabinetNotification>? Notification;
    public static bool HasActiveHelpers => CabinetHelperActivity.Active;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle) _hosted = true;
        SettingsChanged();
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle) _hosted = false;
        SettingsChanged();
        Task transition;
        lock (_lifecycle) transition = _transition;
        await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public void SettingsChanged()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            var revision = ++_revision;
            _cancel?.Cancel();
            // Chain lifecycle changes: a previous loop fully retires before its replacement starts.
            _transition = _transition.ContinueWith(async _ =>
            {
                Task? loop;
                lock (_lifecycle) { _cancel?.Cancel(); loop = _loop; }
                if (loop is not null) await loop.ConfigureAwait(false);
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var context = _paths.Enter();
                    if (!_settings.CabinetEnabled || !_hosted) StopComponents();
                    else ConfigureComponents();
                }
                catch (Exception ex) { _log("Configuration deferred: " + ex.Message); }
                finally { _gate.Release(); }
                lock (_lifecycle)
                {
                    _cancel?.Dispose();
                    _cancel = null;
                    _loop = null;
                    if (_hosted && _settings.CabinetEnabled && !_disposed && revision == _revision)
                    {
                        _cancel = new CancellationTokenSource();
                        var token = _cancel.Token;
                        _loop = Task.Run(() => LoopAsync(token));
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
        if (!_settings.CabinetEnabled) Publish(CabinetStatus.Off);
    }
    private async Task LoopAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && _settings.CabinetEnabled)
            {
                await RunAsync(false, cancellation).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
    public Task SyncNowAsync(CancellationToken cancellationToken = default) => RunAsync(true, cancellationToken);
    private async Task RunAsync(bool manual, CancellationToken cancellation)
    {
        if (!_settings.CabinetEnabled || _disposed) return;
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!_settings.CabinetEnabled || _disposed) return;
            await Task.Run(() => Step(manual, cancellation), cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log("Sync deferred: " + ex.Message); }
        finally
        {
            if (!_settings.CabinetEnabled) Publish(CabinetStatus.Off);
            else if (Status.IsSyncing) Publish(Status with { IsSyncing = false });
            _gate.Release();
        }
    }
    private void ConfigureComponents()
    {
        if (!_settings.CabinetEnabled) { StopComponents(); return; }
        if (_paths.StandaloneRunning()) { StopComponents(); return; }
        if (_playtime is null)
        {
            _paths.ImportStandalone();
            _playtime = new Playtime(Path.Combine(_paths.DataRoot, "playtime.txt"), _log);
            _playtime.RefreshGames();
            LoadState();
        }
        if (_settings.CabinetLivePlaytime) _helpers ??= new Shadows(_log);
        else { _helpers?.Dispose(); _helpers = null; }
        Optional("Schema configuration", () =>
        {
            if (_settings.CabinetFetchSchemas) _schemas ??= new SchemaFill(_log);
            else { _schemas?.Dispose(); _schemas = null; }
        });
        Optional("Workshop configuration", () =>
        {
            if (_settings.CabinetWorkshopLinks && !_workshop) { WorkshopLinker.Initialize(_log); _workshop = true; }
            else if (!_settings.CabinetWorkshopLinks && _workshop) { WorkshopLinker.Stop(); _workshop = false; }
        });
    }
    private void StopComponents()
    {
        _cloud?.Suspend();
        _schemas?.Dispose(); _schemas = null;
        _helpers?.Dispose(); _helpers = null;
        if (_workshop) WorkshopLinker.Stop();
        _workshop = false;
        _playtime = null;
        _fingerprints.Clear(); _syncTimes.Clear();
    }
    private void Step(bool manual, CancellationToken cancellation)
    {
        using var context = _paths.Enter();
        if (_paths.StandaloneRunning())
        {
            StopComponents();
            Publish(Status with { Enabled = true, IsSyncing = false, StandaloneCabinetRunning = true,
                Games = Status.Games.Select(g => g with { IsPlaying = false, LivePlaytime = false }).ToArray() });
            return;
        }
        ConfigureComponents();
        if (!_settings.CabinetEnabled || _playtime is null) return;
        cancellation.ThrowIfCancellationRequested();
        var account = Syncer.AccountId();
        var running = _playtime.RunningApps();
        Publish(Status with { Enabled = true, SteamRunning = _paths.SteamRunning(), IsSyncing = true, StandaloneCabinetRunning = false });
        // Count the elapsed interval before helpers can begin Steam-owned counting.
        _playtime.Tick(account);
        var completed = HelperTickOverride is not null ? HelperTickOverride(running, account) : _helpers?.Tick(running, account);
        if (completed is not null)
        {
            bool invalidated = false;
            foreach (var id in completed) invalidated |= _fingerprints.Remove(id);
            // Persist invalidation even if a later pass is interrupted or has no account.
            if (invalidated) SaveFingerprints();
        }
        if (_playtime.LastAdded.Count > 0)
            Notify(CabinetNotificationKind.PlaytimeAdded, _playtime.LastAdded.Select(item =>
            { var split = item.LastIndexOf('|'); return GameName(item[..split]) + item[split..]; }).ToArray(), _playtime.LastAdded.Count);
        if (!_settings.CabinetEnabled) return;
        Optional("Workshop", () =>
        {
            OptionalTickOverride?.Invoke();
            if (!_workshop) return;
            WorkshopLinker.Tick(running);
            if (WorkshopLinker.LastCreated > 0) Notify(CabinetNotificationKind.WorkshopChanged, WorkshopLinker.LastGames, WorkshopLinker.LastCreated);
        });
        Optional("Schema fetch", () =>
        {
            _schemas?.Tick(running.Count > 0 || (_helpers?.HasChildren ?? false), false);
            if ((_schemas?.LastFound ?? 0) > 0) Notify(CabinetNotificationKind.AchievementListsFound, Array.Empty<string>(), _schemas!.LastFound);
        });
        var synced = new List<string>();
        bool changed = false;
        if (account is not null)
        {
            foreach (var id in Syncer.OnlineFixAppIds())
            {
                cancellation.ThrowIfCancellationRequested();
                if (!_settings.CabinetEnabled || _paths.StandaloneRunning()) return;
                // No competing disk writer while the game or its Steam IPC helper owns the cache.
                if (running.Contains(id) || (_helpers?.Active(id) ?? false) || (_schemas?.Active(id) ?? false) || Shadows.HelperActive(id)) continue;
                var fingerprint = account + "|" + Syncer.Fingerprint(id);
                if (!manual && _fingerprints.TryGetValue(id, out var previous) && previous == fingerprint && (_syncTimes.ContainsKey(id) || !File.Exists(SchemaFill.SchemaPath(id)))) continue;
                try
                {
                    var result = Syncer.Sync(id, account);
                    _fingerprints[id] = fingerprint;
                    if (result.Skipped is not null) { _log(id + ": " + result.Skipped); continue; }
                    _syncTimes[id] = DateTime.UtcNow.Ticks;
                    changed |= result.NewAchievements > 0 || result.StatsChanged > 0;
                    if (result.NewAchievements > 0) synced.Add(result.GameName + "|" + result.NewAchievements);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { _log(id + ": deferred: " + ex.Message); }
            }
            SaveFingerprints();
            WorkshopEngine.AtomicWrite(Path.Combine(_paths.DataRoot, "sync-times.txt"), string.Join(Environment.NewLine, _syncTimes.Select(p => p.Key + "|" + p.Value)));
        }
        Optional("Cloud/presence", () => _cloud?.Tick(_playtime, account, running, GameName,
            id => !_settings.CabinetEnabled || running.Contains(id) || (_playtime?.RunningApps().Contains(id) ?? false)
                || Shadows.HelperActive(id) || Playtime.SteamCounting(id) || (_schemas?.Active(id) ?? false) || _paths.StandaloneRunning(), manual, cancellation), cancellation);
        Publish(new CabinetStatus(true, _paths.SteamRunning(), false, false, ReadGames(account, running), _cloud?.Status ?? CabinetCloudStatus.Disconnected));
        if (synced.Count > 0) Notify(CabinetNotificationKind.AchievementsSynced, synced, synced.Count);
        else if (manual && !changed) Notify(CabinetNotificationKind.AlreadyInSync, Array.Empty<string>(), 0);
    }
    // Fixture hooks exercise real skip/sync behavior without starting Steam helper processes.
    internal Func<HashSet<string>, string?, IEnumerable<string>>? HelperTickOverride { get; set; }
    internal Action? OptionalTickOverride { get; set; }
    internal Task TickAsync() => RunAsync(false, default);
    private void SaveFingerprints() => WorkshopEngine.AtomicWrite(Path.Combine(_paths.DataRoot, "state.txt"),
        string.Join(Environment.NewLine, _fingerprints.Select(p => p.Key + "=" + p.Value)));
    private void Optional(string component, Action action, CancellationToken cancellation = default)
    {
        try { action(); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log(component + " deferred: " + (component == "Cloud/presence" ? ex.GetType().Name : ex.Message)); }
    }
    private IReadOnlyList<CabinetGame> ReadGames(string? account, HashSet<string> running)
    {
        VdfText? config = null;
        if (account is not null)
        {
            var file = Path.Combine(Syncer.SteamPath(), "userdata", account, "config", "localconfig.vdf");
            CabinetFiles.EnsureSafePath(file);
            if (File.Exists(file)) config = new VdfText(CabinetFiles.ReadText(file));
        }
        var games = new List<CabinetGame>();
        foreach (var id in Syncer.OnlineFixAppIds())
        {
            string name = GameName(id); int total = 0, unlocked = 0; bool schemaFound = false;
            try
            {
                var schema = SchemaFill.SchemaPath(id);
                CabinetFiles.EnsureSafePath(schema);
                if (File.Exists(schema))
                {
                    var game = KV.Read(CabinetFiles.ReadBytes(schema)).Children.FirstOrDefault();
                    name = game?.Get("gamename")?.Str() ?? name;
                    var stats = game?.Get("stats");
                    schemaFound = stats?.Children is not null;
                    var user = Path.Combine(Syncer.StatsDir(), "UserGameStats_" + account + "_" + id + ".bin");
                    CabinetFiles.EnsureSafePath(user);
                    var cache = File.Exists(user) ? KV.Read(CabinetFiles.ReadBytes(user)).Children.FirstOrDefault() : null;
                    if (stats?.Children is not null) foreach (var stat in stats.Children)
                    {
                        var bits = stat.Get("bits")?.Children;
                        if (bits is null) continue;
                        int mask = Convert.ToInt32(cache?.Get(stat.Key)?.Get("data")?.Value ?? 0);
                        foreach (var bit in bits)
                            if (int.TryParse(bit.Get("bit")?.Str() ?? bit.Key, out var n) && n is >= 0 and <= 31)
                            { total++; if ((mask & (1 << n)) != 0) unlocked++; }
                    }
                }
            }
            catch (Exception ex) { _log("Overview " + id + ": " + ex.Message); }
            long.TryParse(config?.GetAppValue(id, "Playtime"), out var minutes);
            _syncTimes.TryGetValue(id, out var ticks);
            var last = ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)) : (DateTimeOffset?)null;
            games.Add(new CabinetGame(id, name, schemaFound ? unlocked : null, schemaFound ? total : null,
                Math.Max(0, minutes) / 60.0, last, running.Contains(id), _helpers?.Active(id) ?? false,
                _playtime?.PendingMinutes(id) ?? 0, schemaFound));
        }
        return games.OrderByDescending(g => g.IsPlaying).ThenByDescending(g => g.LastSynced).ThenBy(g => g.AppId, StringComparer.Ordinal).ToArray();
    }
    private string GameName(string id)
    {
        try
        {
            CabinetPaths.Numeric(id);
            var schema = SchemaFill.SchemaPath(id);
            if (File.Exists(schema))
            {
                var name = KV.Read(CabinetFiles.ReadBytes(schema)).Children.FirstOrDefault()?.Get("gamename")?.Str();
                if (!string.IsNullOrEmpty(name)) return name;
            }
            foreach (var library in Playtime.Libraries())
            {
                var manifest = Path.Combine(library, "steamapps", "appmanifest_" + id + ".acf");
                if (!File.Exists(manifest)) continue;
                var name = new VdfText(CabinetFiles.ReadText(manifest)).StringValue("AppState", "name");
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch (Exception ex) { _log("Game name " + id + ": " + ex.Message); }
        return id;
    }
    private void LoadState()
    {
        var state = Path.Combine(_paths.DataRoot, "state.txt");
        CabinetFiles.EnsureSafePath(state);
        if (File.Exists(state)) foreach (var line in CabinetFiles.ReadLines(state))
        { int equals = line.IndexOf('='); if (equals > 0) _fingerprints[line[..equals]] = line[(equals + 1)..]; }
        var times = Path.Combine(_paths.DataRoot, "sync-times.txt");
        CabinetFiles.EnsureSafePath(times);
        if (File.Exists(times)) foreach (var line in CabinetFiles.ReadLines(times))
        { var pair = line.Split('|'); if (pair.Length == 2 && long.TryParse(pair[1], out var ticks)) _syncTimes[pair[0]] = ticks; }
    }
    private void Publish(CabinetStatus status)
    {
        Volatile.Write(ref _status, status);
        try { StatusChanged?.Invoke(this, status); } catch (Exception ex) { _log("Status subscriber: " + ex.Message); }
    }
    private void Notify(CabinetNotificationKind kind, IReadOnlyList<string> items, int count)
    {
        try { Notification?.Invoke(this, new CabinetNotification(kind, items, count)); } catch (Exception ex) { _log("Notification subscriber: " + ex.Message); }
    }
    public void Dispose()
    {
        lock (_lifecycle) { if (_disposed) return; _disposed = true; _hosted = false; _cancel?.Cancel(); }
        _gate.Wait();
        try { using var context = _paths.Enter(); StopComponents(); }
        finally { _gate.Release(); }
        Publish(CabinetStatus.Off);
    }
}

internal static class CabinetHelperActivity
{
    private static int _children;
    internal static bool Active => Volatile.Read(ref _children) > 0 || AnyEvent();
    private static bool AnyEvent()
    {
        try { using var activity = EventWaitHandle.OpenExisting("Local\\Cabinet-shadow-active"); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    internal static void Track(System.Diagnostics.Process child)
    {
        // Separate process handle remains valid after the component disposes its own handle.
        System.Diagnostics.Process monitor;
        try { monitor = System.Diagnostics.Process.GetProcessById(child.Id); }
        catch (ArgumentException) { return; } // Already exited; there is no executable in use.
        Interlocked.Increment(ref _children);
        _ = Task.Run(() =>
        {
            try { monitor.WaitForExit(); }
            catch (InvalidOperationException) { }
            finally { monitor.Dispose(); Interlocked.Decrement(ref _children); }
        });
    }
}
