using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// "Add all DLC" for one base game, driven from the Steam store plugin in two steps: <see cref="Check"/>
/// resolves and verifies every DLC without writing anything, then <see cref="Apply"/> installs the ones
/// that verified.
/// </summary>
/// <remarks>
/// <para>The DLC set is the game's own declared list (PICS <c>listofdlc</c>, parent = this game) plus
/// the store's <c>dlc</c> list. The store list also carries DLC of OTHER games (Total War: WARHAMMER III
/// lists WARHAMMER I/II packs), so those take their parent from their own Steam <c>fullgame</c> and are
/// verified against it — never against the page's game.</para>
/// <para>DexLua coverage is checked first, using one validated script per parent per batch.
/// Covered DLC are generated locally from that snapshot, without lua.tools quota. Other DLC use
/// the legacy info/generate route; its quota only affects legacy items. Depotless DLC are written
/// locally. All generated files go through
/// <see cref="LuaInstaller.InstallLua"/> and its content policy.</para>
/// <para>Ownership comes from the store page's own markers and can only SKIP a DLC, never add one.</para>
/// </remarks>
public sealed class DlcBatchService
{
    internal const int MaxDlc = 1000;
    internal const int MaxOwned = 400;

    /// <summary>The operations the batch needs, injectable for tests.</summary>
    internal sealed record Ports(
        Func<long, CancellationToken, Task<GameDetails?>> Details,
        Func<long, CancellationToken, Task<IReadOnlyList<long>>> DeclaredDlc,
        Func<long, IReadOnlyList<long>> StoreDlc,
        Func<long, bool> IsInstalled,
        Func<string, string, CancellationToken, Task<DlcInfo?>> DlcInfo,
        Func<string, string, CancellationToken, Task<DownloadedFile>> Generate,
        Func<string, long, InstallResult> Install,
        Func<long, CancellationToken, Task<DexLuaDlcSnapshot>>? DexLua = null,
        Func<string, long, string, InstallResult>? InstallWithSource = null);

    public sealed class Item
    {
        public long AppId { get; init; }
        public long BaseAppId { get; set; }
        public string? Name { get; set; }
        /// <summary>queued, owned, added, ready, unavailable, not-dlc, failed, installed, limit.</summary>
        public string State { get; set; } = "queued";
        /// <summary>Ready and has keyed depots requiring generated Lua.</summary>
        public bool Download { get; set; }
        public string Source { get; set; } = "lua.tools DLC";
        internal DexLuaDlcSnapshot? DexLua { get; set; }
        public string? Error { get; set; }
    }

    public sealed class Batch
    {
        public long AppId { get; init; }
        /// <summary>checking, ready, applying, done, failed, cancelled.</summary>
        public string Phase { get; set; } = "checking";
        public List<Item> Items { get; } = [];
        public int Done { get; set; }
        public string? Error { get; set; }
        internal CancellationTokenSource Cts { get; } = new();
        internal Task Work { get; set; } = Task.CompletedTask;
        internal Dictionary<long, DexLuaDlcSnapshot?> DexLuaParents { get; } = [];
    }

    private readonly Ports _ports;
    private readonly ConcurrentDictionary<long, Batch> _batches = new();
    private readonly object _gate = new();

    public DlcBatchService(LuaToolsApiClient api, SteamDepotInfo depots, SteamAppInfoCache appInfo,
        LuaInstaller installer)
        : this(new Ports(
            (id, ct) => api.GetDetailsAsync(id.ToString(), ct),
            async (id, ct) => (await depots.GetAsync(id, ct))?.DlcIds ?? [],
            id => StoreDlcIds(appInfo.GetFullDetails(id)),
            id => installer.ReadInstalledLua(id) is not null,
            (dlc, b, ct) => api.GetDlcInfoAsync(dlc, b, ct),
            (dlc, b, ct) => api.GenerateDlcAsync(dlc, b, null, null, ct),
            (path, id) => installer.InstallLua(path, id, source: "lua.tools DLC"),
            (id, ct) => api.GetDexLuaDlcSnapshotAsync(id.ToString(), ct),
            (path, id, source) => installer.InstallLua(path, id, source: source)))
    { }

    internal DlcBatchService(Ports ports) => _ports = ports;

    public Batch? GetState(long baseAppId) => _batches.TryGetValue(baseAppId, out var b) ? b : null;

    /// <summary>Resolve + verify every DLC of <paramref name="baseAppId"/>. Writes nothing.</summary>
    public bool Check(long baseAppId, IReadOnlyCollection<long> owned)
    {
        var batch = new Batch { AppId = baseAppId };
        lock (_gate)
        {
            if (_batches.TryGetValue(baseAppId, out var current) && current.Phase is "checking" or "applying") return false;
            _batches[baseAppId] = batch;
        }
        var ownedSet = owned.Take(MaxOwned).ToHashSet();
        batch.Work = Task.Run(() => CheckAsync(batch, ownedSet));
        return true;
    }

    /// <summary>Install every DLC the last <see cref="Check"/> verified as ready.</summary>
    public bool Apply(long baseAppId)
    {
        lock (_gate)
        {
            if (!_batches.TryGetValue(baseAppId, out var batch) || batch.Phase != "ready"
                || !batch.Items.Any(i => i.State == "ready")) return false;
            batch.Phase = "applying";
            batch.Done = 0;
            batch.Work = Task.Run(() => ApplyAsync(batch));
            return true;
        }
    }

    public bool Cancel(long baseAppId)
    {
        lock (_gate)
        {
            if (!_batches.TryGetValue(baseAppId, out var batch) || batch.Phase is not ("checking" or "applying" or "ready"))
                return false;
            batch.Cts.Cancel();
            if (batch.Phase == "ready") batch.Phase = "cancelled";
            return true;
        }
    }

    private async Task CheckAsync(Batch batch, HashSet<long> owned)
    {
        var ct = batch.Cts.Token;
        try
        {
            var game = await _ports.Details(batch.AppId, ct);
            if (game is null || game.IsDlc) { Fail(batch, Resources.Strings.DlcBatch_Err_NotAGame); return; }

            var declared = (await _ports.DeclaredDlc(batch.AppId, ct)).Where(CatalogDataPolicy.IsId).ToHashSet();
            var all = declared.Concat(_ports.StoreDlc(batch.AppId).Where(CatalogDataPolicy.IsId))
                .Where(id => id != batch.AppId).Distinct().Take(MaxDlc).ToList();
            lock (_gate)
                foreach (var id in all)
                    batch.Items.Add(new Item { AppId = id, BaseAppId = declared.Contains(id) ? batch.AppId : 0 });
            if (all.Count == 0) { Fail(batch, Resources.Strings.DlcBatch_Err_NoDlc); return; }
            PluginLog.Log($"DlcBatch.Check appid={batch.AppId} declared={declared.Count} total={all.Count} owned={owned.Count}");

            foreach (var item in batch.Items.ToList())
            {
                ct.ThrowIfCancellationRequested();
                await CheckOneAsync(batch, item, owned, ct);
                lock (_gate) batch.Done++;
            }
            lock (_gate) batch.Phase = "ready";
            PluginLog.Log($"DlcBatch.Ready appid={batch.AppId} {Summary(batch)}");
        }
        catch (OperationCanceledException) { lock (_gate) batch.Phase = "cancelled"; }
        catch (Exception ex)
        {
            PluginLog.Log($"DlcBatch.Check appid={batch.AppId} EXCEPTION: {ex.GetType().Name}");
            Fail(batch, ex is ApiException or AuthException ? ex.Message : Resources.Strings.Add_Err_Generic);
        }
    }

    private async Task CheckOneAsync(Batch batch, Item item, HashSet<long> owned, CancellationToken ct)
    {
        if (owned.Contains(item.AppId)) { item.State = "owned"; return; }
        if (_ports.IsInstalled(item.AppId)) { item.State = "added"; return; }
        if (item.BaseAppId == 0)
        {
            // Store-listed only: its own Steam record says which game it belongs to.
            var details = await _ports.Details(item.AppId, ct);
            item.Name = details?.Name;
            if (details is not { IsDlc: true } || !ApiContract.IsAppId(details.BaseAppId))
            { item.State = "not-dlc"; return; }
            item.BaseAppId = long.Parse(details.BaseAppId!, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (_ports.DexLua is not null)
        {
            if (!batch.DexLuaParents.TryGetValue(item.BaseAppId, out var source))
            {
                try { source = await _ports.DexLua(item.BaseAppId, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or OperationCanceledException)
                { source = null; }
                batch.DexLuaParents[item.BaseAppId] = source;
            }
            if (source is not null)
            {
                DlcInfo? coverage;
                try { coverage = source.GetInfo(item.AppId.ToString()); }
                catch (ApiException) { coverage = null; } // store-listed DLC may not belong to this parent's depot map
                if (coverage is not null && (coverage.HaveCount > 0 || coverage.MissingCount == 0))
                {
                    item.Source = DexLuaService.SourceName;
                    item.DexLua = source;
                    item.Download = coverage.DepotCount > 0;
                    item.State = "ready";
                    return;
                }
            }
        }
        try
        {
            var info = await _ports.DlcInfo(item.AppId.ToString(), item.BaseAppId.ToString(), ct);
            if (info is null || !(info.HaveCount > 0 || info.MissingCount == 0)) { item.State = "unavailable"; return; }
            item.Download = info.DepotCount > 0;
            item.State = "ready";
        }
        catch (ApiException ex) when (ex.Status is null or System.Net.HttpStatusCode.NotFound)
        {
            // One DLC the API cannot describe must not sink the whole batch.
            item.State = "unavailable";
            item.Error = ex.Message;
        }
        catch (ApiException ex)
        {
            // An exhausted/expired legacy source must not hide later public-source DLC.
            item.State = ex.Status == System.Net.HttpStatusCode.TooManyRequests ? "limit" : "failed";
            item.Error = ex.Message;
        }
        catch (AuthException ex) { item.State = "failed"; item.Error = ex.Message; }
    }

    private async Task ApplyAsync(Batch batch)
    {
        var ct = batch.Cts.Token;
        bool limitHit = false;
        try
        {
            foreach (var item in batch.Items.Where(i => i.State == "ready").ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (item.Download && item.DexLua is null && limitHit)
                { item.State = "limit"; lock (_gate) batch.Done++; continue; }
                try
                {
                    var result = item.Download ? await GenerateAndInstallAsync(item, ct) : InstallLocal(item);
                    if (result.Error is null && !result.AnyFailed) item.State = "installed";
                    else { item.State = "failed"; item.Error = result.Error; }
                }
                catch (ApiException ex) when (item.DexLua is null && ex.Status == System.Net.HttpStatusCode.TooManyRequests)
                {
                    limitHit = true;
                    item.State = "limit";
                }
                catch (ApiException ex) { item.State = "failed"; item.Error = ex.Message; }
                catch (AuthException ex) { item.State = "failed"; item.Error = ex.Message; }
                lock (_gate) batch.Done++;
            }
            lock (_gate) batch.Phase = "done";
        }
        catch (OperationCanceledException) { lock (_gate) batch.Phase = "cancelled"; }
        catch (Exception ex)
        {
            PluginLog.Log($"DlcBatch.Apply appid={batch.AppId} EXCEPTION: {ex.GetType().Name}");
            Fail(batch, Resources.Strings.Add_Err_Generic);
        }
        PluginLog.Log($"DlcBatch.Apply appid={batch.AppId} {Summary(batch)}");
    }

    /// <summary>The whole unlock for a DLC with no depots: identical to what lua.tools generates for it.</summary>
    internal static string LocalLua(long dlcAppId) => $"addappid({dlcAppId})\n";

    private InstallResult InstallLocal(Item item)
    {
        string dir = Path.Combine(Path.GetTempPath(), "LuaTools-dlc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, $"{item.AppId}.lua");
            File.WriteAllText(path, LocalLua(item.AppId), new UTF8Encoding(false));
            return Install(path, item);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private async Task<InstallResult> GenerateAndInstallAsync(Item item, CancellationToken ct)
    {
        var file = item.DexLua is { } source
            ? await source.GenerateAsync(item.AppId.ToString(), null, ct)
            : await _ports.Generate(item.AppId.ToString(), item.BaseAppId.ToString(), ct);
        try { return Install(file.FilePath, item); }
        finally { Downloads.ManifestJobFactory.DeleteStaged(file.FilePath); }
    }

    private InstallResult Install(string path, Item item) =>
        _ports.InstallWithSource?.Invoke(path, item.AppId, item.Source) ?? _ports.Install(path, item.AppId);

    private void Fail(Batch batch, string error)
    {
        lock (_gate) { batch.Phase = "failed"; batch.Error = error; }
    }

    private static string Summary(Batch batch) => string.Join(" ", batch.Items.GroupBy(i => i.State)
        .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}"));

    /// <summary>The <c>dlc</c> array of a cached Steam appdetails blob.</summary>
    internal static IReadOnlyList<long> StoreDlcIds(string? appDetailsJson)
    {
        if (string.IsNullOrWhiteSpace(appDetailsJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(appDetailsJson);
            if (!doc.RootElement.TryGetProperty("dlc", out var dlc) || dlc.ValueKind != JsonValueKind.Array) return [];
            return dlc.EnumerateArray().Take(MaxDlc)
                .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _))
                .Select(e => e.GetInt64()).ToList();
        }
        catch (JsonException) { return []; }
    }
}
