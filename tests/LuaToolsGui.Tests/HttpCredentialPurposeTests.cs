using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class HttpCredentialPurposeTests
{
    [Theory]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Raft_Fix_Repair_Steam.rar", "GET", true)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Hollow%20Knight/Fix%20Repair/Game_Fix_Repair.zip", "GET", true)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Raft_Fix_Repair_Steam.rar", "HEAD", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Raft_Fix_Repair_Steam.rar", "POST", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/", "GET", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/FullGame.rar", "GET", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Other/Fix%20Repair/Game_Fix_Repair.rar", "GET", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/nested/Game_Fix_Repair.rar", "GET", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft%2FGame/Fix%20Repair/Game_Fix_Repair.rar", "GET", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Game%2Ffix_repair.rar", "GET", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Game%5Cfix_repair.rar", "GET", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Game%00fix_repair.rar", "GET", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Game_Fix_Repair.rar?x=1", "GET", false)]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Game_Fix_Repair.rar#fragment", "GET", false)]
    [InlineData("https://uploads.online-fix.me:443/uploads/Raft/Fix%20Repair/Game_Fix_Repair.rar", "GET", false)]
    [InlineData("https://lua.tools/uploads/Raft/Fix%20Repair/Game_Fix_Repair.rar", "GET", false)]
    public async Task OnlineFixCookiePurposeIsLimitedToArchiveGets(string url, string method, bool allowed)
    {
        var handler = new Stub();
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(1), handler);
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.Add("Cookie", "online_fix_auth=anonymous-session");
        HttpCredentialPurpose.OnlineFixMe.Apply(request);
        if (allowed)
        {
            using var response = await http.SendAsync(request);
            Assert.Single(handler.Requests);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(request));
            Assert.Empty(handler.Requests);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OnlineFixPurposeNeverAllowsAuthorizationOrApiKey(bool bearer, bool apiKey)
    {
        var handler = new Stub();
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(1), handler);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Game_Fix_Repair.rar");
        request.Headers.Add("Cookie", "online_fix_auth=anonymous-session");
        if (bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "account-secret");
        if (apiKey) request.Headers.Add("apikey", "public-key");
        HttpCredentialPurpose.OnlineFixMe.Apply(request);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(request));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OnlineFixCookiesNeedExplicitPurposeAndCannotRedirectToListing()
    {
        var handler = new Stub { Redirect = true };
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(1), handler);
        const string url = "https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/Game_Fix_Repair.rar";
        using var untagged = new HttpRequestMessage(HttpMethod.Get, url);
        untagged.Headers.Add("Cookie", "online_fix_auth=anonymous-session");
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(untagged));
        Assert.Empty(handler.Requests);
        using var tagged = new HttpRequestMessage(HttpMethod.Get, url);
        tagged.Headers.Add("Cookie", "online_fix_auth=anonymous-session");
        HttpCredentialPurpose.OnlineFixMe.Apply(tagged);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(tagged));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://lua.tools/api/me/supporter-status", "GET", HttpCredentialPurpose.LuaToolsApi, true, false)]
    [InlineData("https://lua.tools/api/dlc/info?appid=1&base=2", "GET", HttpCredentialPurpose.LuaToolsApi, true, false)]
    [InlineData("https://lua.tools/api/manifest/download?appid=1&source=Ryuu", "GET", HttpCredentialPurpose.LuaToolsApi, true, false)]
    [InlineData("https://lua.tools/api/givemethemanifestpunk/1/2", "GET", HttpCredentialPurpose.LuaToolsApi, true, false)]
    [InlineData("https://lua.tools/api/dlc/generate?appid=1&base=2", "GET", HttpCredentialPurpose.LuaToolsApi, true, false)]
    [InlineData("https://lua.tools/api/denuvo/download?fix=x&slot=fix", "GET", HttpCredentialPurpose.LuaToolsApi, true, false)]
    [InlineData("https://db.lua.tools/auth/v1/token?grant_type=pkce", "POST", HttpCredentialPurpose.Supabase, false, true)]
    [InlineData("https://db.lua.tools/auth/v1/token?grant_type=refresh_token", "POST", HttpCredentialPurpose.Supabase, false, true)]
    [InlineData("https://db.lua.tools/auth/v1/verify", "POST", HttpCredentialPurpose.Supabase, false, true)]
    [InlineData("https://db.lua.tools/rest/v1/user_downloads?select=appid", "HEAD", HttpCredentialPurpose.Supabase, true, true)]
    [InlineData("https://hubcapmanifest.com/api/v1/status/1", "GET", HttpCredentialPurpose.Hubcap, true, false)]
    [InlineData("https://hubcapmanifest.com/api/v1/user/stats?api_key=opaque", "GET", HttpCredentialPurpose.Hubcap, false, false)]
    [InlineData("https://hubcapmanifest.com/api/v1/manifest/1?api_key=opaque", "GET", HttpCredentialPurpose.Hubcap, false, false)]
    public async Task OriginalAuthenticatedContractRoutesRemainAllowed(string url, string method,
        HttpCredentialPurpose purpose, bool bearer, bool apiKey)
    {
        var handler = new Stub();
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(1), handler);
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        purpose.Apply(req);
        if (bearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "account-secret");
        if (apiKey) req.Headers.Add("apikey", "public-key");
        using var response = await http.SendAsync(req);
        Assert.Single(handler.Requests);
        Assert.Equal(new Uri(url), handler.Requests[0]);
    }

    [Theory]
    [InlineData("https://lua.tools/api/manifest/download", "POST", HttpCredentialPurpose.LuaToolsApi)]
    [InlineData("https://db.lua.tools/auth/v1/token", "GET", HttpCredentialPurpose.Supabase)]
    [InlineData("https://hubcapmanifest.com/api/v1/status/1", "GET", HttpCredentialPurpose.LuaToolsApi)]
    [InlineData("https://lua.tools/unrelated", "GET", HttpCredentialPurpose.LuaToolsApi)]
    [InlineData("https://api.steampowered.com/ISteamApps/GetAppList/v2/", "GET", HttpCredentialPurpose.LuaToolsApi)]
    public async Task AccountCredentialsCannotEscapePurposeRouteOrMethod(string url, string method, HttpCredentialPurpose purpose)
    {
        var handler = new Stub();
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(1), handler);
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        purpose.Apply(req);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "account-secret");
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(req));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SameOriginRedirectCannotSendCredentialToUnrelatedRoute()
    {
        var handler = new Stub { Redirect = true };
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(1), handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://lua.tools/api/manifest/download");
        HttpCredentialPurpose.LuaToolsApi.Apply(req);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "account-secret");
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(req));
        Assert.Single(handler.Requests);
    }

    private sealed class Stub : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public bool Redirect { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add(req.RequestUri!);
            var response = new HttpResponseMessage(Redirect ? HttpStatusCode.Found : HttpStatusCode.OK);
            if (Redirect) response.Headers.Location = new Uri("/unrelated", UriKind.Relative);
            return Task.FromResult(response);
        }
    }
}
