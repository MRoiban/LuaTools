using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
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
public sealed class CabinetService : ICabinetService, ICabinetCloud, ICabinetSaves, IHostedService, IDisposable
{
    // The engine's manifest download goes through LuaTools' outbound policy (raw.githubusercontent.com).
    static CabinetService() { SaveManifestCache.Fetcher = FetchManifest; }

    private readonly ICabinetSettings _settings;
    private readonly UnlockerService? _unlocker;
    private readonly CloudRedirectCabinetConfig _cloudRedirectConfig = new();
    private int _cloudRedirectVersion = -1;
    private bool _cloudRedirectEnabled;
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

    public CabinetService(ICabinetSettings settings, UnlockerService? unlocker = null)
    { _settings = settings; _unlocker = unlocker; }

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

    // Sync now also runs a Cabinet Cloud round (progress and save backups).
    public Task SyncNowAsync(CancellationToken cancellationToken = default) => OnEngine(() => { CloudSync.RequestSync(); Tick(true); });

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

    public Task ConnectAsync(string input, string? server = null, CancellationToken cancellationToken = default)
    {
        string origin = CabinetConnectInput.NormalizeServer(server);
        if (CabinetConnectInput.Classify(input) == CabinetConnectInputKind.Invalid)
            throw new CabinetConnectException(CabinetConnectFailure.InvalidCode);
        return OnEngine(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_instance is null) throw new InvalidOperationException("Cabinet engine is not active");
            try { CloudSync.Connect(input.Trim(), origin); }
            catch (System.Net.WebException ex)
            {
                throw new CabinetConnectException(ex.Response is null
                    ? CabinetConnectFailure.Unreachable : CabinetConnectFailure.Server);
            }
            try { ReconcileCloudRedirect(); }
            catch { throw new CabinetConnectException(CabinetConnectFailure.Config); }
            finally { PublishCloudSnapshot(); }
        });
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => OnEngine(() =>
    {
        if (CloudSync.Busy) throw new InvalidOperationException("Cabinet Cloud is busy");
        CloudSync.Disconnect();
        try { _cloudRedirectConfig.Remove(); _cloudRedirectVersion = CloudSync.ConnectionVersion; }
        finally { PublishCloudSnapshot(); }
    });

    // Called after the add-on is enabled. Credentials stay on the engine thread.
    internal Task ConfigureCloudRedirectAsync() => OnEngine(() => ReconcileCloudRedirect(force: true));

    private void ReconcileCloudRedirect(bool force = false)
    {
        if (CloudSync.Busy) return;
        bool enabled = _unlocker?.IsCloudRedirectAddonEnabled == true;
        if (!force && _cloudRedirectVersion == CloudSync.ConnectionVersion && _cloudRedirectEnabled == enabled) return;
        if (CloudSync.Connected && enabled) _cloudRedirectConfig.Apply(CloudSync.Server, CloudSync.Token);
        else if (!CloudSync.Connected) _cloudRedirectConfig.Remove();
        _cloudRedirectVersion = CloudSync.ConnectionVersion;
        _cloudRedirectEnabled = enabled;
    }

    private CabinetCloudStatus ReadCloudStatus()
    {
        string? server = CloudSync.Server;
        string? host = Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.Host : server;
        return CloudSync.Connected ? new CabinetCloudStatus(true, CloudSync.UserName, host, null) : CabinetCloudStatus.Disconnected;
    }

    private void PublishCloudSnapshot()
    {
        var cloud = ReadCloudStatus();
        Publish(Status with { Cloud = cloud });
        try { CloudStatusChanged?.Invoke(this, cloud); } catch { }
    }

    Task ICabinetCloud.SyncNowAsync(CancellationToken cancellationToken) => OnEngine(CloudSync.RequestSync);

    // ── ICabinetSaves: run on the Cabinet Cloud worker, between rounds ──
    private static readonly TimeSpan SaveActionTimeout = TimeSpan.FromMinutes(3);

    private async Task<T> OnSaves<T>(Func<CloudClient, T> action, bool needsCloud, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnEngine(() =>
        {
            if (_instance is null) throw new InvalidOperationException("Cabinet engine is not active");
            if (needsCloud && !CloudSync.Connected) throw new InvalidOperationException("Cabinet Cloud is not connected");
            CloudSync.QueueSaveAction(client =>
            {
                try { done.TrySetResult(action(client)); }
                catch (Exception ex) { done.TrySetException(ex); }
            });
        }).ConfigureAwait(false);
        _wake.Set();
        try { return await done.Task.WaitAsync(SaveActionTimeout, cancellationToken).ConfigureAwait(false); }
        finally { _wake.Set(); }
    }
    private Task OnSaves(Action<CloudClient> action, bool needsCloud, CancellationToken cancellationToken) =>
        OnSaves<bool>(client => { action(client); PublishSoon(); return true; }, needsCloud, cancellationToken);
    private void PublishSoon() => _work.Enqueue(() => PublishSnapshot(_playtime?.RunningApps() ?? new HashSet<string>()));

    private static void CheckAppId(string appId)
    {
        if (string.IsNullOrEmpty(appId) || appId.Length > 10 || !appId.All(char.IsAsciiDigit)) throw new ArgumentException("Invalid AppID", nameof(appId));
    }
    private static (SaveCloudMeta? Archive, SaveLegacyMeta? Legacy) CloudState(CloudClient client, string appId)
    {
        var state = client.AppStateOf(appId);
        state.TryGetValue("archive", out var archive); state.TryGetValue("save", out var save);
        return (SaveCloudMeta.Parse(archive), SaveLegacyMeta.Parse(save));
    }
    private static Func<string, bool> Blocked => CloudSync.BlockedApp;

    public Task RescanAsync(CancellationToken cancellationToken = default) =>
        OnSaves(client => client.ScanSaves(client.Connected), false, cancellationToken);

    public Task SetOverrideAsync(string appId, string mode, IReadOnlyList<string> folders, bool? sync, CancellationToken cancellationToken = default)
    {
        CheckAppId(appId);
        return OnSaves(client =>
        {
            client.Saves.Overrides.Set(appId, mode, folders, sync);
            var env = client.Saves.CurrentEnvironment(true);
            client.Saves.Scan(appId, env, client.Saves.Eligible(env).Contains(appId), client.Connected);
        }, false, cancellationToken);
    }

    public Task ResetOverrideAsync(string appId, CancellationToken cancellationToken = default)
    {
        CheckAppId(appId);
        return OnSaves(client =>
        {
            client.Saves.Overrides.Reset(appId);
            var env = client.Saves.CurrentEnvironment(true);
            client.Saves.Scan(appId, env, client.Saves.Eligible(env).Contains(appId), client.Connected);
        }, false, cancellationToken);
    }

    public Task<CabinetRestorePreview> PreviewRestoreAsync(string appId, CancellationToken cancellationToken = default)
    {
        CheckAppId(appId);
        return OnSaves(client =>
        {
            var (archive, legacy) = CloudState(client, appId);
            string? version = archive is { Deleted: false, Files: > 0 } ? archive.Id : legacy is { Empty: false } ? "legacy" : null;
            if (version is null) throw new InvalidOperationException("No cloud backup with save files");
            var plan = client.Saves.PreviewRestore(appId, version, client, client.Saves.CurrentEnvironment(true), out _, out _);
            return ToPreview(plan);
        }, true, cancellationToken);
    }

    public Task RestoreAsync(string appId, CancellationToken cancellationToken = default)
    {
        CheckAppId(appId);
        return OnSaves(client =>
        {
            var (archive, legacy) = CloudState(client, appId);
            string? version = archive is { Deleted: false, Files: > 0 } ? archive.Id : legacy is { Empty: false } ? "legacy" : null;
            if (version is null) throw new InvalidOperationException("No cloud backup with save files");
            client.Saves.UseAccount(client.Server, client.UserId);
            client.Saves.Restore(appId, version, archive, client, Blocked, client.Saves.CurrentEnvironment(true));
        }, true, cancellationToken);
    }

    public Task KeepLocalAsync(string appId, CancellationToken cancellationToken = default)
    {
        CheckAppId(appId);
        return OnSaves(client =>
        {
            client.Saves.UseAccount(client.Server, client.UserId);
            client.Saves.KeepLocal(appId, CloudState(client, appId).Archive, client, Blocked, client.Saves.CurrentEnvironment(true));
        }, true, cancellationToken);
    }

    public Task DeleteCloudAsync(string appId, CancellationToken cancellationToken = default)
    {
        CheckAppId(appId);
        return OnSaves(client =>
        {
            client.Saves.UseAccount(client.Server, client.UserId);
            client.Saves.DeleteCloud(appId, CloudState(client, appId).Archive, client);
        }, true, cancellationToken);
    }

    // LuaTools: the Ludusavi manifest through the outbound policy, with a conditional request.
    internal static SaveManifestResponse FetchManifest(string url, string etag, long maxBytes)
    {
        using var client = AppHttp.Create(TimeSpan.FromSeconds(120), responseBudget: HttpResponseBudget.LargeList);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotModified) return new SaveManifestResponse { Status = 304 };
        if (!response.IsSuccessStatusCode) return new SaveManifestResponse { Status = (int)response.StatusCode };
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Manifest too large");
        using var body = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920]; int read;
        while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("Manifest too large");
            buffer.Write(chunk, 0, read);
        }
        return new SaveManifestResponse { Status = 200, ETag = response.Headers.ETag?.ToString(), Body = buffer.ToArray() };
    }

    internal static CabinetRestorePreview ToPreview(SaveRestorePlan plan)
    {
        var items = plan.Items.Select(i => new CabinetRestoreItem(i.Action == "replace" ? CabinetRestoreAction.Replace : CabinetRestoreAction.Create, i.Destination, i.Size))
            .Concat(plan.Deletions.Select(i => new CabinetRestoreItem(CabinetRestoreAction.Delete, i.Destination, i.Size))).ToList();
        return new CabinetRestorePreview(plan.BackupPath, items, plan.Errors.Concat(plan.Unmapped).ToList(), plan.CanApply);
    }

    internal static IReadOnlyList<CabinetSaveGame> MapSaves(IEnumerable<SaveGameStatus> statuses, IReadOnlyDictionary<string, string> names, bool connected)
    {
        static DateTimeOffset? Time(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        var list = new List<CabinetSaveGame>();
        foreach (var s in statuses)
        {
            var state = s.Status switch
            {
                SaveSyncStatus.Synced => CabinetSaveState.Synced,
                SaveSyncStatus.Pending => connected ? CabinetSaveState.Pending : CabinetSaveState.NotConnected,
                SaveSyncStatus.Conflict => CabinetSaveState.Conflict,
                SaveSyncStatus.Error => CabinetSaveState.Error,
                SaveSyncStatus.Empty => CabinetSaveState.NoFiles,
                SaveSyncStatus.Ambiguous => CabinetSaveState.Ambiguous,
                SaveSyncStatus.LocalMissing => CabinetSaveState.LocalMissing,
                SaveSyncStatus.CloudDeleted => CabinetSaveState.CloudDeleted,
                SaveSyncStatus.Deferred => CabinetSaveState.Deferred,
                SaveSyncStatus.Offline => CabinetSaveState.NotConnected,
                SaveSyncStatus.Disabled => CabinetSaveState.Off,
                SaveSyncStatus.CloudOnly => CabinetSaveState.CloudOnly,
                _ => CabinetSaveState.NotFound,
            };
            // Never "Synced" without save files, whatever the journal says.
            if (state == CabinetSaveState.Synced && s.Files == 0) state = s.Discovery == SaveStatus.Empty ? CabinetSaveState.NoFiles : CabinetSaveState.NotFound;
            string name = s.Name ?? (names.TryGetValue(s.AppId, out var n) ? n : "App " + s.AppId);
            var roots = s.Roots.Select(r => new CabinetSaveRoot(r.Id, r.Source, r.Path, r.State, r.Files, r.Bytes, r.Note)).ToList();
            list.Add(new CabinetSaveGame(s.AppId, name, state, s.Discovery, roots, s.Files, s.Bytes, s.CloudFiles, s.CloudBytes,
                Time(s.LastUpload), Time(s.LastDownload), s.Error, s.OverrideMode, s.ManifestVersion, s.Notes.ToList()));
        }
        return list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

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
            try { ReconcileCloudRedirect(); }
            catch { CabinetApp.Log("CloudRedirect Cabinet configuration could not be saved"); }
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
            cloud = ReadCloudStatus();
        }
        catch { cloud = CabinetCloudStatus.Disconnected; }
        IReadOnlyList<CabinetSaveGame> saves;
        try { saves = MapSaves(CloudSync.Saves.Statuses(), games.ToDictionary(g => g.AppId, g => g.Name), cloud.Connected); }
        catch (Exception ex) { CabinetApp.Log("save overview error: " + ex.Message); saves = Array.Empty<CabinetSaveGame>(); }
        bool cloudChanged = cloud != Status.Cloud;
        Publish(new CabinetStatus(true, Playtime.SteamRunning(), false, false, games, cloud, saves));
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
