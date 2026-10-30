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
    ManifestJobFactory jobs, AcquisitionCoordinator? acquisition = null)
{
    private readonly AcquisitionCoordinator _acquisition = acquisition ?? new(api, hubcap, settings, auth, queue, jobs);
    private const string HubcapSourceName = "Sadie (Morrenus)";
    /// <summary>Legacy DLC source; signed-out generation uses SteamML.</summary>
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
        public string[] DisabledReasons { get; set; } = [];
        public bool IsSupporter { get; set; }
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
            var result = await _acquisition.CheckAsync(appId, state.FastFetch);
            state.GameName ??= result.Details?.Name;
            state.DlcBaseAppId = result.Details?.IsDlc == true ? result.Details.BaseAppId : null;
            if (!PublishSources(state, result.Sources, appId)) return;
            if (state.FastFetch)
            {
                var best = result.Sources.FirstOrDefault(r => r.CanDownload);
                if (best is null) { state.Checking = false; state.Error = Resources.Strings.Err_NoSourceAvailable; }
                else await DownloadAsync(appId, state, best);
            }
        }
        catch (Exception ex)
        {
            lock (_gate) if (IsCurrent(state)) { state.Checking = false; state.Error = ex is ApiException ? ex.Message : ex is Agent.MutationRejectedException { Code: "BASE_APP_REQUIRED" } ? Resources.Strings.Add_Err_BaseGame : Resources.Strings.Add_Err_Generic; }
            PluginLog.Error($"PluginAdd.Check appid={appId} failed", ex);
        }
    }

    internal Task<List<SourceRow>> CheckDlcSourcesAsync(string appId, string baseAppId, bool fastFetch) =>
        _acquisition.CheckDlcSourcesAsync(appId, baseAppId, fastFetch);

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
                    return state.QueueItem = _acquisition.Enqueue(appId, state.GameName, row, state.DlcBaseAppId);
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
            PluginLog.Error($"PluginAdd.Download appid={appId} source='{row.Name}' failed", ex);
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
