using System.IO;
using System.Net.Http;

namespace LuaToolsGui.Services;

/// <summary>Bounded GitHub metadata/download transport with the original capability-matched fallback origins.</summary>
public class GithubProxy
{
    private readonly HttpClient _http;
    public GithubProxy() : this(AppHttp.Create(TimeSpan.FromMinutes(5))) { }
    internal GithubProxy(HttpClient http) => _http = http;

    public static IEnumerable<string> Candidates(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        new OutboundNetworkPolicy().Validate(uri);
        if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.Host is not ("github.com" or "api.github.com" or "raw.githubusercontent.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com"))
            throw new HttpRequestException("Unexpected GitHub origin.");
        yield return url;
        // Never forward signed/credential-bearing URL queries to a mirror. The only mirrored query
        // parameters are the original public release-list pagination contract.
        if (uri.Query.Length > 0 && (uri.Host != "api.github.com" || !IsPublicPagination(uri.Query))) yield break;
        var mirrors = uri.Host == "api.github.com" ? AppConfig.GithubApiMirrors : AppConfig.GithubDownloadMirrors;
        if (uri.Host is not ("github.com" or "api.github.com" or "raw.githubusercontent.com" or "objects.githubusercontent.com")) yield break;
        foreach (string mirror in mirrors)
        {
            string candidate = mirror + url;
            new OutboundNetworkPolicy().Validate(new Uri(candidate));
            yield return candidate;
        }
    }

    private static bool IsPublicPagination(string query) => query.TrimStart('?').Split('&').All(part =>
    {
        string[] pair = part.Split('=');
        return pair.Length == 2 && pair[0] is "page" or "per_page"
            && pair[1].Length is > 0 and <= 3 && pair[1].All(char.IsAsciiDigit);
    });

    public async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct = default)
    {
        HttpResponseMessage? last = null;
        foreach (string candidate in Candidates(url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.TryAddWithoutValidation("User-Agent", "LuaToolsGui");
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                // Central response content enforces the metadata byte/time budget even for callers
                // using ReadAsStringAsync. Caller owns the returned response.
                HttpResponseBudget.Json.Apply(req);
                var response = await _http.SendAsync(req, ct);
                if (response.IsSuccessStatusCode) { last?.Dispose(); return response; }
                last?.Dispose();
                last = response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { last?.Dispose(); throw; }
            catch (InvalidDataException) { last?.Dispose(); throw; }
            catch (OperationCanceledException) { /* request timeout: next source-defined origin */ }
            catch (HttpRequestException) { /* try the next source-defined origin */ }
        }
        return last;
    }

    public async Task DownloadAsync(string url, string destPath, IProgress<double?>? progress, CancellationToken ct = default)
    {
        const long maxBytes = 512L * 1024 * 1024;
        Exception? last = null;
        foreach (string candidate in Candidates(url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.TryAddWithoutValidation("User-Agent", "LuaToolsGui");
                HttpResponseBudget.Download.Apply(req);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                long? total = res.Content.Headers.ContentLength;
                if (total > maxBytes) throw new InvalidDataException("Download exceeds the size limit.");
                using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bodyTimeout.CancelAfter(TimeSpan.FromMinutes(5));
                var readCt = bodyTimeout.Token;
                ManifestContentPolicy.EnsureNoReparsePoints(destPath);
                await using var src = await res.Content.ReadAsStreamAsync(readCt);
                await using var dst = File.Create(destPath);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, readCt)) > 0)
                {
                    written += read;
                    if (written > maxBytes) throw new InvalidDataException("Download exceeds the size limit.");
                    await dst.WriteAsync(buffer.AsMemory(0, read), readCt);
                    progress?.Report(total is > 0 ? (double)written / total.Value : null);
                }
                if (total.HasValue && written != total.Value) throw new InvalidDataException("Incomplete download.");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { File.Delete(destPath); } catch { }
                throw;
            }
            catch (InvalidDataException)
            {
                try { File.Delete(destPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                try { File.Delete(destPath); } catch { }
            }
        }
        throw last ?? new HttpRequestException("GitHub download failed.");
    }
}
