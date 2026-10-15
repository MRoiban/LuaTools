using System.Globalization;
using System.IO;
using System.Net.Http;

namespace LuaToolsGui.Services.Cabinet;

/// <summary>All client state, receipts and network work are serialized with the engine cadence.</summary>
public sealed class CabinetCloud : ICabinetCloud
{
    private readonly ICabinetSettings _settings;
    private readonly CabinetPaths _paths;
    private readonly Action _enableDebugging;
    private readonly Func<Uri, HttpClient>? _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CabinetCloudClient? _client;
    private CabinetCloudStatus _status = CabinetCloudStatus.Disconnected;
    private DateTimeOffset _nextPresence, _nextRound;
    private string? _presenceState;
    private uint _published;
    private bool _labels;
    private Func<string, long> _minutes = _ => 0;
    private Func<string, string, int, bool>? _queue;
    private Func<string, bool> _blocked = _ => true;
    private string[] _running = [];
    private string? _account;
    private bool _wasPlaying;
    private Func<CancellationToken, Task>? _engineSync;
    internal void BindEngineSync(Func<CancellationToken, Task> sync) => _engineSync = sync;
    private Func<string, string> _name = id => id;
    public CabinetCloud(ICabinetSettings settings) : this(settings, CabinetPaths.Default,
        () => PluginInstallerService.EnableCabinetDebugging(Syncer.SteamPath())) { }
    internal CabinetCloud(ICabinetSettings settings, CabinetPaths paths, Action enableDebugging, Func<Uri, HttpClient>? transport = null)
    { _settings = settings; _paths = paths; _enableDebugging = enableDebugging; _transport = transport; }
    public CabinetCloudStatus Status => Volatile.Read(ref _status);
    public event EventHandler<CabinetCloudStatus>? StatusChanged;
    public bool FriendLabels
    {
        get => _labels && Status.Connected;
        set
        {
            _gate.Wait();
            try
            {
                if (value && (!_settings.CabinetEnabled || !Status.Connected)) return;
                if (value && !_labels) _enableDebugging(); // Explicit toggle is the only marker creation path.
                _labels = value;
                if (value) WorkshopEngine.AtomicWrite(Path.Combine(_paths.DataRoot, "cloud-friend-labels.txt"), "1");
                else { var file = Path.Combine(_paths.DataRoot, "cloud-friend-labels.txt"); CabinetFiles.EnsureSafePath(file); File.Delete(file); }
                _nextPresence = default;
                if (!value) global::LuaToolsGui.Services.Cabinet.FriendLabels.Clear();
            }
            finally { _gate.Release(); }
        }
    }
    private CabinetCloudClient Client()
    {
        if (_client is not null) return _client;
        _paths.ImportStandalone();
        var labelConsent = Path.Combine(_paths.DataRoot, "cloud-friend-labels.txt");
        CabinetFiles.EnsureSafePath(labelConsent); _labels = File.Exists(labelConsent);
        _client = new CabinetCloudClient(_paths.DataRoot, _paths.OnlineFixRoot, id => _minutes(id), _ => { });
        if (_transport is not null) _client.Transport = _transport;
        return _client;
    }
    private void Publish()
    {
        var status = _client?.Connected == true ? new CabinetCloudStatus(true, global::LuaToolsGui.Services.Cabinet.FriendLabels.CleanName(_client.UserName), new Uri(_client.Server).Host, _status.LastSync) : CabinetCloudStatus.Disconnected;
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
    public async Task ConnectAsync(string link, CancellationToken cancellationToken = default)
    {
        if (!_settings.CabinetEnabled) return;
        // Validate before opening files; all errors are fixed/localized by the UI.
        CabinetCloudClient.ParseLink(link, out _, out _);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var context = _paths.Enter();
            var client = Client(); client.Cancellation = cancellationToken;
            await Task.Run(() => client.Connect(link), cancellationToken).ConfigureAwait(false);
            WorkshopEngine.AtomicWrite(Path.Combine(_paths.DataRoot, "cloud-consent.txt"), "1");
            Volatile.Write(ref _status, Status with { LastSync = null });
            _nextRound = _nextPresence = default; _presenceState = null; global::LuaToolsGui.Services.Cabinet.FriendLabels.Clear();
            Publish();
        }
        finally { _gate.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var context = _paths.Enter();
            _labels = false;
            var labels = Path.Combine(_paths.DataRoot, "cloud-friend-labels.txt"); CabinetFiles.EnsureSafePath(labels); File.Delete(labels);
            global::LuaToolsGui.Services.Cabinet.FriendLabels.Clear();
            // Local revocation always succeeds even when the server is unavailable.
            try { if (_client is not null) _client.Cancellation = cancellationToken; if (_client?.Connected == true && _published != 0) await Task.Run(() => DeletePresence(), CancellationToken.None).ConfigureAwait(false); }
            catch { }
            _client?.Disconnect();
            var consent = Path.Combine(_paths.DataRoot, "cloud-consent.txt");
            CabinetFiles.EnsureSafePath(consent); File.Delete(consent);
            _published = 0; _presenceState = null; Publish();
        }
        finally { _gate.Release(); }
    }
    private void DeletePresence()
    {
        if (_published == 0 || _client?.Connected != true) return;
        _client.RequestJson("DELETE", "/api/presence?accountId=" + _published.ToString(CultureInfo.InvariantCulture), null!);
        _published = 0;
    }
    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        // The engine owns the non-thread-safe playtime ledger. Public manual sync must take
        // its gate as well; cloud's own gate alone cannot protect ledger provenance.
        if (_engineSync is not null) { await _engineSync(cancellationToken).ConfigureAwait(false); return; }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { using var context = _paths.Enter(); await Task.Run(() => Sync(true, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    internal void Tick(Playtime playtime, string? account, HashSet<string> running, Func<string, string> name, Func<string, bool> blocked, bool manual, CancellationToken cancellation)
    {
        _gate.Wait(cancellation);
        try
        {
            if (_wasPlaying && running.Count == 0) _nextRound = default;
            _wasPlaying = running.Count > 0;
            _account = account; _running = running.OrderBy(x => x, StringComparer.Ordinal).ToArray(); _name = name; _blocked = blocked;
            _queue = playtime.QueueCloudMinutes;
            _minutes = id =>
            {
                if (account is null) return 0;
                var path = Path.Combine(Syncer.SteamPath(), "userdata", account, "config", "localconfig.vdf");
                long value = 0;
                if (File.Exists(path)) long.TryParse(new VdfText(CabinetFiles.ReadText(path)).GetAppValue(id, "Playtime"), out value);
                return Math.Max(0, value - playtime.CloudMinutesInSteam(id));
            };
            Sync(manual, cancellation);
        }
        finally { _gate.Release(); }
    }
    private void Sync(bool manual, CancellationToken cancellation)
    {
        if (!_settings.CabinetEnabled || _paths.StandaloneRunning()) { global::LuaToolsGui.Services.Cabinet.FriendLabels.Clear(); return; }
        if (_client is null)
        {
            var consent = Path.Combine(_paths.DataRoot, "cloud-consent.txt");
            CabinetFiles.EnsureSafePath(consent);
            if (!File.Exists(consent)) return; // Imported credentials never silently opt in.
            Client(); Publish();
        }
        if (!_client!.Connected) return;
        _client.Cancellation = cancellation;
        uint.TryParse(_account, out var own);
        var app = _running.FirstOrDefault();
        string state = own + ":" + app;
        var now = DateTimeOffset.UtcNow;
        if (manual || state != _presenceState || now >= _nextPresence)
        {
            _nextPresence = now.AddSeconds(60);
            try
            {
                if (_published != 0 && (_published != own || app is null)) DeletePresence();
                if (own != 0 && app is not null)
                    _client.RequestJson("PUT", "/api/presence", new { accountId = own, appId = app, name = global::LuaToolsGui.Services.Cabinet.FriendLabels.CleanName(_name(app)) });
                _published = app is null ? 0 : own; _presenceState = state;
            }
            catch (OperationCanceledException) { throw; }
            catch { PluginLog.Log("Cabinet presence deferred"); } // Never log credentials or server bodies.
            try
            {
                if (_labels && _paths.SteamRunning()) global::LuaToolsGui.Services.Cabinet.FriendLabels.Update(_client.RequestJson("GET", "/api/presence", null!), own);
                else global::LuaToolsGui.Services.Cabinet.FriendLabels.Clear();
            }
            catch (OperationCanceledException) { throw; }
            catch { PluginLog.Log("Cabinet friend labels deferred"); } // Existing labels expire.
        }
        if (_queue is not null && _running.Length == 0 && (manual || now >= _nextRound))
        {
            _nextRound = now.AddMinutes(30);
            try
            {
                _client.Deliver(_queue, _blocked);
                _client.Round(_blocked);
                _client.Deliver(_queue, _blocked);
                Volatile.Write(ref _status, Status with { LastSync = now });
                Publish();
            }
            catch (OperationCanceledException) { throw; }
            catch { _nextRound = now.AddMinutes(1); PluginLog.Log("Cabinet cloud sync deferred"); }
        }
    }
    internal void Suspend() { global::LuaToolsGui.Services.Cabinet.FriendLabels.Clear(); _presenceState = null; }
}
