using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services;

/// <summary>One SteamML archive, parsed: depot keys and manifest ids from its lua, and the zip holding
/// the matching <c>&lt;depot&gt;_&lt;manifest&gt;.manifest</c> files.</summary>
public sealed record SteamMlArchive(string AppId, string ZipPath, IReadOnlyDictionary<long, string> Keys,
    IReadOnlyDictionary<long, string> ManifestIds, IReadOnlySet<string> ManifestFiles);

/// <summary>SteamML's public app-ID ZIPs. No account, key, or lua.tools quota.</summary>
public sealed partial class SteamMlService
{
    public const string SourceName = "SteamML";
    private static readonly Lazy<HttpClient> Client = new(() => AppHttp.CreateExternalDataClient(
        TimeSpan.FromMinutes(5), new Uri(AppConfig.SteamMlCdnUrl)));
    private readonly HttpClient _http;

    public SteamMlService() : this(Client.Value) { }
    internal SteamMlService(HttpClient http) => _http = http;

    private static Uri ZipUri(string appid)
    {
        ApiContract.RequireAppId(appid);
        return new Uri(AppConfig.SteamMlCdnUrl + appid + ".zip");
    }

    public async Task<string> CheckStatusAsync(string appid, CancellationToken ct = default)
    {
        var uri = ZipUri(appid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => "available",
                HttpStatusCode.NotFound => "unavailable",
                _ => "unknown"
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return "unknown"; }
    }

    internal async Task<Dictionary<string, string>> CheckSourcesAsync(string appid,
        Func<string, CancellationToken, Task<Dictionary<string, string>>> checkStandard, CancellationToken ct)
    {
        ApiContract.RequireAppId(appid);
        var steamMl = CheckStatusAsync(appid, ct);
        Dictionary<string, string> sources;
        try { sources = await checkStandard(appid, ct); }
        catch (ApiException) { sources = []; }
        sources[SourceName] = await steamMl;
        return sources;
    }

    // A game's archive also carries its DLC depots, so signed-out DLC and depot downloads read keys and
    // manifests from the base game's archive. Fetched once per app per session.
    private readonly ConcurrentDictionary<string, Task<SteamMlArchive?>> _archives = new();
    internal static string CacheFolder { get; set; } =
        Path.Combine(Path.GetTempPath(), AppConfig.DataDirectoryName, "steamml");

    /// <summary>The app's parsed archive, or null when SteamML has none.</summary>
    public async Task<SteamMlArchive?> GetArchiveAsync(string appid, CancellationToken ct = default)
    {
        ApiContract.RequireAppId(appid);
        var task = _archives.GetOrAdd(appid, FetchArchiveAsync);
        try { return await task.WaitAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A failure (offline, invalid archive) is retried next time; only "not on SteamML" is kept.
            _archives.TryRemove(new KeyValuePair<string, Task<SteamMlArchive?>>(appid, task));
            throw;
        }
    }

    private async Task<SteamMlArchive?> FetchArchiveAsync(string appid)
    {
        DownloadedFile file;
        try { file = await DownloadManifestAsync(appid, null, CancellationToken.None); }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound) { return null; }
        try
        {
            ManifestContentPolicy.EnsureNoReparsePoints(CacheFolder);
            Directory.CreateDirectory(CacheFolder);
            string path = Path.Combine(CacheFolder, appid + ".zip");
            File.Copy(file.FilePath, path, overwrite: true);
            return ParseArchive(appid, path);
        }
        finally { ManifestJobFactory.DeleteStaged(file.FilePath); }
    }

    [GeneratedRegex(@"^\s*addappid\(\s*(\d{1,10})\s*,\s*\d+\s*,\s*""([0-9a-fA-F]{64})""\s*\)", RegexOptions.Multiline)]
    private static partial Regex KeyLine();
    [GeneratedRegex(@"^\s*setManifestid\(\s*(\d{1,10})\s*,\s*""(\d{1,20})""", RegexOptions.Multiline)]
    private static partial Regex ManifestLine();

    internal static SteamMlArchive ParseArchive(string appid, string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        ManifestContentPolicy.ValidateArchive(zip, manifestPackage: true);
        var keys = new Dictionary<long, string>();
        var manifests = new Dictionary<long, string>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) { files.Add(entry.Name); continue; }
            if (!entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || entry.Length > 4 * 1024 * 1024) continue;
            using var reader = new StreamReader(entry.Open());
            string lua = reader.ReadToEnd();
            foreach (Match m in KeyLine().Matches(lua)) keys[long.Parse(m.Groups[1].Value)] = m.Groups[2].Value.ToLowerInvariant();
            foreach (Match m in ManifestLine().Matches(lua)) manifests[long.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
        }
        return new SteamMlArchive(appid, zipPath, keys, manifests, files);
    }

    /// <summary>Copy one depot manifest out of the archive into <paramref name="dir"/>, named the way
    /// Steam names it. Null when the archive doesn't have that version.</summary>
    public static string? ExtractManifest(SteamMlArchive archive, long depotId, string manifestId, string dir)
    {
        string name = $"{depotId}_{manifestId}.manifest";
        if (!archive.ManifestFiles.Contains(name)) return null;
        using var zip = ZipFile.OpenRead(archive.ZipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        entry.ExtractToFile(path, overwrite: true);
        return path;
    }

    public async Task<DownloadedFile> DownloadManifestAsync(string appid,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ZipUri(appid));
        HttpResponseBudget.Download.Apply(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ApiException(string.Format(Resources.Strings.SteamMl_DownloadFailed, (int)response.StatusCode), response.StatusCode);
        // Only the app-ID name is trusted, not a server-supplied Content-Disposition.
        response.Content.Headers.ContentDisposition = null;
        var file = await HttpFileDownloader.SaveResponseAsync(response, appid + ".zip", progress, ct);
        if (ManifestJobFactory.IsZip(file.FilePath)) return file;
        ManifestJobFactory.DeleteStaged(file.FilePath);
        throw new ApiException(Resources.Strings.SteamMl_InvalidArchive);
    }
}
