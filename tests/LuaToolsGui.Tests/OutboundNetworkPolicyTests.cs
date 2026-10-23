using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class OutboundNetworkPolicyTests
{
    [Theory]
    [InlineData("https://uploads.online-fix.me:2053/uploads/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Raft_Fix_Repair_Steam.rar")]
    public async Task OnlineFixPortIsAllowedForItsUploadsPath(string url)
    {
        var handler = new Stub(_ => new(HttpStatusCode.OK));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        using var response = await http.GetAsync(url);
        Assert.Equal(new Uri(url), Assert.Single(handler.Uris));
    }

    [Theory]
    [InlineData("https://lua.tools:2053/uploads/Raft/")]
    [InlineData("https://evil.example:2053/uploads/Raft/")]
    [InlineData("https://uploads.online-fix.me.evil.example:2053/uploads/Raft/")]
    [InlineData("https://uploads.online-fix.me:2053/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads")]
    [InlineData("https://uploads.online-fix.me:2053/Uploads/Raft/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads-other/Raft/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/?x=1")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/#fragment")]
    [InlineData("https://user@uploads.online-fix.me:2053/uploads/Raft/")]
    [InlineData("https://uploads.online-fix.me:443/uploads/Raft/")]
    [InlineData("https://uploads.online-fix.me:8443/uploads/Raft/")]
    [InlineData("http://uploads.online-fix.me:2053/uploads/Raft/")]
    public async Task OnlineFixPortDoesNotBroadenOtherOriginsPathsOrClients(string url)
    {
        var handler = new Stub(_ => new(HttpStatusCode.OK));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url));
        Assert.Empty(handler.Uris);
        Assert.Throws<HttpRequestException>(() => new OutboundNetworkPolicy(dns: true).Validate(new Uri(url)));
    }

    [Fact]
    public void OnlineFixIsNotAllowedOnDnsOrLoopbackOnlyClients()
    {
        var uri = new Uri("https://uploads.online-fix.me:2053/uploads/Raft/");
        Assert.Throws<HttpRequestException>(() => new OutboundNetworkPolicy(dns: true).Validate(uri));
        Assert.Throws<HttpRequestException>(() => new OutboundNetworkPolicy([8080]).Validate(uri));
    }

    [Theory]
    [InlineData("http://167.235.229.108/check_apis")]
    [InlineData("http://lua.tools/")]
    [InlineData("https://lua.tools.evil.example/")]
    [InlineData("https://evil.lua.tools/")]
    [InlineData("https://lua.tools:8443/")]
    [InlineData("https://user:secret@lua.tools/")]
    [InlineData("http://127.0.0.1:8080/json")]
    [InlineData("https://ghproxy.net/https://evil.example/example")]
    public async Task RejectedInitialDestinationNeverReachesTransport(string url)
    {
        var handler = new Stub(_ => new(HttpStatusCode.OK));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url));
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task RedirectCannotEscapeAllowlist()
    {
        var handler = new Stub(_ => Redirect("https://evil.example/token"));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://lua.tools/file"));
        Assert.Single(handler.Uris);
    }

    [Fact]
    public async Task CrossOriginRedirectRejectsCredentials()
    {
        var handler = new Stub(r => r.RequestUri!.Host == "lua.tools"
            ? Redirect("https://db.lua.tools/file") : new(HttpStatusCode.OK));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://lua.tools/api/manifest/download");
        HttpCredentialPurpose.LuaToolsApi.Apply(req);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        req.Headers.Add("X-Token", "secret");
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(req));
        Assert.Single(handler.Uris);
    }

    [Fact]
    public async Task PublicRedirectStripsCustomHeaders()
    {
        var handler = new Stub(r => r.RequestUri!.Host == "github.com"
            ? Redirect("https://release-assets.githubusercontent.com/file") : new(HttpStatusCode.OK));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://github.com/file");
        req.Headers.Add("X-Token", "secret");
        using var response = await http.SendAsync(req);
        Assert.Equal(2, handler.Uris.Count);
        Assert.Empty(handler.LastHeaders);
    }

    [Fact]
    public async Task AuthPostRedirectIsRejectedWithoutReplayingBody()
    {
        var handler = new Stub(_ => Redirect("https://db.lua.tools/token"));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.PostAsync("https://db.lua.tools/auth/v1/token", new StringContent("secret")));
        Assert.Single(handler.Uris);
    }

    [Fact]
    public async Task CredentialQueryCannotRedirectAcrossOrigins()
    {
        var handler = new Stub(_ => Redirect("https://lua.tools/file"));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://hubcapmanifest.com/api/v1/user/stats?api_key=secret");
        HttpCredentialPurpose.Hubcap.Apply(req);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(req));
        Assert.Single(handler.Uris);
    }

    [Fact]
    public async Task AuthHeadersCannotTargetPublicMetadataHost()
    {
        var handler = new Stub(_ => new(HttpStatusCode.OK));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.steamcmd.net/v1/info/1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(req));
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task RedirectLoopIsBounded()
    {
        var handler = new Stub(_ => Redirect("/again"));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://lua.tools/file"));
        Assert.Equal(6, handler.Uris.Count);
    }

    [Fact]
    public void LoopbackPermissionIsExactAndCannotReachRemoteHosts()
    {
        var policy = new OutboundNetworkPolicy([8080]);
        policy.Validate(new Uri("http://127.0.0.1:8080/json"));
        Assert.Throws<HttpRequestException>(() => policy.Validate(new Uri("http://localhost:8080/json")));
        Assert.Throws<HttpRequestException>(() => policy.Validate(new Uri("http://127.0.0.1:6767/json")));
        Assert.Throws<HttpRequestException>(() => policy.Validate(new Uri("https://lua.tools/")));
    }

    [Theory]
    [InlineData("https://ghproxy.net/https://github.com/example/repo/releases/download/v1/file.zip")]
    [InlineData("https://ghfast.top/https://raw.githubusercontent.com/example/repo/main/info.json")]
    [InlineData("https://gh.ddlc.top/https://objects.githubusercontent.com/file")]
    [InlineData("https://lua.tools/api/gh/https://api.github.com/repos/example/repo/releases/latest")]
    [InlineData("https://lua.tools/api/gh/https://api.github.com/repos/example/repo/releases?per_page=100&page=1")]
    public void SourceDefinedProxyRouteAllowed(string url) => new OutboundNetworkPolicy().Validate(new Uri(url));

    [Theory]
    [InlineData("https://ghproxy.net/unrelated")]
    [InlineData("https://ghproxy.net/http://github.com/file")]
    [InlineData("https://ghproxy.net/https://github.com.evil.example/file")]
    [InlineData("https://ghproxy.net/https://github.com/file?signature=opaque")]
    [InlineData("https://lua.tools/api/gh/https://github.com/file")]
    [InlineData("https://ghfast.top/https://api.github.com/repos/example/repo")]
    [InlineData("https://lua.tools/api/gh/https://api.github.com/repos/example/repo/releases?token=opaque")]
    public void ProxyCannotBecomeGeneralOrSignedUrlDestination(string url) =>
        Assert.Throws<HttpRequestException>(() => new OutboundNetworkPolicy().Validate(new Uri(url)));

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        public List<string> LastHeaders { get; private set; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            LastHeaders = request.Headers.Select(h => h.Key).ToList();
            return Task.FromResult(respond(request));
        }
    }
}
