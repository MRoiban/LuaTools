using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class HttpCredentialPurposeTests
{
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
