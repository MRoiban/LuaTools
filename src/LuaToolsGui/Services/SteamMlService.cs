using System.Net;
using System.Net.Http;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services;

/// <summary>SteamML's public app-ID ZIPs. No account, key, or lua.tools quota.</summary>
public sealed class SteamMlService
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
