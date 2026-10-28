using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public record DownloadedFile(string FilePath, string FileName);

/// <summary>Typed client for the lua.tools web API, authenticated with a Supabase bearer token.</summary>
public class LuaToolsApiClient
{
    private readonly AuthService auth;
    private readonly SteamAppInfoCache appInfo;
    private readonly CoverCache covers;
    private readonly HttpClient _http;
    private readonly SteamMlService _steamMl;
    private readonly DexLuaService _dexLua;
    private readonly SteamDepotInfo _depots;
    /// <summary>SteamML archives, for signed-out depot downloads.</summary>
    public SteamMlService SteamMl => _steamMl;
    private readonly Func<Uri, HttpClient> _externalClient;

    public LuaToolsApiClient(AuthService auth, SteamAppInfoCache appInfo, CoverCache covers, SteamDepotInfo depots)
        : this(auth, appInfo, covers, AppHttp.Create(TimeSpan.FromMinutes(5), new Uri(AppConfig.ApiBaseUrl)), depots: depots) { }

    internal LuaToolsApiClient(AuthService auth, SteamAppInfoCache appInfo, CoverCache covers, HttpClient http, Func<Uri, HttpClient>? externalClient = null, SteamMlService? steamMl = null,
        SteamDepotInfo? depots = null, DexLuaService? dexLua = null)
    {
        _steamMl = steamMl ?? new SteamMlService();
        _dexLua = dexLua ?? new DexLuaService();
        _depots = depots ?? new SteamDepotInfo();
        this.auth = auth;
        this.appInfo = appInfo;
        this.covers = covers;
        _http = http;
        _externalClient = externalClient ?? (uri => AppHttp.CreateExternalDataClient(TimeSpan.FromMinutes(5), uri, HttpResponseBudget.Download));
        _http.BaseAddress ??= new Uri(AppConfig.ApiBaseUrl);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, MaxDepth = 32 };

    // ── Endpoints ───────────────────────────────────────────────────

    public async Task<List<SteamSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Calls Steam's public store-search directly (no lua.tools, no auth). Guests can search.
        var url = $"{AppConfig.SteamStoreSearchUrl}?term={Uri.EscapeDataString(query)}&l=english&cc=US";
        using var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return [];

        var data = await ReadJsonAsync<SteamStoreSearchResponse>(res, ct);
        return (data?.Items ?? [])
            .Where(i => i.Id > 0 && i.Id <= uint.MaxValue && ApiContract.ValidText(i.Name, 512))
            .Take(8)
            .Select(i => new SteamSearchResult
            {
                AppId = i.Id,
                Name = i.Name,
                Icon = i.TinyImage ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{i.Id}/capsule_sm_120.jpg",
            })
            .ToList();
    }

    /// <summary>Steam's featured "top sellers" + "new releases" lists for the Add page strips. Public,
    /// no auth. Returns empty lists on any failure (the strips just don't show). Each list keeps only real
    /// games (type 0) that have a capsule image, capped to keep the strips light.</summary>
    public async Task<(List<SteamFeaturedItem> TopSellers, List<SteamFeaturedItem> NewReleases)> GetFeaturedAsync(
        CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.GetAsync($"{AppConfig.SteamFeaturedUrl}?cc=us&l=english", ct);
            if (!res.IsSuccessStatusCode) return ([], []);
            var data = await ReadJsonAsync<SteamFeaturedResponse>(res, ct);

            // Steam's featuredcategories genuinely repeats appids within a list (e.g. top_sellers returns
            // the same game 2–3×), so DistinctBy the appid. Keeps the first, preserving Steam's order.
            static List<SteamFeaturedItem> Clean(SteamFeaturedCategory? c) =>
                (c?.Items ?? [])
                    .Where(i => i.Type == 0 && i.Id > 0 && i.Id <= uint.MaxValue && ApiContract.ValidText(i.Name, 512) && !string.IsNullOrEmpty(i.LargeCapsuleImage))
                    .DistinctBy(i => i.Id)
                    .Take(20)
                    .ToList();

            return (Clean(data?.TopSellers), Clean(data?.NewReleases));
        }
        catch { return ([], []); }
    }

    /// <summary>Public endpoint, no auth required.</summary>
    /// <summary>Game metadata straight from Steam's appdetails (cached to details\&lt;appid&gt;.json via the
    /// throttle, interactive priority), no lua.tools proxy. ANY fetch path funnels through here (normal /
    /// DLC / fast / plugin add), so this is also where the header image gets warmed into covers\.</summary>
    public async Task<GameDetails?> GetDetailsAsync(string appid, CancellationToken ct = default)
    {
        if (!ApiContract.IsAppId(appid)) return null;
        long id = long.Parse(appid, System.Globalization.CultureInfo.InvariantCulture);
        var details = await appInfo.ResolveGameDetailsAsync(id, ct);
        if (details is { HeaderImage: { Length: > 0 } img })
            _ = covers.EnsureAsync(id, img, CancellationToken.None); // warm the cover cache (best-effort)
        return details;
    }

    /// <summary>Source name → "available" | "unavailable" | other status.</summary>
    /// <remarks>Signed out, only sources that need no account are checked: lua.tools sources would
    /// only fail at download.</remarks>
    public async Task<Dictionary<string, string>> CheckSourcesAsync(string appid, CancellationToken ct = default)
    {
        ApiContract.RequireAppId(appid);
        var dexLua = _dexLua.CheckStatusAsync(appid, ct);
        var sources = auth.IsGuest
            ? new Dictionary<string, string> { [SteamMlService.SourceName] = await _steamMl.CheckStatusAsync(appid, ct) }
            : await _steamMl.CheckSourcesAsync(appid, (id, token) => SourceAvailabilityClient.CheckAsync(id, token), ct);
        sources[DexLuaService.SourceName] = await dexLua;
        return sources;
    }

    /// <summary>
    /// The standard lua.tools daily download usage (25/day), counted live from the user_downloads
    /// table via Supabase REST. The same source the website reads. RLS scopes it to the signed-in
    /// user, so no user id is needed. Null on failure / not signed in.
    /// </summary>
    public async Task<StandardUsage?> GetStandardUsageAsync(CancellationToken ct = default)
    {
        try
        {
            // Count today's rows without fetching them: HEAD + Prefer: count=exact → Content-Range header.
            var todayUtc = DateTime.UtcNow.Date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string url = $"{AppConfig.SupabaseUrl}/rest/v1/user_downloads" +
                         $"?select=appid&downloaded_at=gte.{Uri.EscapeDataString(todayUtc)}";

            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            HttpCredentialPurpose.Supabase.Apply(req);
            req.Headers.TryAddWithoutValidation("apikey", AppConfig.SupabaseAnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetValidAccessTokenAsync());
            req.Headers.TryAddWithoutValidation("Prefer", "count=exact");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            // Content-Range: "0-24/25"  (or "*/0" when empty). The count is after the slash.
            string? range = res.Content.Headers.TryGetValues("Content-Range", out var v) ? v.FirstOrDefault()
                          : (res.Headers.TryGetValues("Content-Range", out var hv) ? hv.FirstOrDefault() : null);
            int used;
            if (range is not null && range.Split('/') is [_, var countStr] && int.TryParse(countStr, out int c) && c >= 0)
                used = c;
            else return null;
            return new StandardUsage(used, AppConfig.DailyDownloadLimit);
        }
        catch
        {
            return null; // decorative, never block on it
        }
    }

    public async Task<SupporterStatus?> GetSupporterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await SendAsync(HttpMethod.Get, "/api/me/supporter-status", ct);
            return await ReadJsonAsync<SupporterStatus>(res, ct);
        }
        catch { return null; }
    }

    public async Task<DlcInfo?> GetDlcInfoAsync(string appid, string baseAppId, CancellationToken ct = default)
    {
        ApiContract.RequireAppId(appid);
        ApiContract.RequireAppId(baseAppId);
        if (auth.IsGuest) return await GuestDlcInfoAsync(appid, baseAppId, ct);
        using var res = await SendAsync(HttpMethod.Get, $"/api/dlc/info?appid={Uri.EscapeDataString(appid)}&base={Uri.EscapeDataString(baseAppId)}", ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        string? mediaType = res.Content.Headers.ContentType?.MediaType;
        var (data, reason) = ParseDlcInfo(mediaType, body, appid, baseAppId);
        if (data is null)
        {
            // Sanitized: ids, status, media type and a fixed reason code only. Never the body.
            PluginLog.Log($"DlcInfo.Invalid appid={appid} base={baseAppId} status={(int)res.StatusCode} type={mediaType} reason={reason}");
            throw new ApiException(Resources.Strings.Api_Err_InvalidDlcResponse);
        }
        return data;
    }

    /// <summary>Validate a <c>/api/dlc/info</c> body. Returns the info, or null plus a reason code.</summary>
    /// <remarks>The live API identifies the response by the BASE game it resolved, not the DLC:
    /// <c>appid=1670540&amp;base=1142710</c> answers <c>{"appid":"1142710","name":"Total War: WARHAMMER III",…}</c>.
    /// So the echo binds the response to this request when it matches either requested id; anything
    /// else is rejected. Zero depots with zero counts is a valid DLC with no separate content.</remarks>
    internal static (DlcInfo? Info, string Reason) ParseDlcInfo(string? mediaType, string body, string appid, string baseAppId)
    {
        if (mediaType is not null && mediaType != "application/json" && !mediaType.EndsWith("+json", StringComparison.Ordinal))
            return (null, "content-type");
        DlcInfo? data;
        try { data = JsonSerializer.Deserialize<DlcInfo>(body, JsonOpts); }
        catch (JsonException) { return (null, "malformed-json"); }
        if (data is null) return (null, "empty");
        if (data.AppId != appid && data.AppId != baseAppId) return (null, "appid-mismatch");
        if (data.Depots is null || data.Depots.Count > 10000) return (null, "depots");
        if (data.DepotCount < 0 || data.HaveCount < 0 || data.MissingCount < 0) return (null, "counts");
        if (data.Depots.Any(d => !ApiContract.IsAppId(d.DepotId))) return (null, "depot-id");
        return (data, "");
    }

    public Task<DownloadedFile> DownloadManifestAsync(
        string appid, string source, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        ApiContract.RequireAppId(appid);
        if (source == DexLuaService.SourceName)
            return _dexLua.DownloadManifestAsync(appid, progress, ct);
        if (source == SteamMlService.SourceName)
            return _steamMl.DownloadManifestAsync(appid, progress, ct);
        if (!ApiContract.ValidText(source, 128)) throw new ArgumentException("Invalid source.");
        string url = $"/api/manifest/download?appid={Uri.EscapeDataString(appid)}&source={Uri.EscapeDataString(source)}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.zip", progress, ct);
    }

    /// <summary>
    /// One depot's raw <c>.manifest</c> by id, so a depot download no longer depends on Steam happening
    /// to have the file in its depotcache.
    /// </summary>
    /// <remarks>
    /// Unlike every other endpoint here the ids go in the PATH, not the query string. Auth is the same
    /// Bearer token as the rest, and the response is raw bytes on 200 / a JSON error otherwise, which
    /// <see cref="SendAsync"/> already turns into an <see cref="ApiException"/>.
    ///
    /// <para>This one writes no history row and does NOT consume the daily download cap. It is instead
    /// limited to 120 requests per 10 minutes per user, and only cache misses count. A large game is
    /// ~20 depots, comfortably inside that — which is why manifests are fetched lazily per depot at
    /// download time rather than eagerly when the picker opens.</para>
    /// </remarks>
    public Task<DownloadedFile> DownloadDepotManifestAsync(
        long depotId, string manifestId,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (depotId <= 0 || depotId > uint.MaxValue || !ulong.TryParse(manifestId, out var id) || id == 0
            || manifestId.Length > 20 || manifestId[0] == '0'
            || manifestId.Any(c => c < '0' || c > '9'))
            throw new ArgumentException("Invalid depot or manifest identifier.");
        return DownloadFileAsync($"/api/givemethemanifestpunk/{depotId}/{manifestId}",
            $"{depotId}_{manifestId}.manifest", progress, ct);
    }

    public Task<DownloadedFile> GenerateDlcAsync(
        string appid, string baseAppId, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        ApiContract.RequireAppId(appid);
        ApiContract.RequireAppId(baseAppId);
        if (auth.IsGuest) return GuestGenerateDlcAsync(appid, baseAppId, ct);
        string url = $"/api/dlc/generate?appid={Uri.EscapeDataString(appid)}&base={Uri.EscapeDataString(baseAppId)}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.lua", progress, ct);
    }

    // ── Signed-out DLC, from the base game's SteamML archive ─────────

    /// <summary>The DLC's depots (from the base game's public app info) and which of them the base
    /// game's SteamML archive has a key for: the same shape and rule as <c>/api/dlc/info</c>.</summary>
    private async Task<DlcInfo> GuestDlcInfoAsync(string appid, string baseAppId, CancellationToken ct)
    {
        var (depots, keys) = await GuestDlcDepotsAsync(appid, baseAppId, ct);
        return new DlcInfo
        {
            AppId = baseAppId, Type = "dlc", DepotCount = depots.Count,
            HaveCount = depots.Count(d => keys.ContainsKey(d.Id)),
            MissingCount = depots.Count(d => !keys.ContainsKey(d.Id)),
            Depots = depots.Select(d => new DlcDepot
            {
                DepotId = d.Id.ToString(), Language = d.Language, OsList = d.Os, Included = keys.ContainsKey(d.Id),
            }).ToList(),
        };
    }

    /// <summary>A <c>&lt;dlc&gt;.lua</c> (plus its depots' manifests when SteamML has them), as
    /// <c>/api/dlc/generate</c> returns it. A DLC with no content of its own is the one-line lua.</summary>
    private async Task<DownloadedFile> GuestGenerateDlcAsync(string appid, string baseAppId, CancellationToken ct)
    {
        var (depots, keys) = await GuestDlcDepotsAsync(appid, baseAppId, ct);
        var archive = depots.Count > 0 ? await _steamMl.GetArchiveAsync(baseAppId, ct) : null;
        var lua = new System.Text.StringBuilder($"addappid({appid})\n");
        var manifests = new List<string>();
        string dir = Path.Combine(HttpFileDownloader.StagingFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var d in depots)
        {
            if (!keys.TryGetValue(d.Id, out var key)) continue;
            lua.Append($"addappid({d.Id},0,\"{key}\")\n");
            if (archive?.ManifestIds.TryGetValue(d.Id, out var mid) == true)
            {
                lua.Append($"setManifestid({d.Id},\"{mid}\")\n");
                if (SteamMlService.ExtractManifest(archive, d.Id, mid, dir) is { } path) manifests.Add(path);
            }
        }
        string luaPath = Path.Combine(dir, $"{appid}.lua");
        await File.WriteAllTextAsync(luaPath, lua.ToString(), ct);
        if (manifests.Count == 0) return new DownloadedFile(luaPath, $"{appid}.lua");
        string zipPath = Path.Combine(dir, $"{appid}.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(luaPath, $"{appid}.lua");
            foreach (var m in manifests) zip.CreateEntryFromFile(m, Path.GetFileName(m));
        }
        return new DownloadedFile(zipPath, $"{appid}.zip");
    }

    private async Task<(List<ContentDepot> Depots, IReadOnlyDictionary<long, string> Keys)> GuestDlcDepotsAsync(
        string appid, string baseAppId, CancellationToken ct)
    {
        long dlcId = long.Parse(appid);
        var info = await _depots.GetAsync(long.Parse(baseAppId), ct)
            ?? throw new ApiException(Resources.Strings.Add_Err_Generic);
        var depots = info.Depots.Where(d => d.DlcAppId == dlcId).ToList();
        if (depots.Count == 0) return (depots, new Dictionary<long, string>());
        var archive = await _steamMl.GetArchiveAsync(baseAppId, ct)
            ?? throw new ApiException(Resources.Strings.SteamMl_NoArchive);
        return (depots, archive.Keys);
    }

    // ── Denuvo fixes ────────────────────────────────────────────────

    /// <summary>Public. Every game that has at least one Denuvo fix, plus the tag catalogue.</summary>
    public async Task<DenuvoListingsResponse?> GetDenuvoListingsAsync(CancellationToken ct = default)
    {
        using var res = await _http.GetAsync("/api/denuvo/listings", ct);
        if (!res.IsSuccessStatusCode) return null;
        var data = await ReadJsonAsync<DenuvoListingsResponse>(res, ct);
        if (data is null || data.Games is null || data.Tags is null || data.Games.Count > 10000
            || data.Games.Any(g => !ApiContract.IsAppId(g.AppId) || !ApiContract.ValidText(g.Name, 512) || g.FixCount < 0))
            throw new ApiException("Invalid catalogue response.");
        return data;
    }

    /// <summary>Public. One game's fixes (id/title/desc/tags + which download slots exist).</summary>
    public async Task<DenuvoFixesResponse?> GetDenuvoFixesAsync(string appid, CancellationToken ct = default)
    {
        ApiContract.RequireAppId(appid);
        using var res = await _http.GetAsync($"/api/denuvo/fixes?appid={Uri.EscapeDataString(appid)}", ct);
        if (!res.IsSuccessStatusCode) return null; // 404 = no fixes for this appid
        var data = await ReadJsonAsync<DenuvoFixesResponse>(res, ct);
        if (data is null || data.AppId != appid || data.Fixes is null || data.Fixes.Count > 1000
            || data.Fixes.Any(f => !ApiContract.ValidFixId(f.Id) || !ApiContract.ValidText(f.Title, 512)))
            throw new ApiException("Invalid fixes response.");
        return data;
    }

    /// <summary>
    /// Auth: download a fix's "manifest" or "fix" slot. The endpoint returns a short-lived signed
    /// R2 URL (counts toward 25/day); we then fetch the file from that URL. Caller must be signed in.
    /// </summary>
    public async Task<DownloadedFile> DownloadDenuvoAsync(
        string fixId, string slot, string fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (!ApiContract.ValidFixId(fixId) || slot is not ("fix" or "manifest"))
            throw new ArgumentException("Invalid fix or slot identifier.");
        // 1. Ask the API for a signed URL (auth + daily-limit gate live here).
        using var res = await SendAsync(HttpMethod.Get,
            $"/api/denuvo/download?fix={Uri.EscapeDataString(fixId)}&slot={Uri.EscapeDataString(slot)}", ct);
        var signed = await ReadJsonAsync<DenuvoDownloadResponse>(res, ct);
        if (string.IsNullOrWhiteSpace(signed?.Url))
            throw new ApiException(Resources.Strings.Api_Err_EmptyDownloadLink);

        // 2. Fetch the file from R2 (no auth header: the signed URL carries its own credentials).
        return await DownloadFromUrlAsync(signed.Url, fallbackName, progress, ct);
    }

    // ── Plumbing ────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        // All callers of SendAsync are login-gated endpoints; the caller ensures the user is signed in.
        using var req = new HttpRequestMessage(method, url);
        if (completion == HttpCompletionOption.ResponseHeadersRead) HttpResponseBudget.Download.Apply(req);
        HttpCredentialPurpose.LuaToolsApi.Apply(req);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetValidAccessTokenAsync());

        var res = await _http.SendAsync(req, completion, ct);
        if (res.IsSuccessStatusCode) return res;

        string message = string.Format(Resources.Strings.Api_Err_RequestFailed, (int)res.StatusCode);
        if (res.StatusCode == HttpStatusCode.Unauthorized) message = Resources.Strings.Api_Err_SessionExpired;
        var status = res.StatusCode;
        res.Dispose();
        throw new ApiException(message, status);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    private async Task<DownloadedFile> DownloadFileAsync(
        string url, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, url, ct, HttpCompletionOption.ResponseHeadersRead);
        return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
    }

    /// <summary>Download a file from an absolute URL with NO auth header (e.g. a signed R2 link).</summary>
    private async Task<DownloadedFile> DownloadFromUrlAsync(
        string url, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // New request (not via SendAsync) so no Bearer header and the absolute URL isn't prefixed.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new ApiException("Invalid download destination.");
        using var client = _externalClient(uri);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseBudget.Download.Apply(req);
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApiException(string.Format(Resources.Strings.Api_Err_DownloadFailed, (int)res.StatusCode), res.StatusCode);
        return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
    }
}
