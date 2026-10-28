using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services;

/// <summary>Public, declarative Lua scripts. Availability does not imply downloadable depot manifests.</summary>
public sealed class DexLuaService
{
    public const string SourceName = "DexLua";
    private static readonly HttpResponseBudget Budget = new(ManifestContentPolicy.MaxLuaBytes, TimeSpan.FromSeconds(30));
    private static readonly Lazy<HttpClient> Client = new(() => AppHttp.CreateExternalDataClient(
        TimeSpan.FromSeconds(30), new Uri(AppConfig.DexLuaBaseUrl), Budget));
    private readonly HttpClient _http;
    public DexLuaService() : this(Client.Value) { }
    internal DexLuaService(HttpClient http) => _http = http;

    public async Task<string> CheckStatusAsync(string appid, CancellationToken ct = default)
    {
        ApiContract.RequireAppId(appid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await FetchAsync(appid, timeout.Token); return "available"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound) { return "unavailable"; }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or OperationCanceledException or InvalidDataException)
        { return "unknown"; }
    }

    private async Task<byte[]> FetchAsync(string appid, CancellationToken ct)
    {
        ApiContract.RequireAppId(appid);
        using var request = new HttpRequestMessage(HttpMethod.Get, AppConfig.DexLuaBaseUrl + "api/download?id=" + appid);
        Budget.Apply(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ApiException(string.Format(Resources.Strings.DexLua_DownloadFailed, (int)response.StatusCode), response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        try { return Normalize(bytes, appid); }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or RegexMatchTimeoutException)
        { throw new ApiException(Resources.Strings.DexLua_InvalidScript); }
    }

    internal static byte[] Normalize(byte[] bytes, string appid)
    {
        ApiContract.RequireAppId(appid);
        if (bytes.Length > ManifestContentPolicy.MaxLuaBytes) throw new InvalidDataException();
        string text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        // DexLua uses addappid(id, 1, "title") for app labels. Strip only that exact
        // non-key form; never evaluate Lua or loosen the shared declaration policy.
        text = Regex.Replace(text, "^[ \\t]*addappid\\([ \\t]*([1-9][0-9]{0,9})[ \\t]*,[ \\t]*1[ \\t]*,[ \\t]*\"(?:[^\"\\\\\\r\\n]|\\\\[\"\\\\])*\"[ \\t]*\\)[ \\t]*$",
            m => Regex.IsMatch(m.Value, "\"[0-9a-fA-F]{1,64}\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                ? m.Value : $"addappid({m.Groups[1].Value}, 1)", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var normalized = Encoding.UTF8.GetBytes(text);
        ManifestContentPolicy.ValidateLua(normalized, long.Parse(appid, CultureInfo.InvariantCulture));
        return normalized;
    }

    /// <summary>Active depot keys only, from the validated base-game script.</summary>
    internal async Task<IReadOnlyDictionary<long, string>> GetKeysAsync(string appid, CancellationToken ct)
    {
        var bytes = await FetchAsync(appid, ct);
        var keys = new Dictionary<long, string>();
        foreach (Match match in Regex.Matches(Encoding.UTF8.GetString(bytes),
            """^[ \t]*addappid[ \t]*\([ \t]*([1-9][0-9]{0,9})[ \t]*,[ \t]*[01][ \t]*,[ \t]*["']([0-9a-fA-F]{64})["'][ \t]*\)""",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            keys[long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] = match.Groups[2].Value.ToLowerInvariant();
        return keys;
    }

    public async Task<DownloadedFile> DownloadManifestAsync(string appid, IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        var bytes = await FetchAsync(appid, ct);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        return await HttpFileDownloader.SaveResponseAsync(response, appid + ".lua", progress, ct);
    }
}
