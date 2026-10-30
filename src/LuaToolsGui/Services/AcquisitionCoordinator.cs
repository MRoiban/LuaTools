using LuaToolsGui.Models;
using LuaToolsGui.Services.Agent;
using LuaToolsGui.Services.Downloads;
using SourceRow = LuaToolsGui.Services.PluginAddService.SourceRow;

namespace LuaToolsGui.Services;

/// <summary>Shared read-only source policy and queue admission for Add, the Steam picker and agents.
/// Provider HTTP and install logic remain in the existing services/factory.</summary>
public sealed class AcquisitionCoordinator(LuaToolsApiClient api, HubcapService hubcap,
    SettingsService settings, AuthService auth, DownloadQueue queue, ManifestJobFactory jobs, LuaInstaller? installer = null)
{
    private readonly Func<long, string?>? _readInstalled = installer is null ? null : installer.ReadInstalledLua;
    public const string HubcapSource = "Sadie (Morrenus)";
    public sealed record SourceSnapshot(GameDetails? Details, List<SourceRow> SourcesList)
    {
        public List<SourceRow> Sources => SourcesList;
    }
    public async Task<SourceSnapshot> CheckAsync(long appId, bool fastFetch = false, CancellationToken ct = default)
    {
        ApiContract.RequireAppId(appId.ToString());
        GameDetails? details;
        try { details = api is null ? null : await api.GetDetailsAsync(appId.ToString(), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { details = null; }
        if (details?.IsDlc == true)
        {
            if (!ApiContract.IsAppId(details.BaseAppId)) throw new MutationRejectedException("BASE_APP_REQUIRED", 6);
            return new(details, await CheckDlcSourcesAsync(appId.ToString(), details.BaseAppId!, fastFetch, ct));
        }
        string? key = settings.HubcapApiKey;
        var hs = string.IsNullOrEmpty(key) ? null : await hubcap.CheckStatusAsync(key, appId.ToString(), ct);
        var premium = new SourceRow { Name = HubcapSource, DisplayName = SourceMeta.Get(HubcapSource).DisplayName ?? HubcapSource,
            NeedsKey = true, Locked = true, Status = hs is null ? "unknown" : hs.ManifestFileExists ? "available" : "unavailable" };
        await RefreshUsageAsync([premium], ct);
        // The GUI/plugin fast path never probes lower priority providers after eligible Hubcap.
        if (fastFetch && premium.CanDownload) return new(details, [premium]);
        Dictionary<string, string> statuses;
        try { statuses = await api!.CheckSourcesAsync(appId.ToString(), ct); }
        catch (ApiException) { statuses = []; }
        var rows = statuses.Where(k => k.Key != HubcapSource).Select(k => new SourceRow
        { Name = k.Key, DisplayName = SourceMeta.Get(k.Key).DisplayName ?? k.Key, Status = k.Value,
            NeedsKey = SourceMeta.Get(k.Key).RequiresUserKey }).ToList();
        rows.Insert(0, premium);
        await RefreshUsageAsync(rows, ct);
        return new(details, rows.OrderByDescending(r => r.NeedsKey).ToList());
    }
    public async Task RefreshUsageAsync(IEnumerable<SourceRow> sources, CancellationToken ct = default)
    {
        var rows = sources.ToList();
        if (rows.Any(r => r.NeedsKey))
        {
            string? key = settings.HubcapApiKey;
            var probe = string.IsNullOrEmpty(key) ? null : await hubcap.ProbeStatsAsync(key, ct);
            var stats = probe?.Stats;
            foreach (var row in rows.Where(r => r.NeedsKey))
            {
                string? reason = string.IsNullOrEmpty(key) ? "HUBCAP_KEY_REQUIRED" : probe?.DisabledReason;
                row.Locked = reason is not null;
                row.DisabledReasons = (row.Status != "available" ? new[] { row.Status == "unknown" ? "SOURCE_UNKNOWN" : "SOURCE_UNAVAILABLE" } : []).Concat(reason is null ? [] : new[] { reason }).ToArray();
                row.Stats = stats is null ? null : $"{stats.DailyUsage}/{stats.DailyLimit}";
            }
        }
        var standard = rows.Where(r => !r.NeedsKey && !IsPublic(r.Name)).ToList();
        StandardUsage? usage = null;
        bool supporter = false;
        if (!auth.IsGuest && standard.Count > 0)
        {
            try { usage = await api.GetStandardUsageAsync(ct); supporter = (await api.GetSupporterStatusAsync(ct))?.IsSupporter == true; }
            catch (ApiException) { }
        }
        foreach (var row in rows.Where(r => !r.NeedsKey))
        {
            var reasons = new List<string>();
            if (row.Status != "available") reasons.Add(row.Status == "unknown" ? "SOURCE_UNKNOWN" : "SOURCE_UNAVAILABLE");
            if (!IsPublic(row.Name))
            {
                if (auth.IsGuest) reasons.Add("LOGIN_REQUIRED");
                else if (usage is null) reasons.Add("USAGE_UNAVAILABLE");
                else if (!supporter && usage.Used >= usage.Limit) reasons.Add("QUOTA_EXHAUSTED");
                row.IsSupporter = supporter;
                row.Stats = usage is null ? null : supporter ? $"{usage.Used} / {Resources.Strings.Add_Unlimited}" : $"{usage.Used}/{usage.Limit}";
            }
            row.DisabledReasons = reasons.ToArray(); row.Locked = reasons.Count > 0;
        }
    }
    public static bool IsPublic(string source) => source is SteamMlService.SourceName or DexLuaService.SourceName;
    public async Task<List<SourceRow>> CheckDlcSourcesAsync(string appId, string baseAppId, bool fastFetch, CancellationToken ct = default)
    {
        async Task<string> Status(Func<Task<DlcInfo?>> read, bool suppressErrors = true)
        {
            try { var info = await read(); return info is not null && (info.HaveCount > 0 || info.MissingCount == 0) ? "available" : "unavailable"; }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { return "unavailable"; }
            catch (Exception ex) when (suppressErrors && ex is (ApiException or System.Net.Http.HttpRequestException or OperationCanceledException)) { return "unknown"; }
        }
        var dex = new SourceRow { Name = DexLuaService.SourceName, DisplayName = DexLuaService.SourceName,
            Status = await Status(async () => await api.GetDexLuaDlcInfoAsync(appId, baseAppId, ct)) };
        if (fastFetch && dex.CanDownload) return [dex];
        var legacy = new SourceRow { Name = PluginAddService.DlcSourceName, DisplayName = auth.IsGuest ? SteamMlService.SourceName : "lua.tools",
            Status = await Status(() => api.GetDlcInfoAsync(appId, baseAppId, ct), dex.CanDownload) };
        // Legacy DLC is deliberately public for guests (SteamML), unlike standard manifests.
        foreach (var row in new[] { dex, legacy })
        { row.DisabledReasons = row.Status == "available" ? [] : ["SOURCE_UNAVAILABLE"]; row.Locked = row.DisabledReasons.Length > 0; }
        return [dex, legacy];
    }
    public DownloadItem Enqueue(long appId, string? name, SourceRow source, string? baseAppId = null,
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? confirm = null,
        Action<DownloadItem, JobResult?>? onFinished = null, Action? onReveal = null, Func<string>? installVersion = null)
        => EnqueueCaptured(installer?.CaptureInstallation(), appId, name, source, baseAppId, confirm, onFinished, onReveal, installVersion);

    internal DownloadItem EnqueueCaptured(InstallationBinding? installation, long appId, string? name, SourceRow source, string? baseAppId = null,
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? confirm = null,
        Action<DownloadItem, JobResult?>? onFinished = null, Action? onReveal = null, Func<string>? installVersion = null, bool reuseActive = false)
    {
        if (installation is { IsCurrent: false }) throw new MutationRejectedException("CONTEXT_CHANGED", 6);
        using var pinned = installation?.Pin();
        if (!source.CanDownload) throw new MutationRejectedException(source.DisabledReasons.FirstOrDefault() ?? "SOURCE_UNAVAILABLE", 6);
        var job = baseAppId is not null ? jobs.CreateDlcJob(appId, baseAppId, name, onFinished, onReveal, source.Name)
            : jobs.CreateManifestJob(appId, name, source.Name, source.NeedsKey, confirm, onFinished, onReveal);
        string CurrentVersion() => _readInstalled?.Invoke(appId) is { } path ? LuaVault.HashFile(path) : "missing";
        string acceptedVersion = CurrentVersion();
        job = job with { ConfirmAsync = async (file, item, ct) =>
        {
            if (installation is { IsCurrent: false }) return false;
            if (confirm is null) return CurrentVersion() == acceptedVersion;
            // A GUI decision describes the target visible when its diff opens. Remote agents supply
            // their own version binding and can request a new decision after acquisition.
            string before = CurrentVersion();
            if (!await confirm(file, item, ct) || installation is { IsCurrent: false }) return false;
            if (installVersion is null && CurrentVersion() != before) return false;
            acceptedVersion = CurrentVersion();
            return true;
        }};
        if (_readInstalled is not null || installVersion is not null)
        {
            var install = job.InstallAsync;
            job = job with { InstallAsync = (file, item, ct) =>
            {
                lock (LuaInstaller.InstallGate)
                {
                    if (installation is { IsCurrent: false }) return Task.FromResult(new JobResult(false, Resources.Strings.Agent_Add_GateChanged));
                    using var selected = installation?.Pin();
                    if (CurrentVersion() != (installVersion?.Invoke() ?? acceptedVersion)) return Task.FromResult(new JobResult(false, Resources.Strings.Agent_Add_GateChanged));
                    return install(file, item, ct);
                }
            }};
        }
        var original = job.DownloadAsync;
        job = job with { DownloadAsync = async (item, progress, ct) =>
        {
            if (installation is { IsCurrent: false }) throw new DownloadAbortedException(Resources.Strings.Agent_Add_GateChanged);
            // A queued job is never authorized by a stale GUI badge. Recheck current credentials/usage.
            var fresh = baseAppId is null ? (await CheckAsync(appId, false, ct)).Sources
                : await CheckDlcSourcesAsync(appId.ToString(), baseAppId, false, ct);
            var selected = fresh.FirstOrDefault(r => r.Name == source.Name);
            if (selected?.CanDownload != true || installation is { IsCurrent: false }) throw new DownloadAbortedException(Resources.Strings.Agent_Add_GateChanged);
            return await original(item, progress, ct);
        }};
        var active = queue.Items.FirstOrDefault(i => i.IsActive && i.Job.DedupeKey == job.DedupeKey);
        if (!reuseActive && active is not null && active.Job.SubTitle != job.SubTitle) throw new MutationRejectedException("JOB_ACTIVE", 6);
        return queue.Enqueue(job);
    }
}
