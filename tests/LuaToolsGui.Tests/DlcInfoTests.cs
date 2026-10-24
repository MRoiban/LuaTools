using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>/api/dlc/info contract and DLC routing (Total War: WARHAMMER III, "Invalid DLC response.").</summary>
public class DlcInfoTests
{
    // Live bodies captured 2026-10-24 (sanitized: they carry no user data). The API echoes the BASE appid.
    private const string OgreKingdoms = "{\"appid\":\"1142710\",\"name\":\"Total War: WARHAMMER III\",\"depotCount\":0,\"haveCount\":0,\"missingCount\":0,\"depots\":[]}";
    private const string WithDepots = "{\"appid\":\"1142710\",\"name\":\"Total War: WARHAMMER III\",\"depotCount\":3,\"haveCount\":3,\"missingCount\":0,\"depots\":["
        + "{\"depotId\":\"1143201\",\"language\":null,\"oslist\":\"macos\",\"included\":true},"
        + "{\"depotId\":\"1143217\",\"language\":null,\"oslist\":\"linux\",\"included\":true},"
        + "{\"depotId\":\"1374300\",\"language\":null,\"oslist\":\"windows\",\"included\":true}]}";
    private const string Session = "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":3600,\"user\":{\"id\":\"db101b52-9f32-4663-8896-bdc8fd58b651\"}}";

    private static HttpResponseMessage Respond(string body, string mediaType = "application/json", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(response(request));
        }
    }

    /// <summary>A signed-in client whose lua.tools API answers /api/dlc/info with <paramref name="dlcInfo"/>.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "DlcInfoTests-" + Guid.NewGuid().ToString("N"));
        internal readonly Stub Transport;
        internal readonly HttpClient Http;
        internal readonly AuthService Auth;
        internal readonly LuaToolsApiClient Api;
        internal readonly SteamAppInfoCache AppInfo;
        internal string? LastDlcQuery;

        internal Fixture(Func<HttpResponseMessage> dlcInfo)
        {
            Directory.CreateDirectory(Path.Combine(directory, "details"));
            Transport = new Stub(r =>
            {
                string path = r.RequestUri!.AbsolutePath;
                if (path == "/api/auth/code/redeem") return Respond("{\"token\":\"test-token\"}");
                if (path == "/auth/v1/verify") return Respond(Session);
                if (path == "/api/dlc/info")
                {
                    Assert.Equal("test-access", r.Headers.Authorization!.Parameter);
                    LastDlcQuery = r.RequestUri.Query;
                    return dlcInfo();
                }
                throw new Exception("Unexpected request " + path);
            });
            Http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), Transport);
            Http.BaseAddress = new Uri(AppConfig.ApiBaseUrl);
            Auth = new AuthService(Http, Path.Combine(directory, "auth.dat"));
            AppInfo = new SteamAppInfoCache(new CacheService(Path.Combine(directory, "cache.json")),
                NullLogger<SteamAppInfoCache>.Instance, new Stub(_ => throw new Exception("Steam was reached")), Path.Combine(directory, "details"));
            Api = new LuaToolsApiClient(Auth, AppInfo, new CoverCache(Transport, Path.Combine(directory, "covers")), Http);
        }

        internal Task SignInAsync() => Auth.SignInWithCodeAsync("abc123");

        /// <summary>Cache a Steam appdetails blob, as the details\ cache stores it.</summary>
        internal void Details(long appid, string type, string? fullgame, string name = "Test")
        {
            string parent = fullgame is null ? "" : $",\"fullgame\":{{\"appid\":\"{fullgame}\",\"name\":\"Parent\"}}";
            File.WriteAllText(Path.Combine(directory, "details", $"{appid}.json"),
                $"{{\"type\":\"{type}\",\"name\":\"{name}\",\"steam_appid\":{appid}{parent}}}");
        }

        public void Dispose()
        {
            Http.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // ── Response validation ─────────────────────────────────────────

    [Fact]
    public void LiveZeroDepotResponseEchoingBaseAppIdIsValidAndGeneratable()
    {
        var (info, reason) = LuaToolsApiClient.ParseDlcInfo("application/json", OgreKingdoms, "1670540", "1142710");
        Assert.Equal("", reason);
        Assert.NotNull(info);
        Assert.Empty(info!.Depots);
        Assert.True(info.HaveCount > 0 || info.MissingCount == 0); // CanGenerateDlc rule
    }

    [Fact]
    public void LiveResponseWithDepotsIsValid()
    {
        var (info, _) = LuaToolsApiClient.ParseDlcInfo("application/json", WithDepots, "1374300", "1142710");
        Assert.Equal(["1143201", "1143217", "1374300"], info!.Depots.Select(d => d.DepotId));
        Assert.All(info.Depots, d => Assert.True(d.Included));
    }

    [Fact]
    public void ResponseEchoingTheDlcAppIdIsAlsoValid()
    {
        var (info, _) = LuaToolsApiClient.ParseDlcInfo("application/json", OgreKingdoms.Replace("1142710", "1670540"), "1670540", "1142710");
        Assert.NotNull(info);
    }

    [Theory]
    [InlineData("application/json", "{\"appid\":\"594570\",\"depotCount\":0,\"haveCount\":0,\"missingCount\":0,\"depots\":[]}", "appid-mismatch")]
    [InlineData("application/json", "{\"error\":\"Unauthorized\"}", "appid-mismatch")]
    [InlineData("application/json", "{}", "appid-mismatch")]
    [InlineData("text/html", "<!DOCTYPE html><html><body>Login</body></html>", "content-type")]
    [InlineData("application/json", "<!DOCTYPE html><html></html>", "malformed-json")]
    [InlineData("application/json", "{\"appid\":1142710,\"depots\":[]}", "malformed-json")]
    [InlineData("application/json", "[]", "malformed-json")]
    [InlineData("application/json", "null", "empty")]
    [InlineData("application/json", "{\"appid\":\"1142710\",\"depots\":null}", "depots")]
    [InlineData("application/json", "{\"appid\":\"1142710\",\"depotCount\":-1,\"depots\":[]}", "counts")]
    [InlineData("application/json", "{\"appid\":\"1142710\",\"depots\":[{\"depotId\":\"../1\"}]}", "depot-id")]
    [InlineData("application/json", "{\"appid\":\"1142710\",\"depots\":[{\"depotId\":\"0\"}]}", "depot-id")]
    public void MalformedOrUnboundResponsesAreRejected(string mediaType, string body, string reason)
    {
        var (info, actual) = LuaToolsApiClient.ParseDlcInfo(mediaType, body, "1670540", "1142710");
        Assert.Null(info);
        Assert.Equal(reason, actual);
    }

    // ── Authenticated request ───────────────────────────────────────

    [Fact]
    public async Task OgreKingdomsRequestSendsDlcAndParentAndAcceptsTheLiveBody()
    {
        using var f = new Fixture(() => Respond(OgreKingdoms));
        await f.SignInAsync();
        var info = await f.Api.GetDlcInfoAsync("1670540", "1142710");
        Assert.Equal(0, info!.DepotCount);
        Assert.Equal("?appid=1670540&base=1142710", f.LastDlcQuery);
    }

    [Fact]
    public async Task HtmlSuccessIsAnInvalidDlcResponseNotAGenericFailure()
    {
        using var f = new Fixture(() => Respond("<html>maintenance</html>", "text/html"));
        await f.SignInAsync();
        var ex = await Assert.ThrowsAsync<ApiException>(() => f.Api.GetDlcInfoAsync("1670540", "1142710"));
        Assert.Equal(LuaToolsGui.Resources.Strings.Api_Err_InvalidDlcResponse, ex.Message);
    }

    [Fact]
    public async Task UnauthorizedIsReportedAsAnExpiredSession()
    {
        using var f = new Fixture(() => Respond("{\"error\":\"Unauthorized\"}", status: HttpStatusCode.Unauthorized));
        await f.SignInAsync();
        var ex = await Assert.ThrowsAsync<ApiException>(() => f.Api.GetDlcInfoAsync("1670540", "1142710"));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal(LuaToolsGui.Resources.Strings.Api_Err_SessionExpired, ex.Message);
    }

    [Fact]
    public async Task GuestsAndInvalidParentsNeverReachTheApi()
    {
        using var f = new Fixture(() => throw new Exception("API was reached"));
        await Assert.ThrowsAsync<AuthException>(() => f.Api.GetDlcInfoAsync("1670540", "1142710"));
        await f.SignInAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Api.GetDlcInfoAsync("1670540", "0"));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Api.GetDlcInfoAsync("1670540", "../1142710"));
        Assert.DoesNotContain("/api/dlc/info", f.Transport.Paths);
    }

    // ── Parent mapping ──────────────────────────────────────────────

    [Fact]
    public void ParentGameComesFromEachDlcsOwnFullgame()
    {
        using var f = new Fixture(() => throw new Exception("unused"));
        f.Details(1670540, "dlc", "1142710");  // WARHAMMER III - Ogre Kingdoms
        f.Details(1556110, "dlc", "594570");   // WARHAMMER II - The Silence & The Fury (legacy parent)
        f.Details(1142710, "game", null);
        f.Details(999999, "dlc", null);        // DLC with no parent data
        Assert.Equal("1142710", f.AppInfo.GetGameDetails(1670540)!.BaseAppId);
        Assert.Equal("594570", f.AppInfo.GetGameDetails(1556110)!.BaseAppId);
        Assert.False(f.AppInfo.GetGameDetails(1142710)!.IsDlc);
        var orphan = f.AppInfo.GetGameDetails(999999)!;
        Assert.True(orphan.IsDlc);
        Assert.Null(orphan.BaseAppId);
    }

    // ── Steam plugin routing ────────────────────────────────────────

    private static PluginAddService Plugin(Fixture f, bool fastFetch = false)
    {
        var settings = new SettingsService(new AppSettings { FastFetch = fastFetch });
        var queue = new DownloadQueue(new CacheService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")), NullLogger<DownloadQueue>.Instance);
        var jobs = new ManifestJobFactory(f.Api, new HubcapService(f.Http), settings, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        return new PluginAddService(f.Api, new HubcapService(f.Http), settings, f.Auth, queue, jobs);
    }

    private static async Task<PluginAddService.AddState> RunAsync(PluginAddService plugin, long appid)
    {
        Assert.True(plugin.Start(appid, "Store name"));
        var state = plugin.GetState(appid)!;
        await state.CheckTask.WaitAsync(TimeSpan.FromSeconds(10));
        return state;
    }

    [Fact]
    public async Task PluginRoutesDlcToDlcInfoInsteadOfManifestSources()
    {
        using var f = new Fixture(() => Respond(OgreKingdoms));
        f.Details(1670540, "dlc", "1142710", "Total War: WARHAMMER III - Ogre Kingdoms");
        await f.SignInAsync();
        var state = await RunAsync(Plugin(f), 1670540);
        Assert.Null(state.Error);
        Assert.True(state.SourcesLoaded);
        var row = Assert.Single(state.Sources);
        Assert.Equal(PluginAddService.DlcSourceName, row.Name);
        Assert.True(row.CanDownload);
        Assert.Equal("1142710", state.DlcBaseAppId);
        Assert.Equal("?appid=1670540&base=1142710", f.LastDlcQuery);
        Assert.Contains("/api/dlc/info", f.Transport.Paths);
    }

    [Fact]
    public async Task PluginDlcWithMissingKeysIsNotGeneratable()
    {
        using var f = new Fixture(() => Respond("{\"appid\":\"1142710\",\"depotCount\":1,\"haveCount\":0,\"missingCount\":1,\"depots\":[{\"depotId\":\"1670540\",\"included\":false}]}"));
        f.Details(1670540, "dlc", "1142710");
        await f.SignInAsync();
        var state = await RunAsync(Plugin(f, fastFetch: true), 1670540);
        Assert.False(Assert.Single(state.Sources).CanDownload);
        Assert.Equal(LuaToolsGui.Resources.Strings.Err_NoSourceAvailable, state.Error);
        Assert.False(state.Busy);
    }

    [Fact]
    public async Task PluginDlcRequiresSignInAndAParent()
    {
        using var f = new Fixture(() => throw new Exception("API was reached"));
        f.Details(1670540, "dlc", "1142710");
        f.Details(999999, "dlc", null);
        var guest = await RunAsync(Plugin(f), 1670540);
        Assert.Equal(LuaToolsGui.Resources.Strings.Add_SignIn_Dlc, guest.Error);
        await f.SignInAsync();
        var orphan = await RunAsync(Plugin(f), 999999);
        Assert.Equal(LuaToolsGui.Resources.Strings.Add_Err_BaseGame, orphan.Error);
        Assert.Empty(orphan.Sources);
        Assert.DoesNotContain("/api/dlc/info", f.Transport.Paths);
    }

    [Fact]
    public async Task PluginDlcInfoFailureSurfacesTheApiError()
    {
        using var f = new Fixture(() => Respond("{\"error\":\"Unauthorized\"}"));
        f.Details(1670540, "dlc", "1142710");
        await f.SignInAsync();
        var state = await RunAsync(Plugin(f), 1670540);
        Assert.Equal(LuaToolsGui.Resources.Strings.Api_Err_InvalidDlcResponse, state.Error);
        Assert.False(state.Checking);
    }
}
