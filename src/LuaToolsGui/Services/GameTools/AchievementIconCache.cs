using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;

namespace LuaToolsGui.Services.GameTools;

/// <summary>
/// Achievement icons from Steam's community CDN (<see cref="GameStatsFiles.IconBase"/>, a host already in
/// <see cref="OutboundNetworkPolicy"/>), cached on disk per app so each is downloaded once.
/// Only JPEG/PNG bodies that decode are kept.
/// </summary>
public class AchievementIconCache
{
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _downloads = new(6);
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public AchievementIconCache()
        : this(AppHttp.Create(TimeSpan.FromSeconds(20), responseBudget: HttpResponseBudget.Image),
            Path.Combine(LuaTools.Agent.DataPaths.Roaming, AppConfig.DataDirectoryName, "achievement-icons")) { }

    internal AchievementIconCache(HttpClient http, string root) { _http = http; _root = root; }

    /// <summary>Local path of the icon, downloading it first if needed; null when unavailable.</summary>
    public Task<string?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (LocalPath(url) is not { } path) return Task.FromResult<string?>(null);
        if (File.Exists(path)) return Task.FromResult<string?>(path);
        var task = _inFlight.GetOrAdd(path, _ => DownloadAsync(new Uri(url!), path, ct));
        if (task.IsCompleted) _inFlight.TryRemove(path, out _);
        return task;
    }

    /// <summary>&lt;root&gt;\&lt;appid&gt;\&lt;file&gt; for a URL under the icon base, else null.</summary>
    internal string? LocalPath(string? url)
    {
        if (url is null || !url.StartsWith(GameStatsFiles.IconBase, StringComparison.Ordinal)) return null;
        var parts = url[GameStatsFiles.IconBase.Length..].Split('/');
        if (parts.Length != 2 || !long.TryParse(parts[0], out long app) || !CatalogDataPolicy.IsId(app)) return null;
        string file = parts[1];
        if (file.Length == 0 || file.StartsWith('.') || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !file.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) return null;
        return Path.Combine(_root, parts[0], file);
    }

    private async Task<string?> DownloadAsync(Uri uri, string path, CancellationToken ct)
    {
        await _downloads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return path;
            byte[] bytes = await _http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
            if (!IsImage(bytes) || SafeImageLoader.Decode(bytes, 64) is null) return null;
            await CatalogDataPolicy.WriteAsync(path, bytes, ct).ConfigureAwait(false);
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; } // 404, offline, policy refusal or write error: the row shows no icon
        finally
        {
            _downloads.Release();
            _inFlight.TryRemove(path, out _);
        }
    }

    internal static bool IsImage(byte[] b) =>
        (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||
        (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47);
}
