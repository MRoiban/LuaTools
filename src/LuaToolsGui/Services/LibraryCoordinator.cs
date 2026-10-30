using System.IO;
namespace LuaToolsGui.Services;

public sealed record LibraryEntry(long AppId, string FilePath, DateTime AddedAt, string Name, bool NameIsPlaceholder);

/// <summary>GUI-owned library data operations, independent of WPF rendering and selection.</summary>
public static class LibraryCoordinator
{
    public static IEnumerable<Cabinet.CabinetGame> CabinetGames(IEnumerable<Cabinet.CabinetGame> games) => CabinetProjection.OrderGames(games);
    public static (string[] Types, string[] Genres, string[] Years) FilterOptions(IEnumerable<AppFilterData?> values)
    {
        var types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var genres = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var years = new SortedSet<int>();
        foreach (var d in values)
        {
            if (d is null) continue;
            if (!string.IsNullOrWhiteSpace(d.Type)) types.Add(d.Type);
            foreach (var genre in d.Genres) genres.Add(genre);
            if (d.ReleaseYear is { } year) years.Add(year);
        }
        return (types.ToArray(), genres.ToArray(), years.OrderByDescending(y => y).Select(y => y.ToString()).ToArray());
    }
    public static async Task<(string? Cover, string? Name)> ResolveTileDataAsync(long appid, SteamAppInfoCache details, CoverCache covers, bool placeholder)
    {
        string? name = null;
        string? local = await ResolveCoverFileAsync(appid, details, covers, n => { if (placeholder) name = n; });
        if (placeholder && name is null)
        {
            var info = details.GetCached(appid) ?? await details.ResolveAsync(appid);
            if (!string.IsNullOrWhiteSpace(info?.Name)) name = info.Name;
        }
        return (local, name);
    }
    public static string Version(string path) => File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    public static bool TryDelete(string path, out string? error)
    {
        try { if (File.Exists(path)) File.Delete(path); error = null; return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    public static Task<SteamlessResult> RemoveDrmAsync(SteamlessService service, long appid, IProgress<double?>? progress = null, CancellationToken ct = default) => service.PatchGameAsync(appid, progress, ct);
    public static async Task<List<LibraryEntry>> ScanAsync(string dir, SteamAppListCache names, SteamAppInfoCache details, bool home = false)
    {
        if (!Directory.Exists(dir)) return [];
        await names.EnsureLoadedAsync();
        return await Task.Run(() => (home ? Directory.EnumerateFiles(dir, "*.lua").Where(p => long.TryParse(Path.GetFileNameWithoutExtension(p), out _)).Select(p => (AppId: long.Parse(Path.GetFileNameWithoutExtension(p)), Path: p)) : LuaInstaller.EnumerateInstalled(dir)).Select(f =>
        {
            var info = new FileInfo(f.Path);
            string? name = names.GetName(f.AppId) ?? (home ? null : ParseLuaName(f.Path)) ?? details.GetCached(f.AppId)?.Name;
            var added = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;
            return new LibraryEntry(f.AppId, f.Path, added, name ?? string.Format(Resources.Strings.Common_AppFallback, f.AppId), name is null);
        }).OrderByDescending(t => t.AddedAt).ToList());
    }
    public static string? ParseLuaName(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            string? line0 = reader.ReadLine();
            string? line1 = reader.ReadLine();
            if (line0 is not null && line1 is not null &&
                line0.Contains("Created by", StringComparison.OrdinalIgnoreCase) &&
                line1.StartsWith("--"))
            {
                string name = line1.TrimStart('-', ' ').Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch { /* unreadable. Fall back to appid */ }
        return null;
    }
    public static bool MatchesFilters(AppFilterData d, string SelectedType = "Any", string SelectedGenre = "Any", string SelectedYear = "Any", string SelectedPrice = "Any", string SelectedContent = "Any")
    {
        if (SelectedType != "Any" &&
            !string.Equals(d.Type, SelectedType, StringComparison.OrdinalIgnoreCase)) return false;

        if (SelectedGenre != "Any" &&
            !d.Genres.Any(g => string.Equals(g, SelectedGenre, StringComparison.OrdinalIgnoreCase))) return false;

        if (SelectedYear != "Any" &&
            (d.ReleaseYear is null || d.ReleaseYear.Value.ToString() != SelectedYear)) return false;

        if (SelectedPrice != "Any")
        {
            if (SelectedPrice == "Free" && !d.IsFree) return false;
            if (SelectedPrice == "Paid" && d.IsFree) return false;
        }

        if (SelectedContent == "Hide adult" && d.IsAdult) return false;
        if (SelectedContent == "Adult only" && !d.IsAdult) return false;

        return true;
    }
    public static List<T> Sort<T>(IEnumerable<T> tiles, string sort, Func<T, string> name, Func<T, DateTime> added, Func<T, AppFilterData?> data) => sort switch
    {
        "Name (A–Z)" => tiles.OrderBy(t => name(t), StringComparer.OrdinalIgnoreCase).ToList(),
        "Release date (newest)" => tiles
            .OrderByDescending(t => data(t)?.ReleaseDate ?? DateTime.MinValue).ToList(),
        "Metacritic" => tiles
            .OrderByDescending(t => data(t)?.Metacritic ?? int.MinValue).ToList(),
        "Most reviewed" => tiles
            .OrderByDescending(t => data(t)?.Reviews ?? long.MinValue).ToList(),
        _ => tiles.OrderByDescending(t => added(t)).ToList(), // "Recently added" (default)
    };


    public static async Task<string?> ResolveCoverFileAsync(
        long appId, SteamAppInfoCache appInfo, CoverCache covers, Action<string>? onName = null)
    {
        string? local = covers.GetLocalPath(appId);
        if (local is not null) return local;
        if (covers.IsKnownMissing(appId)) return null;

        // Fast path: predictable CDN URL. The grey "Header Capsule" placeholder it can serve for newer
        // apps is fingerprinted + rejected by CoverCache, so this falls through to header_image below.
        local = await covers.EnsureAsync(appId, SteamAppInfoCache.GuessHeaderImageUrl(appId));
        if (local is not null) return local;

        // Slow path: appdetails header_image (throttled) + optional name backfill.
        var info = appInfo.GetCached(appId) ?? await appInfo.ResolveAsync(appId);
        if (onName is not null && !string.IsNullOrWhiteSpace(info?.Name)) onName(info!.Name);
        if (!string.IsNullOrWhiteSpace(info?.HeaderImage))
            local = await covers.EnsureAsync(appId, info!.HeaderImage!);

        // info != null means appdetails answered (so an empty header = genuinely no cover).
        // info == null means we couldn't reach it (rate-limited/offline). Don't give up, retry later.
        if (local is null && info is not null) covers.MarkMissing(appId);
        return local;
    }
}
