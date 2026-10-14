using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class ApiContractTests
{
    private const string Session = "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":3600,\"user\":{\"id\":\"db101b52-9f32-4663-8896-bdc8fd58b651\"}}";
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response(request));
    }
    private static AuthService Auth(HttpClient http) => new(http, Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".dat"));
    private static LuaToolsApiClient Api(HttpClient http, AuthService? auth = null,
        Func<Uri,HttpClient>? external = null) => new(auth ?? Auth(http), null!, null!, http, external);

    [Fact]
    public async Task PublicSearchFeaturedAndCataloguePreserveContracts()
    {
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(r =>
        {
            Assert.Null(r.Headers.Authorization);
            return r.RequestUri!.AbsolutePath switch
            {
                "/api/storesearch/" => Json("{\"items\":[{\"id\":10,\"name\":\"Valid\"},{\"id\":-1,\"name\":\"Invalid\"}]}"),
                "/api/featuredcategories" => Json("{\"top_sellers\":{\"items\":[{\"id\":10,\"name\":\"Valid\",\"type\":0,\"large_capsule_image\":\"https://cdn.cloudflare.steamstatic.com/test.jpg\"}]}}"),
                "/api/denuvo/listings" => Json("{\"games\":[{\"appid\":\"10\",\"name\":\"Valid\",\"fixCount\":1}],\"tags\":[]}"),
                _ => throw new Exception("Unexpected contract")
            };
        }));
        var api = Api(http);
        Assert.Equal(10, Assert.Single(await api.SearchAsync("a & b")).AppId);
        Assert.Single((await api.GetFeaturedAsync()).TopSellers);
        Assert.Single((await api.GetDenuvoListingsAsync())!.Games);
    }

    [Fact]
    public async Task BotAuthUsageManifestAndSignedDownloadKeepCredentialsScoped()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".dat");
        var calls = 0;
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(r =>
        {
            calls++;
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/auth/code/redeem")
            {
                Assert.Equal(HttpMethod.Post, r.Method);
                Assert.Null(r.Headers.Authorization);
                return Json("{\"token\":\"test-magic-hash\"}");
            }
            if (path == "/auth/v1/verify")
            {
                Assert.Equal("db.lua.tools", r.RequestUri.Host);
                Assert.True(r.Headers.Contains("apikey"));
                return Json(Session);
            }
            Assert.Equal("test-access", r.Headers.Authorization!.Parameter);
            if (path == "/rest/v1/user_downloads")
            {
                Assert.Equal(HttpMethod.Head, r.Method);
                Assert.Equal("count=exact", Assert.Single(r.Headers.GetValues("Prefer")));
                var result = Json(""); result.Content.Headers.TryAddWithoutValidation("Content-Range", "0-2/3"); return result;
            }
            if (path == "/api/givemethemanifestpunk/10/123")
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1,2,3]) };
            if (path == "/api/denuvo/download")
            {
                Assert.Contains("fix=online-fix%3A5e01e852-6f21-4d26-b6ae-893116d72d16", r.RequestUri.Query);
                return Json("{\"url\":\"https://signed.example/file?X-Amz-Signature=test\"}");
            }
            throw new Exception("Unexpected contract");
        }));
        try
        {
            http.BaseAddress = new Uri(AppConfig.ApiBaseUrl);
            var auth = new AuthService(http, temp);
            await auth.SignInWithCodeAsync("abc123");
            Assert.True(auth.IsSignedIn);
            var api = Api(http, auth, uri => AppHttp.CreateExternalDataForTesting(TimeSpan.FromSeconds(5), uri, new Stub(r =>
            {
                Assert.Equal(uri, r.RequestUri);
                Assert.Null(r.Headers.Authorization);
                Assert.False(r.Headers.Contains("apikey"));
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1,2,3]) };
            })));
            Assert.Equal(3, (await api.GetStandardUsageAsync())!.Used);
            var manifest = await api.DownloadDepotManifestAsync(10, "123", null);
            File.Delete(manifest.FilePath);
            var signed = await api.DownloadDenuvoAsync("online-fix:5e01e852-6f21-4d26-b6ae-893116d72d16", "manifest", "10.zip", null);
            File.Delete(signed.FilePath);
            Assert.Equal(5, calls);
        }
        finally { File.Delete(temp); }
    }

    [Fact]
    public async Task HubcapStatsStatusAndDownloadUseOriginalAuthentication()
    {
        var key = "smm_" + new string('a',96);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(r =>
        {
            Assert.Equal("hubcapmanifest.com", r.RequestUri!.Host);
            if (r.RequestUri.AbsolutePath == "/api/v1/status/10")
            {
                Assert.Equal(key, r.Headers.Authorization!.Parameter);
                Assert.Empty(r.RequestUri.Query);
                return Json("{\"status\":\"success\",\"manifest_file_exists\":true}");
            }
            Assert.Null(r.Headers.Authorization);
            Assert.Contains("api_key="+key, r.RequestUri.Query);
            return r.RequestUri.AbsolutePath == "/api/v1/user/stats"
                ? Json("{\"user_id\":\"u\",\"daily_usage\":3,\"daily_limit\":800,\"can_make_requests\":true}")
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent([1,2,3]) };
        }));
        var hubcap = new HubcapService(http);
        Assert.Equal(3, (await hubcap.GetStatsAsync(key))!.DailyUsage);
        Assert.True((await hubcap.CheckStatusAsync(key,"10"))!.ManifestFileExists);
        var downloaded = await hubcap.DownloadManifestAsync("10",key,null);
        File.Delete(downloaded.FilePath);
    }

    [Fact]
    public async Task SourceQualifiedFixIdentifierIsPreserved()
    {
        const string fixId = "online-fix:5e01e852-6f21-4d26-b6ae-893116d72d16";
        using var http = new HttpClient(new Stub(_ => Json("{\"appid\":\"10\",\"fixes\":[{\"id\":\"" + fixId + "\",\"title\":\"Valid source fix\"}]}")));
        var result = await Api(http).GetDenuvoFixesAsync("10");
        Assert.Equal(fixId, Assert.Single(result!.Fixes).Id);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("online-fix:../escape")]
    [InlineData("online-fix:folder/file")]
    [InlineData("online-fix:folder\\file")]
    [InlineData("online-fix:fix?slot=fix")]
    public async Task HostileFixIdentifiersNeverReachTransport(string fixId)
    {
        using var http = new HttpClient(new Stub(_ => throw new Exception("Transport was reached")));
        await Assert.ThrowsAsync<ArgumentException>(() => Api(http).DownloadDenuvoAsync(fixId, "fix", "fix.zip", null));
    }

    [Fact]
    public async Task MalformedAndMismatchedFixCataloguesAreRejected()
    {
        foreach (var body in new[] { "{}", "{\"appid\":\"20\",\"fixes\":[]}", "{\"appid\":\"10\",\"fixes\":[{\"id\":\"../escape\",\"title\":\"Bad\"}]}" })
        {
            using var http = new HttpClient(new Stub(_ => Json(body)));
            await Assert.ThrowsAsync<ApiException>(() => Api(http).GetDenuvoFixesAsync("10"));
        }
    }

    [Fact]
    public async Task AuthenticatedErrorsDoNotEchoRemoteTokens()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".dat");
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(r =>
            r.RequestUri!.AbsolutePath == "/api/auth/code/redeem" ? Json("{\"token\":\"test-token\"}")
            : r.RequestUri.AbsolutePath == "/auth/v1/verify" ? Json(Session)
            : new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"test-access test-refresh\"}") }));
        try
        {
            http.BaseAddress = new Uri(AppConfig.ApiBaseUrl);
            var auth = new AuthService(http,temp);
            await auth.SignInWithCodeAsync("abc123");
            var error = await Assert.ThrowsAsync<ApiException>(() => Api(http,auth).GetDlcInfoAsync("10","20"));
            Assert.DoesNotContain("test-access",error.Message);
            Assert.DoesNotContain("test-refresh",error.Message);
        }
        finally { File.Delete(temp); }
    }

    [Theory]
    [InlineData("../10")]
    [InlineData("+10")]
    [InlineData(" 10")]
    [InlineData("0")]
    [InlineData("4294967296")]
    public async Task InvalidIdentifiersNeverReachTransport(string id)
    {
        using var http = new HttpClient(new Stub(_ => throw new Exception("Transport was reached")));
        await Assert.ThrowsAsync<ArgumentException>(() => Api(http).GetDenuvoFixesAsync(id));
    }

    [Fact]
    public async Task InvalidSessionCannotSignIn()
    {
        using var http = new HttpClient(new Stub(r => Json(r.RequestUri!.AbsolutePath == "/api/auth/code/redeem"
            ? "{\"token\":\"test-token\"}" : "{}")));
        var auth = Auth(http);
        await Assert.ThrowsAsync<AuthException>(() => auth.SignInWithCodeAsync("abc123"));
        Assert.False(auth.IsSignedIn);
    }
}
