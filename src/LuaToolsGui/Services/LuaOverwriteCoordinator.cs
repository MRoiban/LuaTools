using System.IO;
using System.IO.Compression;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services;

public sealed record OverwriteRow(string Title, string Meta, bool IsDlc, bool IsShared, string SteamDbUrl);
public sealed record OverwriteDiff(long AppId, string Version, bool Overwrites, bool Readable, List<OverwriteRow> Added, List<OverwriteRow> Removed);

/// <summary>One secret-free overwrite projection for downloaded and submitted content.</summary>
public sealed class LuaOverwriteCoordinator(LuaInstaller installer, SteamAppListCache appList, SteamAppInfoCache appInfo, SteamDepotInfo depotInfo)
{
    public string Version(long appId) => installer.ReadInstalledLua(appId) is { } path ? LuaVault.HashFile(path) : "missing";
    public async Task<OverwriteDiff> InspectAsync(string candidate, long appId, bool enrich = true, CancellationToken ct = default)
    {
        // Lookup state belongs to this invocation; simultaneous GUI and pipe diffs cannot replace it.
        return await new Projection(installer, appList, appInfo, depotInfo).InspectAsync(candidate, appId, enrich, ct);
    }
    private sealed class Projection(LuaInstaller _installer, SteamAppListCache _appList, SteamAppInfoCache _appInfo, SteamDepotInfo _depotInfo)
    {
        private IReadOnlyDictionary<long, ContentDepot> _depotsById = new Dictionary<long, ContentDepot>();
        public async Task<OverwriteDiff> InspectAsync(string path, long appId, bool enrich, CancellationToken ct)
        {
            string? existing = _installer.ReadInstalledLua(appId);
            string version = existing is null ? "missing" : LuaVault.HashFile(existing);
            var oldLua = existing is null ? null : LuaFileParser.Parse(existing, appId);
            var newLua = ExtractLuaFromZip(path, appId);
            if (newLua is null) return new(appId, version, existing is not null, false, [], []);
            var diff = LuaFileParser.Diff(oldLua, newLua);
            if (enrich)
            {
                await _appList.EnsureLoadedAsync();
                _depotsById = await BuildDepotLookupAsync(appId);
                var unnamed = diff.Added.Concat(diff.Removed).Where(e => DiffDisplayName(e) is null).Select(e => e.Id).Distinct();
                await Parallel.ForEachAsync(unnamed, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (id, _) => await _appInfo.ResolveAsync(id));
            }
            return new(appId, version, existing is not null, true, diff.Added.Select(ToOverwriteRow).ToList(), diff.Removed.Select(ToOverwriteRow).ToList());
        }
    private async Task<IReadOnlyDictionary<long, ContentDepot>> BuildDepotLookupAsync(long appId)
    {
        var map = new Dictionary<long, ContentDepot>();
        var info = await _depotInfo.GetAsync(appId);
        if (info is null) return map;
        foreach (var d in info.Depots)
        {
            map[d.Id] = d;
            if (d.DlcAppId is { } dlc) map[dlc] = d;
        }
        return map;
    }

    /// <summary>Real Steam name for a lua entry (DLC name from caches, or lua comment); null if unknown.</summary>
    private string? DiffDisplayName(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        if (d?.DlcAppId is { } dlc)
        {
            string? steamName = _appList.GetName(dlc) ?? _appInfo.GetCached(dlc)?.Name;
            if (steamName is not null) return steamName;
        }
        return _appList.GetName(e.Id) ?? _appInfo.GetCached(e.Id)?.Name ?? e.Comment;
    }

    /// <summary>Enriched diff row: real name + "id · size · OS · lang" + DLC/SHARED chip + SteamDB link.</summary>
    private OverwriteRow ToOverwriteRow(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        bool isDlc = d?.IsDlc ?? false;
        bool isShared = d?.IsShared ?? false;

        string title = DiffDisplayName(e)
            ?? (isDlc ? string.Format(Resources.Strings.Manage_DlcName, d!.DlcAppId)
                : isShared ? Resources.Strings.Manage_SharedDepot
                : e.HasKey ? Resources.Strings.Manage_Depot
                : string.Format(Resources.Strings.Manage_DlcName, e.Id));

        var meta = new List<string> { e.Id.ToString() };
        if (d is { Size: > 0 }) meta.Add(FormatSize(d.Size));
        if (!string.IsNullOrWhiteSpace(d?.Os)) meta.Add(PrettyOs(d!.Os!));
        if (!string.IsNullOrWhiteSpace(d?.Language)) meta.Add(d!.Language!);

        string url = d?.DlcAppId is { } dlcId
            ? $"https://steamdb.info/app/{dlcId}/"
            : $"https://steamdb.info/depot/{e.Id}/";

        return new OverwriteRow(title, string.Join("  ·  ", meta), isDlc, isShared, url);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.##} GB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string PrettyOs(string os) => os switch
    {
        "windows" => "Windows",
        "macos" or "macosx" => "macOS",
        "linux" => "Linux",
        _ => os
    };

    /// <summary>Extract the .lua from a manifest zip and parse it (without installing). A bare-lua
    /// download (no zip wrapper) is parsed directly so the overwrite-diff still works for those sources.</summary>
    private static LuaContents? ExtractLuaFromZip(string zipPath, long appId)
    {
        try
        {
            // bare .lua → parse as-is. Same byte sniff the install path uses.
            if (!ManifestJobFactory.IsZip(zipPath)) return LuaFileParser.Parse(zipPath, appId);

            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var luaEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry is null || luaEntry.Length > 4 * 1024 * 1024) return null;

            string tmp = Path.Combine(Path.GetTempPath(), $"luatools_diff_{appId}_{Guid.NewGuid():N}.lua");
            luaEntry.ExtractToFile(tmp, overwrite: true);
            try { return LuaFileParser.Parse(tmp, appId); }
            finally { try { File.Delete(tmp); } catch { /* temp cleanup best-effort */ } }
        }
        catch { return null; }
    }

    }
}
