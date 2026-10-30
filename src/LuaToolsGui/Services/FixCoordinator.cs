using System.IO;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.Services.OnlineFixMe;

namespace LuaToolsGui.Services;

public sealed record FixCatalogue(DenuvoGameListing[] Games, DenuvoGameListing[] Installed, DenuvoTag[] Tags, HashSet<long> Added, bool Unavailable);
public sealed record FixEntry(DenuvoFix Fix, Uri? Folder, bool Installed, bool Applied);
public sealed record FixDetail(string AppId, string Name, FixEntry[] Fixes, OnlineFixMeLookupStatus ProviderStatus);

/// <summary>Presentation-free provider orchestration shared by the Fixes page and agent.</summary>
public class FixCoordinator(LuaToolsApiClient api, AuthService auth, SettingsService settings, SteamService steam,
    SteamLibraryService library, OnlineFixMeClient online, ManifestJobFactory jobs)
{
    public const string OnlineTagId = "provider:online-fix.me";
    public static DenuvoTag OnlineTag => new() { Id = OnlineTagId, Name = Resources.Strings.Fixes_OnlineFixMe_Provider };
    private static readonly HashSet<long> RuntimeIds = [228980, 1007, 1070560, 1391110, 1628350, 1493710, 2180100, 1887720, 2348590, 2805730, 3658110];
    public static DenuvoGameListing InstalledCard(SteamLibraryService.InstalledGame g) => new()
    {
        AppId = g.AppId.ToString(), Name = g.Name,
        HeaderImage = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{g.AppId}/header.jpg", Tags = [OnlineTag],
    };
    public static DenuvoGameListing[] BuildInstalled(IReadOnlyList<DenuvoGameListing> listing, IEnumerable<SteamLibraryService.InstalledGame> installed)
    {
        var byId = listing.GroupBy(g => g.AppId).ToDictionary(g => g.Key, g => g.First());
        return installed.DistinctBy(g => g.AppId).Where(g => !RuntimeIds.Contains(g.AppId))
            .Select(g => byId.GetValueOrDefault(g.AppId.ToString()) ?? InstalledCard(g))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private readonly object _reads = new();
    private Task<FixCatalogue>? _listing;
    private string? _listingContext;
    private DateTimeOffset _listedAt;
    public Task<FixCatalogue> ListAsync(CancellationToken ct = default)
    {
        lock (_reads)
        {
            string context = steam.EffectivePath + ":" + auth.IsGuest;
            if (_listingContext == context && _listing is not null && (!_listing.IsCompleted || DateTimeOffset.UtcNow - _listedAt < TimeSpan.FromSeconds(1))) return _listing.WaitAsync(ct);
            _listedAt = DateTimeOffset.UtcNow; _listingContext = context;
            return _listing = LoadAsync(ct);
        }
    }
    private async Task<FixCatalogue> LoadAsync(CancellationToken ct)
    {
        var installedTask = Task.Run(library.EnumerateInstalled, ct);
        DenuvoListingsResponse? data = null;
        if (!auth.IsGuest) try { data = await api.GetDenuvoListingsAsync(); } catch { }
        var games = data?.Games.ToArray() ?? [];
        var installed = BuildInstalled(games, await installedTask);
        var added = steam.StPlugInDir is { } dir ? await Task.Run(() => LuaInstaller.EnumerateInstalled(dir).Select(i => i.AppId).ToHashSet(), ct) : [];
        return new(auth.IsGuest ? installed : games, installed, data is null ? (auth.IsGuest ? [] : [OnlineTag]) : [.. data.Tags, OnlineTag], added, !auth.IsGuest && data is null);
    }
    public static DenuvoTag[] DetailTags(IEnumerable<DenuvoFix> fixes) => fixes.SelectMany(f => f.Tags).GroupBy(t => t.Id).Select(g => g.First()).OrderBy(t => t.Name).ToArray();
    public static IEnumerable<T> FilterDetail<T>(IEnumerable<T> rows, string? tag, Func<T, IEnumerable<DenuvoTag>> tags) => tag is null ? rows : rows.Where(f => tags(f).Any(t => t.Id == tag));
    public static IEnumerable<DenuvoGameListing> Filter(FixCatalogue data, string? tag, bool mine, string query, bool online = false)
    {
        IEnumerable<DenuvoGameListing> rows = online || tag == OnlineTagId ? data.Installed : data.Games;
        if (tag is not null && tag != OnlineTagId) rows = rows.Where(g => g.Tags.Any(t => t.Id == tag));
        if (mine) rows = rows.Where(g => long.TryParse(g.AppId, out long id) && data.Added.Contains(id));
        string q = query.Trim();
        return q.Length == 0 ? rows : rows.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || g.AppId.Contains(q));
    }
    public Task<OnlineFixMeLookup> LookupAsync(string title, string? folder, bool fresh, CancellationToken ct) => online.LookupAsync(title, folder, fresh, ct);
    public Task<DenuvoFixesResponse?> LuaToolsAsync(string appId) => auth.IsGuest ? Task.FromResult<DenuvoFixesResponse?>(null) : api.GetDenuvoFixesAsync(appId);
    public async Task<FixDetail> DetailAsync(long appId, string? folder = null, bool fresh = false, CancellationToken ct = default)
    {
        var installed = library.EnumerateInstalled().FirstOrDefault(g => g.AppId == appId);
        DenuvoFixesResponse? lua = null;
        try { lua = await LuaToolsAsync(appId.ToString()); } catch { }
        string title = installed?.Name ?? lua?.Name ?? appId.ToString();
        var found = await LookupAsync(title, folder, fresh, ct);
        var entries = new List<FixEntry>();
        foreach (var f in lua?.Fixes ?? []) entries.Add(Entry(f, null, installed?.InstallDir));
        if (found.Status == OnlineFixMeLookupStatus.Found && found.FolderUrl is { } url)
            foreach (var a in found.Archives) entries.Add(Entry(new()
            {
                Id = OnlineFixMeFixIds.ForArchive(a.Name), Title = a.Name, HasFix = true, FixFilename = a.Name,
                Description = string.Join(" · ", new[] { a.SizeLabel, a.DateLabel, Resources.Strings.Fixes_OnlineFixMe_ArchiveDescription }.Where(s => !string.IsNullOrWhiteSpace(s))), Tags = [OnlineTag],
            }, url, installed?.InstallDir));
        return new(appId.ToString(), title, entries.ToArray(), found.Status);
    }
    private static FixEntry Entry(DenuvoFix f, Uri? folder, string? dir) => new(f, folder, dir is not null, dir is not null && File.Exists(ManifestJobFactory.GetFixRecordPath(dir, f.Id)));
    public void RequireApply(long appId, bool onlineProvider, bool manifest = false)
    {
        if (!manifest) ExecutableFixPolicy.RequireEnabled(settings);
        if (!onlineProvider && auth.IsGuest) throw new InvalidOperationException(Resources.Strings.Fixes_SignIn);
        if (!manifest && library.GetInstallDir(appId) is null) throw new InvalidOperationException(Resources.Strings.Fixes_Toast_GameNotFound);
    }
    public DownloadJob CreateJob(long appId, string name, DenuvoFix fix, Uri? folder, string slot, Action<DownloadItem, JobResult?>? finished = null)
    {
        RequireApply(appId, folder is not null, slot == "manifest");
        return folder is not null ? jobs.CreateOnlineFixMeJob(folder, fix.FixFilename!, appId, name, finished)
            : jobs.CreateDenuvoJob(fix.Id, slot, slot == "manifest" ? fix.ManifestFilename ?? $"{appId}.zip" : fix.FixFilename ?? $"{appId}_fix.zip", appId, name, fix.Title, finished);
    }
    public JobResult Revert(long appId, string id, string name) => jobs.RevertDenuvoFix(appId, id, name);
    public string? InstallDir(long appId) => library.GetInstallDir(appId);
}
