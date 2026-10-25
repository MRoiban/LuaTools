using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
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
        try
        {
            if (!ManifestJobFactory.IsZip(file.FilePath))
                throw new ApiException(Resources.Strings.SteamMl_InvalidArchive);
            PrepareArchive(appid, file.FilePath);
            return file;
        }
        catch
        {
            ManifestJobFactory.DeleteStaged(file.FilePath);
            throw;
        }
    }

    /// <summary>Convert SteamML's transport extras to the manifest-only format used by every consumer.</summary>
    internal static void PrepareArchive(string appid, string zipPath)
    {
        ApiContract.RequireAppId(appid);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            // Check paths, links, duplicates and expansion limits BEFORE ignoring any transport extras.
            ManifestContentPolicy.ValidateArchive(zip, manifestPackage: false);
            foreach (var entry in zip.Entries.ToArray())
            {
                // SteamML also bundles a root README and a Steam user-stats schema. Neither is installed
                // or consumed by LuaTools. Keep this exception specific; all other payloads are validated.
                if (entry.FullName.Equals("README", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.Equals($"UserStats_{appid}_0.bin", StringComparison.OrdinalIgnoreCase))
                {
                    using (var stream = entry.Open())
                        ManifestContentPolicy.ReadBounded(stream, ManifestContentPolicy.MaxDocumentationBytes);
                    entry.Delete();
                    continue;
                }
                if (!entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) continue;
                using var luaStream = entry.Open();
                byte[] bytes = ManifestContentPolicy.ReadBounded(luaStream, ManifestContentPolicy.MaxLuaBytes);
                string lua = new UTF8Encoding(false, true).GetString(bytes);
                byte[] normalized = Encoding.UTF8.GetBytes(RemoveBlockComments(lua));
                ManifestContentPolicy.ValidateLua(normalized, long.Parse(appid));
                luaStream.Position = 0;
                luaStream.SetLength(0);
                luaStream.Write(normalized);
            }
        }
        using var prepared = ZipFile.OpenRead(zipPath);
        ManifestContentPolicy.ValidateArchive(prepared, expectedAppId: long.Parse(appid));
    }

    // Strip only actual Lua long comments, preserving token boundaries and line breaks. Leaving
    // them in the installed file would confuse the line-based depot parser and pin/enable editors.
    internal static string RemoveBlockComments(string lua)
    {
        char[] result = lua.ToCharArray();
        for (int i = 0; i < lua.Length; i++)
        {
            if (lua[i] is '\'' or '"')
            {
                char quote = lua[i];
                while (++i < lua.Length && lua[i] != quote)
                    if (lua[i] == '\\') i++;
                continue;
            }
            if (lua[i] != '-' || i + 1 >= lua.Length || lua[i + 1] != '-') continue;
            int opening = i + 2, end = opening;
            if (opening < lua.Length && lua[opening] == '[')
            {
                end++;
                while (end < lua.Length && lua[end] == '=') end++;
                if (end < lua.Length && lua[end] == '[')
                {
                    string closing = "]" + new string('=', end - opening - 1) + "]";
                    int close = lua.IndexOf(closing, end + 1, StringComparison.Ordinal);
                    if (close < 0) throw new ApiException(Resources.Strings.SteamMl_InvalidArchive);
                    int after = close + closing.Length;
                    for (; i < after; i++)
                        if (result[i] is not ('\r' or '\n')) result[i] = ' ';
                    i--;
                    continue;
                }
            }
            // A long-comment delimiter inside an ordinary line comment has no Lua meaning.
            while (i + 1 < lua.Length && lua[i + 1] is not ('\r' or '\n')) i++;
        }
        return new string(result);
    }
}
