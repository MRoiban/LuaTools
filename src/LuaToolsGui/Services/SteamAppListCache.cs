using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>Bulk app names from Morrenus, with official Steam and bounded stale-cache fallback.</summary>
public class SteamAppListCache
{
    private const string MorrenusUrl = "https://applist.morrenus.xyz/";
    private const string SteamUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
    private const int MaximumEntries = 1000000;
    private const int MaximumBytes = 64 * 1024 * 1024;
    private readonly string _cacheFile;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<long, string> _names = new();
    private Task? _loadTask;
    public SteamAppListCache() : this(AppHttp.Create(TimeSpan.FromSeconds(60), responseBudget: HttpResponseBudget.LargeList),
        Path.Combine(LuaTools.Agent.DataPaths.Roaming, AppConfig.DataDirectoryName, "steam-applist.json")) { }
    internal SteamAppListCache(HttpMessageHandler handler, string cacheFile) : this(AppHttp.CreateForTesting(TimeSpan.FromSeconds(60), handler, HttpResponseBudget.LargeList), cacheFile) { }
    private SteamAppListCache(HttpClient http, string cacheFile) { _http = http; _cacheFile = cacheFile; }
    public string? GetName(long appid) => _names.TryGetValue(appid, out var name) ? name : null;
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();
    private bool ReadCache()
    {
        try
        {
            using var doc = JsonDocument.Parse(CatalogDataPolicy.Read(_cacheFile, MaximumBytes));
            CatalogDataPolicy.Validate(doc.RootElement, MaximumEntries, 4096);
            var names = new Dictionary<long, string>();
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!long.TryParse(entry.Name, out var id) || !CatalogDataPolicy.IsId(id) || entry.Value.ValueKind != JsonValueKind.String) return false;
                var name = entry.Value.GetString()!;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(id, name);
            }
            if (names.Count == 0) return false;
            foreach (var entry in names) _names[entry.Key] = entry.Value;
            return true;
        }
        catch { return false; }
    }
    private async Task LoadAsync()
    {
        if (File.Exists(_cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFile) < TimeSpan.FromDays(14) && ReadCache()) return;
        if (await DownloadAsync(MorrenusUrl, true) || await DownloadAsync(SteamUrl, false))
        {
            try { await CatalogDataPolicy.WriteAsync(_cacheFile, JsonSerializer.SerializeToUtf8Bytes(_names)); } catch { }
        }
        else ReadCache();
    }
    private async Task<bool> DownloadAsync(string url, bool flat)
    {
        try
        {
            using var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            CatalogDataPolicy.Validate(doc.RootElement, MaximumEntries, 4096);
            var apps = flat ? doc.RootElement : doc.RootElement.GetProperty("applist").GetProperty("apps");
            var names = new Dictionary<long, string>();
            foreach (var app in apps.EnumerateArray())
            {
                var id = app.GetProperty("appid").GetInt64();
                var name = app.GetProperty("name").GetString();
                if (!CatalogDataPolicy.IsId(id) || name is null) return false;
                if (!string.IsNullOrWhiteSpace(name)) names[id] = name;
            }
            if (names.Count == 0) return false;
            foreach (var entry in names) _names[entry.Key] = entry.Value;
            return true;
        }
        catch { return false; }
    }
}
