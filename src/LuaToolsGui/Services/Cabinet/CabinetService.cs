using System.Collections.Concurrent;
using System.IO;
using CabinetEngine;
using CabinetApp = CabinetEngine.App;
using Microsoft.Extensions.Hosting;

namespace LuaToolsGui.Services.Cabinet;

/// <summary>
/// Hosts the verbatim Cabinet engine (Services/Cabinet/Engine) in place of Cabinet's tray app.
/// It keeps the same five-second tick, the same data folder (%LOCALAPPDATA%\Cabinet) and the same
/// Local\Cabinet single-instance mutex. LuaTools and standalone Cabinet therefore share state and
/// never run the engine at the same time. All engine calls happen on one dedicated thread, as they
/// did on Cabinet's UI timer thread.
/// </summary>
public sealed class CabinetService : ICabinetService, ICabinetCloud, IHostedService, IDisposable
{
    private readonly ICabinetSettings _settings;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private CabinetStatus _status = CabinetStatus.Off;

    // Engine-thread state, mirroring Cabinet's App fields.
    private Mutex? _instance;
    private Playtime? _playtime;
    private Shadows? _shadows;
    private SchemaFill? _schemas;
    private bool _workshop;
    private readonly Dictionary<string, string> _state = new();
    private readonly Dictionary<string, string> _syncTimes = new();
    private static string StatePath => Path.Combine(CabinetApp.DataDir, "state.txt");
    private static string SyncTimesPath => Path.Combine(CabinetApp.DataDir, "sync-times.txt");

    public CabinetService(ICabinetSettings settings) { _settings = settings; }

    public CabinetStatus Status => Volatile.Read(ref _status);
    public event EventHandler<CabinetStatus>? StatusChanged;
    public event EventHandler<CabinetNotification>? Notification;
    public static bool HasActiveHelpers => Shadows.AnyHelper();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Cabinet engine" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    public void SettingsChanged()
    {
        if (!_settings.CabinetEnabled) Publish(CabinetStatus.Off);
        _wake.Set();
    }

    public Task SyncNowAsync(CancellationToken cancellationToken = default) => OnEngine(() => Tick(true));

    // ── ICabinetCloud: the tray's Cabinet Cloud menu, driven from Settings ──
    CabinetCloudStatus ICabinetCloud.Status => Status.Cloud;
    public event EventHandler<CabinetCloudStatus>? CloudStatusChanged;
    event EventHandler<CabinetCloudStatus>? ICabinetCloud.StatusChanged
    {
        add => CloudStatusChanged += value;
        remove => CloudStatusChanged -= value;
    }

    public bool FriendLabels
    {
        get => CabinetEngine.FriendLabels.DebugEnabled();
        set { if (value && !CabinetEngine.FriendLabels.DebugEnabled() && !CabinetEngine.FriendLabels.EnableDebug()) throw new IOException("Steam debugging marker not created"); }
    }

    public Task ConnectAsync(string link, CancellationToken cancellationToken = default)
    {
        try { CloudClient.ParseLink(link.Trim(), out _, out _); }
        catch (Exception ex) { throw new FormatException("Invalid Cabinet Cloud link", ex); }
        return OnEngine(() => CloudSync.QueueConnect(link));
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => OnEngine(CloudSync.Disconnect);

    Task ICabinetCloud.SyncNowAsync(CancellationToken cancellationToken) => OnEngine(CloudSync.RequestSync);

    private Task OnEngine(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        _wake.Set();
        return done.Task;
    }

    // ── Engine thread ──
    private void Run()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                bool active = false;
                try { active = Prepare(); }
                catch (Exception ex) { CabinetApp.Log("LuaTools host error: " + ex.Message); }
                // Queued Cloud requests take effect on the next active tick, as with Cabinet's tray menu.
                while (_work.TryDequeue(out var action)) action();
                if (active)
                {
                    try { Tick(false); }
                    catch (Exception ex) { CabinetApp.Log("LuaTools host error: " + ex.Message); }
                }
                _wake.WaitOne(5000);
            }
        }
        finally { Shutdown(); }
    }

    /// <summary>True when this process owns Local\Cabinet and the engine components exist.</summary>
    private bool Prepare()
    {
        if (!_settings.CabinetEnabled) { Shutdown(); Publish(CabinetStatus.Off); return false; }
        if (_instance is null)
        {
            var mutex = new Mutex(true, @"Local\Cabinet", out bool created);
            if (!created)
            {
                // Standalone Cabinet (or another LuaTools) owns the engine: pause until it exits.
                mutex.Dispose();
                Publish(Status with { Enabled = true, IsSyncing = false, StandaloneCabinetRunning = true });
                return false;
            }
            _instance = mutex;
        }
        if (_playtime is null)
        {
            Directory.CreateDirectory(CabinetApp.DataDir);
            LoadState();
            _playtime = new Playtime(Path.Combine(CabinetApp.DataDir, "playtime.txt"), CabinetApp.Log);
            CloudSync.Configure(CabinetApp.Log, _playtime.QueueCloudMinutes, _playtime.CloudMinutesInSteam, () => _playtime!.RunningApps());
            CabinetApp.Log("started inside LuaTools");
        }
        if (_settings.CabinetLivePlaytime) _shadows ??= new Shadows(CabinetApp.Log);
        else if (_shadows is not null) { _shadows.Dispose(); _shadows = null; }
        if (_settings.CabinetFetchSchemas) _schemas ??= new SchemaFill(CabinetApp.Log);
        else if (_schemas is not null) { _schemas.Dispose(); _schemas = null; }
        if (_settings.CabinetWorkshopLinks && !_workshop) { WorkshopLinker.Initialize(CabinetApp.Log); _workshop = true; }
        _workshop &= _settings.CabinetWorkshopLinks;
        return true;
    }

    private void Shutdown()
    {
        _schemas?.Dispose(); _schemas = null;
        _shadows?.Dispose(); _shadows = null;
        _playtime = null;
        _workshop = false;
        _state.Clear(); _syncTimes.Clear();
        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch (ApplicationException) { }
            _instance.Dispose();
            _instance = null;
        }
    }

    // Cabinet's App.TickCore, with LuaTools switches gating its optional parts.
    private void Tick(bool force)
    {
        if (_playtime is null) return;
        Publish(Status with { Enabled = true, IsSyncing = true, StandaloneCabinetRunning = false });
        var running = new HashSet<string>();
        // Count before launching/stopping helpers; Running=1 excludes native Steam time.
        try
        {
            var account = Syncer.AccountId();
            var msg = _playtime.Tick(account);
            running = _playtime.RunningApps();
            if (_workshop)
            {
                var workshopMsg = WorkshopLinker.Tick(running);
                if (workshopMsg != null) Message(workshopMsg);
            }
            var schemaMsg = _schemas?.Tick(running.Count > 0 || (_shadows?.HasChildren ?? false), false);
            if (_shadows is not null) foreach (var id in _shadows.Tick(running, account)) _state.Remove(id);
            var cloudMsg = CloudSync.Tick(running.Count > 0 || (_shadows?.HasChildren ?? false) || (_schemas?.HasChild ?? false) || Shadows.AnyHelper(), running);
            if (cloudMsg != null) Message(cloudMsg);
            if (schemaMsg != null) Message(schemaMsg);
            if (msg != null) Message(msg);
        }
        catch (Exception ex) { CabinetApp.Log("playtime/shadow error: " + ex.Message); }
        try
        {
            var account = Syncer.AccountId();
            if (account == null) return;
            var synced = new List<string>();
            foreach (var appId in Syncer.OnlineFixAppIds())
            {
                // Steam IPC owns this cache while a helper is connected. File fallback
                // retries after exit, avoiding competing writers and stale fingerprints.
                if ((_shadows?.Active(appId) ?? false) || (_schemas?.Active(appId) ?? false) || Shadows.HelperActive(appId)) continue;
                var fp = account + "|" + Syncer.Fingerprint(appId);
                if (!force && _state.TryGetValue(appId, out var last) && last == fp &&
                    (_syncTimes.ContainsKey(appId) || !File.Exists(SchemaFill.SchemaPath(appId)))) continue;
                SyncResult r;
                try { r = Syncer.Sync(appId, account); }
                catch (IOException) { continue; }   // file busy, retry next tick
                _state[appId] = fp;
                if (r.Skipped != null) { CabinetApp.Log(r.GameName + " (" + appId + "): skipped, " + r.Skipped); continue; }
                _syncTimes[appId] = DateTime.UtcNow.Ticks.ToString();
                if (r.NewAchievements > 0 || r.StatsChanged > 0)
                {
                    CabinetApp.Log(r.GameName + " (" + appId + "): +" + r.NewAchievements + " achievements, " + r.StatsChanged + " stats");
                    if (r.NewAchievements > 0) synced.Add(r.GameName + " : +" + r.NewAchievements + L.T(" achievement", " succès") + (r.NewAchievements == 1 || L.Fr ? "" : "s"));
                }
            }
            File.WriteAllLines(StatePath, _state.Select(kv => kv.Key + "=" + kv.Value).ToArray());
            File.WriteAllLines(SyncTimesPath, _syncTimes.Select(kv => kv.Key + "|" + kv.Value).ToArray());
            if (synced.Count > 0)
                Message(L.T("Achievements synced to Steam", "Succès synchronisés avec Steam") + "\n" + string.Join("\n", synced));
            else if (force)
                Notify(new CabinetNotification(CabinetNotificationKind.AlreadyInSync, Array.Empty<string>(), 0));
        }
        catch (Exception ex) { CabinetApp.Log("error: " + ex.Message); }
        finally { PublishSnapshot(running); }
    }

    private void PublishSnapshot(HashSet<string> running)
    {
        var games = new List<CabinetGame>();
        try
        {
            foreach (var g in CabinetApp.Games(_syncTimes))
            {
                bool list = g.Total > 0;
                var synced = g.Synced > 0 && g.Synced <= DateTime.MaxValue.Ticks
                    ? new DateTimeOffset(new DateTime(g.Synced, DateTimeKind.Utc)) : (DateTimeOffset?)null;
                games.Add(new CabinetGame(g.Id, g.Name, list ? g.Unlocked : null, list ? g.Total : null,
                    g.Minutes / 60.0, synced, running.Contains(g.Id), _shadows?.Active(g.Id) ?? false,
                    _playtime?.PendingMinutes(g.Id) ?? 0, list));
            }
        }
        catch (Exception ex) { CabinetApp.Log("overview error: " + ex.Message); }
        CabinetCloudStatus cloud;
        try
        {
            string? server = CloudSync.Server;
            string? host = Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.Host : server;
            cloud = CloudSync.Connected ? new CabinetCloudStatus(true, CloudSync.UserName, host, null) : CabinetCloudStatus.Disconnected;
        }
        catch { cloud = CabinetCloudStatus.Disconnected; }
        bool cloudChanged = cloud != Status.Cloud;
        Publish(new CabinetStatus(true, Playtime.SteamRunning(), false, false, games, cloud));
        if (cloudChanged) { try { CloudStatusChanged?.Invoke(this, cloud); } catch { } }
    }

    private void LoadState()
    {
        if (File.Exists(StatePath)) foreach (var l in File.ReadAllLines(StatePath))
        {
            int i = l.IndexOf('=');
            if (i > 0) _state[l.Substring(0, i)] = l.Substring(i + 1);
        }
        if (File.Exists(SyncTimesPath)) foreach (var line in File.ReadAllLines(SyncTimesPath))
        {
            var p = line.Split('|'); if (p.Length == 2) _syncTimes[p[0]] = p[1];
        }
    }

    private void Message(string text) => Notify(new CabinetNotification(CabinetNotificationKind.Message, new[] { text }, 1));

    private void Publish(CabinetStatus status)
    {
        Volatile.Write(ref _status, status);
        try { StatusChanged?.Invoke(this, status); } catch (Exception ex) { CabinetApp.Log("status subscriber: " + ex.Message); }
    }

    private void Notify(CabinetNotification notification)
    {
        try { Notification?.Invoke(this, notification); } catch (Exception ex) { CabinetApp.Log("notification subscriber: " + ex.Message); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _wake.Set();
    }
}
