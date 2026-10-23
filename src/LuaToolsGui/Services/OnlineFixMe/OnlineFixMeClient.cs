using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services.OnlineFixMe;

public sealed class OnlineFixMeClient
{
    private static readonly HttpResponseBudget ListingBudget = new(1024 * 1024, TimeSpan.FromSeconds(15));
    private static readonly HttpResponseBudget ArchiveBudget = new(ManifestContentPolicy.MaxArchiveBytes, TimeSpan.FromMinutes(5));
    private static readonly Regex CookiePair = new(@"\A[A-Za-z0-9_-]{1,64}=[A-Za-z0-9%._~+/=-]{1,4096}\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public OnlineFixMeClient() : this(AppHttp.Create(TimeSpan.FromMinutes(5))) { }

    internal OnlineFixMeClient(HttpClient http, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<OnlineFixMeLookup> LookupAsync(string? gameName, string? folderUrlOverride, bool fresh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<Uri> folders;
        if (!string.IsNullOrEmpty(folderUrlOverride))
        {
            if (OnlineFixMeListing.FolderFromUserUrl(folderUrlOverride) is not { } folder)
                return new(OnlineFixMeLookupStatus.InvalidFolder, null, null, []);
            folders = [folder];
        }
        else folders = OnlineFixMeListing.CandidateFolderNames(gameName).Select(n => OnlineFixMeListing.FolderFromName(n)!).ToArray();
        OnlineFixMeLookup last = new(OnlineFixMeLookupStatus.InvalidFolder, null, null, []);
        foreach (Uri folder in folders)
        {
            if (!fresh && _cache.TryGetValue(folder.AbsoluteUri, out var cached)
                && _now() - cached.At is var age && age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(5))
                last = cached.Lookup;
            else
            {
                (last, _) = await ReadListingAsync(folder, collectCookies: false, ct);
                // A transient outage must not hide the provider for the whole cache window.
                if (last.Status != OnlineFixMeLookupStatus.Unavailable) _cache[folder.AbsoluteUri] = new(_now(), last);
            }
            if (last.Status != OnlineFixMeLookupStatus.NotFound) return last;
        }
        return last;
    }

    public async Task<DownloadedFile> DownloadArchiveAsync(Uri folder, string archiveName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!OnlineFixMeListing.IsGameFolder(folder)) throw new ArgumentException("Invalid Online-Fix.me game folder.", nameof(folder));
        if (!OnlineFixMeListing.IsEligibleArchiveName(archiveName)) throw new InvalidDataException("Invalid Fix Repair archive name.");
        ManifestContentPolicy.ValidateRelativePath(archiveName);
        var (listing, cookie) = await ReadListingAsync(folder, collectCookies: true, ct);
        var archive = listing.Archives.FirstOrDefault(a => a.Name.Equals(archiveName, StringComparison.Ordinal));
        if (listing.Status != OnlineFixMeLookupStatus.Found || archive is null)
            throw new InvalidOperationException("The Fix Repair archive is not available in the fresh folder listing.");
        if (cookie is null) throw new InvalidOperationException("Online-Fix.me did not provide a valid anonymous download session.");

        using var request = CreateRequest(archive.Url, ArchiveBudget);
        request.Headers.Referrer = OnlineFixMeListing.FixRepairUrl(folder);
        request.Headers.Add("Cookie", cookie);
        HttpCredentialPurpose.OnlineFixMe.Apply(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ArchiveBudget.BodyTimeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ManifestContentPolicy.MaxArchiveBytes)
            throw new InvalidDataException("Fix Repair archive exceeds the size limit.");
        // The fresh listing names the archive; server-supplied filenames cannot replace that name.
        response.Content.Headers.Remove("Content-Disposition");
        return await HttpFileDownloader.SaveResponseAsync(response, archiveName, progress, timeout.Token).ConfigureAwait(false);
    }

    private async Task<(OnlineFixMeLookup Lookup, string? Cookie)> ReadListingAsync(Uri folder, bool collectCookies, CancellationToken ct)
    {
        string? name = OnlineFixMeListing.DecodeSegment(folder.AbsolutePath.Split('/')[2]);
        OnlineFixMeLookup Result(OnlineFixMeLookupStatus status, IReadOnlyList<OnlineFixMeArchive>? archives = null) =>
            new(status, folder, name, archives ?? []);
        try
        {
            using var request = CreateRequest(OnlineFixMeListing.FixRepairUrl(folder), ListingBudget);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ListingBudget.BodyTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return (Result(OnlineFixMeLookupStatus.NotFound), null);
            if (response.StatusCode != HttpStatusCode.OK) return (Result(OnlineFixMeLookupStatus.Unavailable), null);
            string html = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!OnlineFixMeListing.LooksLikeIndex(html)) return (Result(OnlineFixMeLookupStatus.Unavailable), null);
            var archives = OnlineFixMeListing.ParseFixRepairListing(html, folder);
            return (Result(archives.Count > 0 ? OnlineFixMeLookupStatus.Found : OnlineFixMeLookupStatus.NotFound, archives),
                collectCookies ? ReadCookies(response) : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return (Result(OnlineFixMeLookupStatus.Unavailable), null);
        }
    }

    private static HttpRequestMessage CreateRequest(Uri url, HttpResponseBudget budget)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(AppConfig.OnlineFixMeUserAgent);
        budget.Apply(request);
        return request;
    }

    private static string? ReadCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        var cookies = new List<string>(4);
        foreach (string value in values)
        {
            int end = value.IndexOf(';');
            string pair = (end < 0 ? value : value[..end]).Trim();
            if (pair.Length > 4161 || !CookiePair.IsMatch(pair)) continue;
            cookies.Add(pair);
            if (cookies.Count == 4) break;
        }
        return cookies.Count > 0 ? string.Join("; ", cookies) : null;
    }

    private sealed record CacheEntry(DateTimeOffset At, OnlineFixMeLookup Lookup);
}
