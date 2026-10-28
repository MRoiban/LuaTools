using System.Collections.Concurrent;
using System.Windows;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services;

/// <summary>
/// Headless add pipeline for the Steam store plugin. Mirrors the logic of
/// <c>DownloadViewModel.FetchAsync</c>/<c>DownloadFromSourceAsync</c>. Dynamic sources, synthesized
/// Hubcap/Sadie source, key-gating, usage badges, FastFetch auto-download, but uses only the underlying
/// SERVICES (never the UI view model), so the app window stays silent. The plugin polls state via the
/// HTTP server; when FastFetch is off it picks a source with <see cref="Pick"/>.
/// </summary>
public class PluginAddService(
    LuaToolsApiClient api,
    HubcapService hubcap,
    SettingsService settings,
    AuthService auth,
    DownloadQueue queue,
    ManifestJobFactory jobs)
{
    private const string HubcapSourceName = "Sadie (Morrenus)";
    /// <summary>The single row a DLC gets: lua.tools generates its unlock lua (no manifest sources).</summary>
    internal const string DlcSourceName = "lua.tools DLC";

    public class SourceRow
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public bool NeedsKey { get; set; }
        public bool Locked { get; set; }
        public string? Stats { get; set; }
        public bool Downloading { get; set; }
        public double Progress { get; set; }
        public bool Indeterminate { get; set; }
        public bool Available => Status == "available";
        public bool CanDownload => Available && !Locked;
    }

    public class AddState
    {
        public long AppId;
        public string? GameName;
        public bool Checking;
        public bool FastFetch;
        public bool SourcesLoaded;
        public List<SourceRow> Sources = [];
        public string? InstallStatus;
        public bool InstallFailed;
        public string? Error;
        public bool Busy; // a download+install is running
        internal string? DlcBaseAppId; // set when the app is DLC: downloads generate an unlock lua
        public bool Cancelled;
        internal DownloadItem? QueueItem;
        internal Task CheckTask = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<long, AddState> _states = new();
    private readonly object _gate = new();

    private bool IsCurrent(AddState state) => !state.Cancelled
        && _states.TryGetValue(state.AppId, out var current) && ReferenceEquals(current, state);

    public AddState? GetState(long appId) => _states.TryGetValue(appId, out var s) ? s : null;

    /// <summary>Begin a headless add: check sources (+ Hubcap synth + gating + usage) and, if FastFetch
    /// is on, auto-download the best source. FastFetch off leaves the sources for the plugin to pick.</summary>
    public bool Start(long appId, string? gameName = null)
    {
        var state = new AddState
        {
            AppId = appId,
            Checking = true,
            FastFetch = settings.FastFetch,
            // The store page usually passes the name it already shows, letting CheckAsync skip a
            // lua.tools /details round-trip. Null/blank → CheckAsync fetches it as a fallback.
            GameName = string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim(),
        };
        lock (_gate)
        {
            if (_states.TryGetValue(appId, out var current) && (current.Checking || current.Busy))
                return false;
            _states[appId] = state;
        }
        PluginLog.Log($"PluginAdd.Start appid={appId} fastFetch={state.FastFetch} guest={auth.IsGuest}");
        state.CheckTask = Task.Run(() => CheckAsync(appId, state));
        return true;
    }

    /// <summary>Plugin picked a source (FastFetch-off path) → download+install it.</summary>
    public bool Pick(long appId, string sourceName)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(appId, out var state) || !IsCurrent(state)
                || state.Checking || state.Busy || !state.SourcesLoaded)
                return false;
            var row = state.Sources.FirstOrDefault(r =>
                string.Equals(r.Name, sourceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.DisplayName, sourceName, StringComparison.OrdinalIgnoreCase));
            if (row?.CanDownload != true) return false;
            state.Busy = true; // reserve admission before another Pick or Start can race us
            _ = Task.Run(() => DownloadAsync(appId, state, row, reserved: true));
            return true;
        }
    }

    /// <summary>Invalidate this generation before cancelling its queue item. Late checks cannot enqueue.</summary>
    public bool Cancel(long appId)
    {
        DownloadItem? item;
        lock (_gate)
        {
            if (!_states.TryGetValue(appId, out var state) || state.Cancelled
                || state.QueueItem is { IsActive: false }
                || (!state.Checking && !state.Busy && !string.IsNullOrEmpty(state.InstallStatus)))
                return false;
            state.Cancelled = true;
            state.Checking = false;
            state.Error = null;
            state.InstallFailed = false;
            state.InstallStatus = null;
            foreach (var row in state.Sources)
            {
                row.Downloading = false;
                row.Indeterminate = false;
            }
            item = state.QueueItem;
        }
        // Queue calls dispatch to WPF: never hold _gate while waiting on its dispatcher.
        if (item is not null) queue.Cancel(item);
        return true;
    }

    private async Task CheckAsync(long appId, AddState state)
    {
        try
        {
            // DLC never has manifests of its own on the sources. Mirror DownloadViewModel.FetchAsync,
            // which branches on the app type before any source lookup.
            var details = await SafeGetDetailsAsync(appId);
            if (details?.IsDlc == true)
            {
                await CheckDlcAsync(appId, state, details);
                return;
            }
            state.GameName ??= details?.Name;

            string? key = settings.HubcapApiKey;

            // Hubcap/Sadie is the priority source. When a key is set, check ITS availability first, and in
            // FastFetch, if Hubcap can serve this game, download straight from it and skip the lua.tools
            // CheckSources call entirely (a lua.tools source would never be picked over Hubcap anyway).
            bool hubcapAvailable = false;
            if (!string.IsNullOrEmpty(key))
            {
                var hs = await hubcap.CheckStatusAsync(key, appId.ToString());
                hubcapAvailable = hs?.ManifestFileExists == true;
            }

            if (state.FastFetch && hubcapAvailable)
            {
                var stats = await hubcap.GetStatsAsync(key!);
                if (stats?.CanMakeRequests == true)
                {
                    var hubMeta = SourceMeta.Get(HubcapSourceName);
                    var hubRow = new SourceRow
                    {
                        Name = HubcapSourceName,
                        DisplayName = hubMeta.DisplayName ?? HubcapSourceName,
                        Status = "available",
                        NeedsKey = true,
                        Stats = $"{stats.DailyUsage}/{stats.DailyLimit}",
                    };
                    if (!PublishSources(state, new List<SourceRow> { hubRow }, appId)) return;
                    await DownloadAsync(appId, state, hubRow);
                    return;
                }
                // Hubcap is out of daily quota → fall through and let a lua.tools source take over.
            }

            Dictionary<string, string> statuses;
            try { statuses = await api.CheckSourcesAsync(appId.ToString()); }
            catch (ApiException) { statuses = []; }

            // Synthesize the Hubcap/Sadie row from the availability already checked above (or "unknown"
            // when no key is set, so it shows locked with the "needs a key" hint).
            statuses[HubcapSourceName] = string.IsNullOrEmpty(key)
                ? "unknown"
                : (hubcapAvailable ? "available" : "unavailable");

            // Premium (key-gated) sources first, mirroring the website/app ordering.
            var rows = statuses
                .OrderByDescending(kv => SourceMeta.Get(kv.Key).RequiresUserKey ? 1 : 0)
                .Select(kv =>
                {
                    var meta = SourceMeta.Get(kv.Key);
                    return new SourceRow
                    {
                        Name = kv.Key,
                        DisplayName = meta.DisplayName ?? kv.Key,
                        Status = kv.Value,
                        NeedsKey = meta.RequiresUserKey,
                    };
                }).ToList();

            // Premium rows stay locked until quota is known. Keyed rows get their
            // real lock state from the Hubcap stats call in FillBadgesAsync.
            var keyRows = rows.Where(r => r.NeedsKey).ToList();
            foreach (var r in keyRows) r.Locked = true;

            if (state.FastFetch)
            {
                // Reached only when Hubcap couldn't serve it (no key / not on Hubcap / out of quota). No
                // picker, so resolve just the Hubcap lock that CanDownload needs (skip the cosmetic X/25
                // badges), then pick the best remaining source.
                if (keyRows.Count > 0 && !string.IsNullOrEmpty(key))
                    await FillHubcapBadgeAsync(keyRows, key);
                if (!PublishSources(state, rows, appId)) return;

                var best = rows.FirstOrDefault(r => r.CanDownload);
                if (best is null)
                {
                    lock (_gate)
                        if (IsCurrent(state)) { state.Checking = false; state.Error = Resources.Strings.Err_NoSourceAvailable; }
                    return;
                }
                await DownloadAsync(appId, state, best);
                return;
            }

            // FastFetch off: publish the picker as soon as availability is known, then lazy-fill the usage
            // badges. The plugin polls state (~350ms), so the "12/25" / "X/Unlimited" counters and the real
            // Hubcap lock pop in on the next tick instead of blocking the whole list.
            if (!PublishSources(state, rows, appId)) return;
            await FillBadgesAsync(rows, keyRows, key);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!IsCurrent(state)) return;
                state.Error = ex.Message;
                state.Checking = false;
            }
            PluginLog.Log($"PluginAdd.Check appid={appId} EXCEPTION: {ex}");
        }
    }

    /// <summary>DLC route: the parent game Steam reports for THIS DLC, then /api/dlc/info (signed out,
    /// the same check against the base game's SteamML archive).
    /// Generation is offered when keys exist or nothing is missing, the same rule as the Add page.</summary>
    private async Task CheckDlcAsync(long appId, AddState state, GameDetails details)
    {
        string? error = null;
        if (!ApiContract.IsAppId(details.BaseAppId)) error = Resources.Strings.Add_Err_BaseGame;
        if (error is not null)
        {
            PluginLog.Log($"PluginAdd.Dlc appid={appId} base={(ApiContract.IsAppId(details.BaseAppId) ? details.BaseAppId : "invalid")} rejected guest={auth.IsGuest}");
            lock (_gate)
                if (IsCurrent(state)) { state.Checking = false; state.Error = error; }
            return;
        }

        string baseAppId = details.BaseAppId!;
        var info = await api.GetDlcInfoAsync(appId.ToString(), baseAppId);
        bool canGenerate = info is not null && (info.HaveCount > 0 || info.MissingCount == 0);
        PluginLog.Log($"PluginAdd.Dlc appid={appId} base={baseAppId} depots={info?.DepotCount} have={info?.HaveCount} missing={info?.MissingCount} canGenerate={canGenerate}");

        state.GameName ??= details.Name;
        lock (_gate) state.DlcBaseAppId = baseAppId;
        var row = new SourceRow
        {
            Name = DlcSourceName,
            DisplayName = Resources.Strings.Downloads_Kind_Dlc,
            Status = canGenerate ? "available" : "unavailable",
        };
        if (!PublishSources(state, [row], appId)) return;
        if (!state.FastFetch) return;
        if (!canGenerate)
        {
            lock (_gate)
                if (IsCurrent(state)) { state.Checking = false; state.Error = Resources.Strings.Err_NoSourceAvailable; }
            return;
        }
        await DownloadAsync(appId, state, row);
    }

    private bool PublishSources(AddState state, List<SourceRow> rows, long appId)
    {
        lock (_gate)
        {
            if (!IsCurrent(state)) return false;
            state.Sources = rows;
            state.SourcesLoaded = true;
            // Keep FastFetch checking until download admission, closing the Start/Pick race.
            state.Checking = state.FastFetch;
        }
        PluginLog.Log($"PluginAdd.Check appid={appId} sources=[{string.Join(", ", rows.Select(r => $"{r.Name}({r.Status},lock={r.Locked})"))}] fastFetch={state.FastFetch}");
        return true;
    }

    private async Task<GameDetails?> SafeGetDetailsAsync(long appId)
    {
        try { return await api.GetDetailsAsync(appId.ToString()); } catch { return null; }
    }

    /// <summary>Fill the usage-counter badges on the (already-published) rows, best-effort and
    /// concurrently: the Hubcap "12/25" badge + real lock state, and the standard "X/25" badge. A failure
    /// leaves the affected rows without a badge. The picker stays fully usable. Row fields are mutated in
    /// place; the status poll re-serializes them, so the badges appear on the next tick.</summary>
    private async Task FillBadgesAsync(List<SourceRow> rows, List<SourceRow> keyRows, string? key)
    {
        var hubcapTask = keyRows.Count > 0 && !string.IsNullOrEmpty(key)
            ? FillHubcapBadgeAsync(keyRows, key!)
            : Task.CompletedTask;
        var standardTask = !auth.IsGuest ? FillStandardBadgeAsync(rows) : Task.CompletedTask;
        await Task.WhenAll(hubcapTask, standardTask);
    }

    /// <summary>Hubcap "12/25" usage badge + lock state on the key-gated rows.</summary>
    private async Task FillHubcapBadgeAsync(List<SourceRow> keyRows, string key)
    {
        try
        {
            var stats = await hubcap.GetStatsAsync(key);
            foreach (var r in keyRows)
            {
                r.Locked = stats?.CanMakeRequests != true;
                if (stats is not null) r.Stats = $"{stats.DailyUsage}/{stats.DailyLimit}";
            }
        }
        catch { }
    }

    /// <summary>Standard "X/25" (or "X/Unlimited" for supporters) usage badge on the non-key rows.</summary>
    private async Task FillStandardBadgeAsync(List<SourceRow> rows)
    {
        try
        {
            var usageTask = api.GetStandardUsageAsync();
            var supporterTask = api.GetSupporterStatusAsync();
            await Task.WhenAll(usageTask, supporterTask);
            var usage = usageTask.Result;
            bool isSupporter = supporterTask.Result?.IsSupporter == true;
            foreach (var r in rows.Where(r => !r.NeedsKey && r.Name != SteamMlService.SourceName && r.Name != DexLuaService.SourceName))
                if (usage is not null)
                    r.Stats = isSupporter ? $"{usage.Used} / Unlimited" : $"{usage.Used}/{usage.Limit}";
        }
        catch { }
    }

    /// <summary>
    /// Queue the pick through the shared <see cref="DownloadQueue"/> and mirror its progress into this
    /// service's plain-POCO state, which the store-page popup polls over HTTP.
    /// </summary>
    /// <remarks>
    /// The download and install themselves live in <c>ManifestJobFactory</c>, the same code path the Add
    /// page uses. Mirroring (rather than data binding) is correct here: <see cref="SourceRow"/> is
    /// serialized to JSON on each poll, so it only needs to hold the latest values.
    ///
    /// Routing through the queue also makes the popup's Cancel button work for the first time: it posts
    /// to /cancel/{appid}, which now resolves the same queue item this method enqueued.
    /// </remarks>
    private async Task DownloadAsync(long appId, AddState state, SourceRow row, bool reserved = false)
    {
        lock (_gate)
        {
            if (!IsCurrent(state))
            {
                if (reserved) state.Busy = false;
                return;
            }
            if ((!reserved && state.Busy) || !row.CanDownload) return;
            state.Busy = true;
            state.Checking = false;
            state.Error = null;
            state.InstallStatus = null;
            state.InstallFailed = false;
            row.Downloading = true;
            row.Indeterminate = true;
            row.Progress = 0;
        }

        try
        {
            DownloadItem? Enqueue()
            {
                // This executes on the queue's dispatcher, so enqueue does not wait on WPF
                // while holding _gate. Cancellation either sees the item or prevents its creation.
                lock (_gate)
                {
                    if (!IsCurrent(state)) return null;
                    var job = state.DlcBaseAppId is { } baseAppId
                        ? jobs.CreateDlcJob(appId, baseAppId, state.GameName)
                        : jobs.CreateManifestJob(appId, state.GameName, row.Name, row.NeedsKey);
                    return state.QueueItem = queue.Enqueue(job);
                }
            }
            var item = Application.Current is { } app
                ? await app.Dispatcher.InvokeAsync(Enqueue)
                : Enqueue();
            if (item is null) return;

            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                lock (_gate)
                {
                    if (!IsCurrent(state)) return;
                    row.Indeterminate = item.IsIndeterminate;
                    row.Progress = item.Percent;
                    row.Downloading = item.IsActive;
                }
            }
            item.PropertyChanged += OnChanged;
            try
            {
                var result = await item.Completion;
                lock (_gate)
                {
                    if (!IsCurrent(state)) return;
                    if (item.Status == DownloadStatus.Cancelled)
                    {
                        state.Cancelled = true;
                        state.Error = null;
                        state.InstallFailed = false;
                    }
                    else if (result is null || !result.Ok)
                    {
                        state.Error = result?.Message ?? item.Message;
                        state.InstallFailed = true;
                    }
                    else
                    {
                        state.InstallStatus = result.Message
                            + " " + string.Format(Resources.Strings.Add_FastFetch_Via, row.Name);
                    }
                }
            }
            finally { item.PropertyChanged -= OnChanged; }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (IsCurrent(state))
                {
                    state.Error = ex.Message;
                    state.InstallFailed = true;
                }
            }
            PluginLog.Log($"PluginAdd.Download appid={appId} source='{row.Name}' EXCEPTION: {ex}");
        }
        finally
        {
            lock (_gate)
            {
                row.Downloading = false;
                row.Indeterminate = false;
                state.Busy = false;
            }
        }
    }
}
