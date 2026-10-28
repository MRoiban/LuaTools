using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

public class HttpServerService : IHostedService
{
    private readonly LuaInstaller _installer;
    private readonly SteamService _steam;
    private readonly CacheService _cache;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpServerService> _log;
    private readonly SettingsService _settings;
    private volatile bool _active;

    // appid -> the queue item for its manifest download. Retained after completion so a late poll from
    // the store-page popup still sees "done" (the previous DownloadState dictionary behaved the same way).
    private readonly ConcurrentDictionary<long, Services.Downloads.DownloadItem> _downloads = new();
    private List<ApiSource> _apiSources = new();
    private bool _apiSourcesLoaded = false;

    public HttpServerService(LuaInstaller installer, SteamService steam, CacheService cache,
        IServiceProvider services, ILogger<HttpServerService> logger, SettingsService settings)
    {
        _installer = installer;
        _steam = steam;
        _cache = cache;
        _services = services;
        _log = logger;
        _settings = settings;
    }

    private void LoadApiSources()
    {
        if (_apiSourcesLoaded) return;
        _apiSourcesLoaded = true;

        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "public", "api.json"),
            Path.Combine(AppContext.BaseDirectory, "api.json"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("api_list", out var list))
                    {
                        _apiSources = new();
                        foreach (var entry in list.EnumerateArray())
                        {
                            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var url = entry.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                            var successCode = entry.TryGetProperty("success_code", out var sc) ? sc.GetInt32() : 200;
                            var enabled = !entry.TryGetProperty("enabled", out var en) || en.GetBoolean();
                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url) && enabled)
                            {
                                try
                                {
                                    new OutboundNetworkPolicy().Validate(new Uri(url.Replace("<appid>", "1")));
                                    _apiSources.Add(new ApiSource(name, url, successCode));
                                }
                                catch (HttpRequestException) { }
                                catch (UriFormatException) { }
                            }
                        }
                    }
                    _log.LogInformation("Loaded {Count} API sources from api.json", _apiSources.Count);
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Failed to parse api.json: {Message}", ex.Message);
                }
            }
        }
        _log.LogWarning("api.json not found; source catalogue unavailable");
        _apiSources = new();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _active = _settings.IsExecutableWorkflowEnabled("LuaToolsPlugin");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _active = false;
        return Task.CompletedTask;
    }

    // Internal capability: only the validated CEF connection in this process can dispatch.
    // Nothing listens on port 6767, so other Windows sessions cannot steal or receive commands.
    internal async Task<(int Status, string Body)> DispatchAsync(string method, string path,
        string requestBody, string installation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_active || !_settings.IsExecutableWorkflowEnabled("LuaToolsPlugin")
            || !string.Equals(_steam.EffectivePath, installation, StringComparison.OrdinalIgnoreCase))
            return (403, JsonErr(LuaToolsGui.Resources.Strings.SteamIsolation_ContextChanged));
        if (method is not ("GET" or "POST")) return (405, JsonErr("Method not allowed."));
        if (Encoding.UTF8.GetByteCount(requestBody) > LocalPluginBridgePolicy.MaximumBodyBytes)
            return (413, JsonErr("Bridge request exceeds its size limit."));
        path = path.TrimEnd('/');
        return path switch
        {
            var p when method == "GET" && MatchGet(p, "/has/{appid}", out var id) => await HandleHas(long.Parse(id)),
            // Steam-plugin headless add: reflects the app's real DownloadViewModel.
            var p when method == "POST" && MatchPost(p, "/add/{appid}", out var id) => HandleAdd(long.Parse(id), requestBody),
            var p when method == "GET" && MatchGet(p, "/add-status/{appid}", out var id) => HandleAddStatus(long.Parse(id)),
            var p when method == "POST" && MatchPost(p, "/add-source/{appid}", out var id) => HandleAddSource(long.Parse(id), requestBody),
            var p when method == "POST" && MatchPost(p, "/check-sources/{appid}", out var id) => await HandleCheckSources(long.Parse(id)),
            // Steam-plugin "Add all DLC": check (verify, no writes) → status → apply.
            var p when method == "POST" && MatchPost(p, "/dlc-batch/{appid}", out var id) => HandleDlcBatchCheck(long.Parse(id), requestBody),
            var p when method == "GET" && MatchGet(p, "/dlc-batch/{appid}", out var id) => HandleDlcBatchStatus(long.Parse(id)),
            var p when method == "POST" && MatchPost(p, "/dlc-batch-apply/{appid}", out var id) => HandleDlcBatchApply(long.Parse(id)),
            var p when method == "POST" && MatchPost(p, "/dlc-batch-cancel/{appid}", out var id) => HandleDlcBatchCancel(long.Parse(id)),
            var p when method == "POST" && MatchPost(p, "/download/{appid}", out var id) => HandleDownload(long.Parse(id), requestBody),
            var p when method == "GET" && MatchGet(p, "/download-status/{appid}", out var id) => HandleStatus(long.Parse(id)),
            var p when method == "POST" && MatchPost(p, "/cancel/{appid}", out var id) => HandleCancel(long.Parse(id)),
            var p when method == "POST" && MatchPost(p, "/remove/{appid}", out var id) => HandleRemove(long.Parse(id)),
            var p when method == "POST" && MatchPost(p, "/open/fix/{appid}", out var id) => HandleOpenFix(long.Parse(id)),
            "/open/settings" when method == "POST" => HandleOpenSettings(),
            "/open-url" when method == "POST" => HandleOpenUrl(requestBody),
            "/restart-steam" when method == "POST" => HandleRestartSteam(),
            "/check-updates" when method == "POST" => await HandleCheckUpdates(),
            "/loaded-apps" when method == "GET" => await HandleReadLoadedApps(),
            "/loaded-apps" when method == "POST" => HandleDismissLoadedApps(),
            "/api-list" when method == "GET" => HandleApiList(),
            "/icon" when method == "GET" => HandleIcon(),
            _ => (404, JsonErr("Not found")),
        };

    }

    private static bool MatchGet(string? path, string pattern, out string id)
    {
        id = "";
        if (path is null) return false;
        var parts = pattern.TrimEnd('/').Split('/');
        var pathParts = path.Split('/');
        if (parts.Length != pathParts.Length) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("{")) { id = pathParts[i]; continue; }
            if (!string.Equals(parts[i], pathParts[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return ApiContract.IsAppId(id);
    }

    private static bool MatchPost(string? path, string pattern, out string id) =>
        MatchGet(path, pattern, out id);

    // â”€â”€ Endpoint handlers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private Task<(int, string)> HandleHas(long appId)
    {
        var exists = _installer.ReadInstalledLua(appId) != null;
        return Task.FromResult((200, Json(new { success = true, exists })));
    }

    // â”€â”€ Steam-plugin add: drive + reflect the real DownloadViewModel â”€â”€

    /// <summary>Trigger the fully headless add (PluginAddService. Dynamic sources, Hubcap, key-gating,
    /// usage, FastFetch auto-download). Uses services only; the app window is never touched.</summary>
    private (int, string) HandleAdd(long appId, string body)
    {
        // The store page passes the game name it already displays, so PluginAddService can skip a
        // lua.tools /details lookup. Best-effort: a missing/blank name just falls back to a fetch.
        string? name = null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("name", out var n))
                name = n.GetString();
        }
        catch { }
        if (!_services.GetRequiredService<PluginAddService>().Start(appId, name))
            return (409, JsonErr("An add operation is already active for this app."));
        return (200, Json(new { success = true }));
    }

    /// <summary>Serialize the headless add state so the plugin popup mirrors what the app would show.</summary>
    private (int, string) HandleAddStatus(long appId)
    {
        var svc = _services.GetRequiredService<PluginAddService>();
        var st = svc.GetState(appId);
        bool installed = _installer.ReadInstalledLua(appId) != null;
        if (st is null)
            return (200, Json(new { success = true, checking = false, sourcesLoaded = false, sources = Array.Empty<object>(), installed }));

        var sources = st.Sources.Select(s => (object)new
        {
            name = s.Name,
            displayName = s.DisplayName,
            status = s.Status,
            available = s.Available,
            canDownload = s.CanDownload,
            locked = s.Locked,
            needsKey = s.NeedsKey,
            stats = s.Stats,
            downloading = s.Downloading,
            progress = s.Progress,
            indeterminate = s.Indeterminate,
        }).ToList();

        return (200, Json(new
        {
            success = true,
            appid = st.AppId,
            checking = st.Checking,
            fastFetch = st.FastFetch,
            sourcesLoaded = st.SourcesLoaded,
            sources,
            installStatus = st.InstallStatus,
            installFailed = st.InstallFailed,
            cancelled = st.Cancelled,
            error = st.Error,
            installed,
        }));
    }

    /// <summary>Plugin picked a source by name (FastFetch-off path) â†’ download+install it headlessly.</summary>
    private (int, string) HandleAddSource(long appId, string body)
    {
        string source = "";
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("source", out var s))
                source = s.GetString() ?? "";
        }
        catch { }
        if (string.IsNullOrWhiteSpace(source)) return (400, JsonErr("source is required"));

        if (!_services.GetRequiredService<PluginAddService>().Pick(appId, source))
            return (409, JsonErr("The source is unavailable or an operation is already active."));
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleDlcBatchCheck(long appId, string body)
    {
        // Owned ids come from the store page's own markers and can only skip DLC. Bounded and numeric.
        var owned = new List<long>();
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("owned", out var o) && o.ValueKind == JsonValueKind.Array)
                foreach (var e in o.EnumerateArray().Take(DlcBatchService.MaxOwned))
                    if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out uint v) && v > 0) owned.Add(v);
        }
        catch (JsonException) { }
        if (!_services.GetRequiredService<DlcBatchService>().Check(appId, owned))
            return (409, JsonErr("A DLC batch is already running for this game."));
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleDlcBatchStatus(long appId)
    {
        var batch = _services.GetRequiredService<DlcBatchService>().GetState(appId);
        if (batch is null) return (200, Json(new { success = true, phase = "idle", items = Array.Empty<object>() }));
        var items = batch.Items.ToList().Select(i => (object)new
        {
            appid = i.AppId, baseAppId = i.BaseAppId, name = i.Name, state = i.State, download = i.Download, source = i.Source, error = i.Error,
        }).ToList();
        return (200, Json(new { success = true, appid = batch.AppId, phase = batch.Phase, done = batch.Done, total = items.Count, items, error = batch.Error }));
    }

    private (int, string) HandleDlcBatchApply(long appId) =>
        _services.GetRequiredService<DlcBatchService>().Apply(appId)
            ? (200, Json(new { success = true }))
            : (409, JsonErr("Nothing verified to add. Check the DLC again."));

    private (int, string) HandleDlcBatchCancel(long appId) =>
        (200, Json(new { success = _services.GetRequiredService<DlcBatchService>().Cancel(appId) }));

    private async Task<(int, string)> HandleCheckSources(long appId)
    {
        // Dynamic source list from the app's real manifest backend (same call the app's
        // DownloadViewModel uses). Sources have no per-source URL. Downloads go through
        // the app's authenticated proxy by source NAME (see HandleDownload).
        try
        {
            var api = _services.GetRequiredService<LuaToolsApiClient>();
            var statuses = await api.CheckSourcesAsync(appId.ToString());
            var results = statuses
                .Select(kv => (object)new { name = kv.Key, available = kv.Value == "available", url = (string?)null })
                .ToList();
            return (200, Json(new { success = true, results }));
        }
        catch (Exception ex)
        {
            return (200, Json(new { success = false, error = ex.Message, results = Array.Empty<object>() }));
        }
    }

    private (int, string) HandleDownload(long appId, string body)
    {

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        // Download is by source NAME (the app's authenticated proxy resolves it). Accept
        // "source" or legacy "apiName".
        string source = json.TryGetProperty("source", out var s) ? s.GetString() ?? ""
            : json.TryGetProperty("apiName", out var a) ? a.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(source))
            return (400, JsonErr("source is required"));

        // The queue's DedupeKey is the real duplicate guard; this keeps the documented 409 contract.
        var queue = _services.GetRequiredService<Services.Downloads.DownloadQueue>();
        if (queue.FindActive($"manifest:{appId}") is not null)
            return (409, JsonErr("Download already in progress for this app"));

        var jobs = _services.GetRequiredService<Services.Downloads.ManifestJobFactory>();
        _downloads[appId] = queue.Enqueue(jobs.CreateManifestJob(appId, null, source, needsKey: false));

        return (200, Json(new { success = true }));
    }

    /// <summary>
    /// Project a queue item onto the EXACT JSON the store-page plugin already expects. The field names
    /// and the status vocabulary are a published contract consumed by luatools.js (via main.lua's
    /// GetAddViaLuaToolsStatus), so nothing here may drift.
    /// </summary>
    private (int, string) HandleStatus(long appId)
    {
        if (!_downloads.TryGetValue(appId, out var item))
            return (200, Json(new { success = true, state = (object?)null }));

        bool done = item.Status == Services.Downloads.DownloadStatus.Completed;
        var (bytesRead, totalBytes) = LegacyBytes(item);

        var payload = new
        {
            status = StatusWireName(item.Status),
            bytesRead,
            totalBytes,
            currentApi = item.SubTitle,
            apiErrors = (object?)null, // nothing ever populated this
            error = item.Status is Services.Downloads.DownloadStatus.Failed
                                or Services.Downloads.DownloadStatus.Cancelled ? item.Message : null,
            installedPath = (string?)null,
            success = done,
            api = done ? item.SubTitle : null,
        };
        return (200, Json(new { success = true, state = payload }));
    }

    /// <summary>
    /// The wire vocabulary. Note "failed", NOT "error": the plugin's startPolling shows its failure UI
    /// on "failed", and the old DownloadState comment claiming "error" was simply wrong.
    /// </summary>
    private static string StatusWireName(Services.Downloads.DownloadStatus s) => s switch
    {
        Services.Downloads.DownloadStatus.Queued => "queued",
        Services.Downloads.DownloadStatus.Downloading => "downloading",
        Services.Downloads.DownloadStatus.AwaitingConfirmation => "processing",
        Services.Downloads.DownloadStatus.Installing => "processing",
        Services.Downloads.DownloadStatus.Completed => "done",
        Services.Downloads.DownloadStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    /// <summary>
    /// Real byte counts when the response had a Content-Length. When it did not, fall back to exactly
    /// what this endpoint used to synthesize (0 of 100) rather than 0/0, so an unknown-length download
    /// renders no worse in the popup than it did before.
    /// </summary>
    private static (long BytesRead, long TotalBytes) LegacyBytes(Services.Downloads.DownloadItem item) =>
        item.TotalBytes is > 0 ? (item.BytesRead, item.TotalBytes.Value) : (0L, 100L);

    /// <summary>
    /// Cancel this app's in-flight manifest download.
    /// </summary>
    /// <remarks>
    /// Resolved through the queue rather than this class's own dictionary, which means it now also
    /// cancels adds started by the store-page popup's own pipeline (PluginAddService, /add/{appid}).
    /// Those share the DedupeKey "manifest:{appid}" but never touched _downloads, so before the queue
    /// existed this endpoint silently did nothing for them and the download ran on after the popup closed.
    /// </remarks>
    private (int, string) HandleCancel(long appId)
    {
        _services.GetRequiredService<PluginAddService>().Cancel(appId);
        var queue = _services.GetRequiredService<Services.Downloads.DownloadQueue>();
        var item = queue.FindActive($"manifest:{appId}");
        if (item is null) return (200, Json(new { success = true, message = "Nothing to cancel" }));

        queue.Cancel(item);
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleRemove(long appId)
    {
        try
        {
            _cache.RemoveLoadedAppId(appId); // also drop it from the "recently added" popup list
            var path = _installer.ReadInstalledLua(appId);
            if (path is not null)
            {
                File.Delete(path);
                var disabled = Path.Combine(Path.GetDirectoryName(path)!, $"{appId}.lua.disabled");
                if (File.Exists(disabled)) File.Delete(disabled);
                return (200, Json(new { success = true, deleted = new[] { path }, count = 1 }));
            }
            return (200, Json(new { success = true, deleted = Array.Empty<string>(), count = 0 }));
        }
        catch (Exception ex)
        {
            return (500, JsonErr(ex.Message));
        }
    }

    // â”€â”€ App-owned actions (surface the LuaTools GUI window; it does the real work) â”€â”€

    /// <summary>Open the Fixes page for a game (same as the fix:// protocol).</summary>
    private (int, string) HandleOpenFix(long appId)
    {
        return OnUiThread(() =>
        {
            var window = _services.GetRequiredService<MainWindow>();
            var fixes = _services.GetRequiredService<FixesViewModel>();
            window.RestoreFromTray();
            window.NavigateToFixes();
            _ = fixes.OpenForAppIdAsync(appId);
        });
    }

    /// <summary>Surface the app's own Settings page (replaces the plugin's settings panel).</summary>
    private (int, string) HandleOpenSettings()
    {
        return OnUiThread(() =>
        {
            var window = _services.GetRequiredService<MainWindow>();
            window.RestoreFromTray();
            window.NavigateToSettings();
        });
    }

    private (int, string) HandleRestartSteam()
    {
        var ok = _steam.RestartSteam();
        return (200, Json(ok
            ? new { success = true, error = (string?)null }
            : new { success = false, error = (string?)"Failed to restart Steam" }));
    }

    private (int, string) HandleOpenUrl(string body)
    {

        string url = "";
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("url", out var u))
                url = u.GetString() ?? "";
        }
        catch { /* fall through to validation */ }

        if (!SteamPluginBridge.IsAllowedExternalUrl(url))
            return (400, JsonErr("URL is not permitted by plugin policy"));

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return (200, Json(new { success = true }));
        }
        catch (Exception ex)
        {
            return (500, JsonErr(ex.Message));
        }
    }

    private Task<(int, string)> HandleCheckUpdates()
    {
        try
        {
            // Frontend "Check for updates" â†’ run the exact same update flow as Steam-open (app + plugin,
            // with the sync app-restart), so the button can't leave the backend out of sync with a freshly
            // updated plugin. Fire-and-forget: the flow may restart Steam and/or the app, so don't block the
            // HTTP response on it. Fall back to the plain checks if the app flow isn't wired yet.
            if (App.RunUpdateFlow is { } flow)
                _ = flow();
            else
            {
                _ = _services.GetRequiredService<UpdateService>().CheckAndStageAsync();
                _ = _services.GetRequiredService<PluginInstallerService>().AutoUpdateAsync();
            }
            return Task.FromResult((200, Json(new { success = true })));
        }
        catch (Exception ex)
        {
            return Task.FromResult((200, Json(new { success = false, error = ex.Message })));
        }
    }

    private async Task<(int, string)> HandleReadLoadedApps()
    {
        var ids = _cache.GetLoadedAppIds();
        // Resolve appid â†’ game name so the plugin's "Added Games" popup shows names, not just numbers
        // (it renders item.name || item.appid). Names are best-effort. A missing one falls back to the id.
        var names = _services.GetRequiredService<SteamAppListCache>();
        try { await names.EnsureLoadedAsync(); } catch { /* offline / not cached yet â†’ ids only */ }
        var apps = ids.Select(id => new { appid = id, name = names.GetName(id) }).ToList();
        return (200, Json(new { success = true, apps }));
    }

    private (int, string) HandleDismissLoadedApps()
    {
        _cache.ClearLoadedAppIds();
        return (200, Json(new { success = true }));
    }

    /// <summary>Marshal a fire-and-forget UI action onto the WPF dispatcher and ack immediately.</summary>
    private (int, string) OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return (500, JsonErr("App not ready"));
        dispatcher.InvokeAsync(() =>
        {
            try { action(); }
            catch (Exception ex) { _log.LogWarning("UI action failed: {Message}", ex.Message); }
        });
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleApiList()
    {
        LoadApiSources();
        var apis = _apiSources.Select((s, i) => new { name = s.Name, index = i }).ToList();
        return (200, Json(new { success = true, apis }));
    }

    private (int, string) HandleIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "luatools-icon.png");
            if (!File.Exists(iconPath))
            {
                var alt = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(alt))
                    iconPath = alt;
                else
                    return (200, Json(new { success = false, dataUrl = "" }));
            }
            var bytes = File.ReadAllBytes(iconPath);
            var b64 = Convert.ToBase64String(bytes);
            var mime = iconPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/x-icon";
            return (200, Json(new { success = true, dataUrl = $"data:{mime};base64,{b64}" }));
        }
        catch
        {
            return (200, Json(new { success = false, dataUrl = "" }));
        }
    }

    // â”€â”€ Download worker â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static string Json(object obj) => JsonSerializer.Serialize(obj);
    private static string JsonErr(string msg) => JsonSerializer.Serialize(new { success = false, error = msg });
}

internal record ApiSource(string Name, string Url, int SuccessCode);
