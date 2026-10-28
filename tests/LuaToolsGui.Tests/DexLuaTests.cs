using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

public class DexLuaTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("apikey"));
            Assert.Equal(AppConfig.DexLuaBaseUrl + "api/download?id=730", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(reply(request));
        }
    }

    private static HttpClient Client(Stub stub) => AppHttp.CreateExternalDataForTesting(
        TimeSpan.FromSeconds(2), new Uri(AppConfig.DexLuaBaseUrl), stub);

    [Theory]
    [InlineData(200, "available")]
    [InlineData(404, "unavailable")]
    [InlineData(429, "unknown")]
    [InlineData(503, "unknown")]
    public async Task AvailabilityValidatesScriptWithoutCredentials(int code, string expected)
    {
        using var stub = new Stub(r => {
            Assert.Equal(HttpMethod.Get, r.Method);
            return new((HttpStatusCode)code) { Content = new StringContent("addappid(730, 1, \"Test Game\")") };
        });
        using var client = Client(stub);
        Assert.Equal(expected, await new DexLuaService(client).CheckStatusAsync("730"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0730")]
    [InlineData("4294967296")]
    [InlineData("../730")]
    [InlineData("730?key=x")]
    public async Task InvalidIdsNeverReachTransport(string id)
    {
        using var stub = new Stub(_ => throw new Exception("Unexpected request"));
        using var client = Client(stub);
        var service = new DexLuaService(client);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CheckStatusAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DownloadManifestAsync(id, null));
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var client = Client(new Stub(_ => new(HttpStatusCode.OK)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DexLuaService(client).CheckStatusAsync("730", cancel.Token));
    }

    [Fact]
    public async Task RedirectDoesNotFetchAnotherDestination()
    {
        using var stub = new Stub(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://lua.tools/730.zip");
            return response;
        });
        using var client = Client(stub);
        Assert.Equal("unknown", await new DexLuaService(client).CheckStatusAsync("730"));
        await Assert.ThrowsAsync<HttpRequestException>(() => new DexLuaService(client).DownloadManifestAsync("730", null));
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task GuestApiRoutesToPublicLuaWithoutTouchingAuthOrStandardTransport()
    {
        using var stub = new Stub(r => {
            Assert.Equal(HttpMethod.Get, r.Method);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("addappid(730, 1, \"Test Game\")") };
            response.Content.Headers.ContentDisposition = new("attachment") { FileName = "untrusted.exe" };
            return response;
        });
        using var client = Client(stub);
        // Null auth is deliberate: direct downloads must not access the lua.tools session.
        var api = new LuaToolsApiClient(null!, null!, null!, client, dexLua: new DexLuaService(client));
        var file = await api.DownloadManifestAsync("730", "DexLua", null, null);
        try { Assert.Equal("730.lua", file.FileName); ManifestContentPolicy.ValidateLuaFile(file.FilePath, 730); }
        finally { File.Delete(file.FilePath); Directory.Delete(Path.GetDirectoryName(file.FilePath)!); }
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task HtmlIsRejectedAsAnArchive()
    {
        using var client = Client(new Stub(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>") }));
        await Assert.ThrowsAsync<ApiException>(() => new DexLuaService(client).DownloadManifestAsync("730", null));
    }

    [Theory]
    [InlineData("api/fix?id=730")]
    [InlineData("api/download?id=730&token=x")]
    [InlineData("api/download?id=0730")]
    public void OrdinaryPolicyRejectsOtherResources(string path)
    {
        var policy = new OutboundNetworkPolicy();
        policy.Validate(new Uri(AppConfig.DexLuaBaseUrl + "api/download?id=730"));
        Assert.Throws<HttpRequestException>(() => policy.Validate(new Uri(AppConfig.DexLuaBaseUrl + path)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NormalizationPreservesKeysWithoutInventingManifests(int mode)
    {
        string key = new('a', 64);
        var result = DexLuaService.Normalize(System.Text.Encoding.UTF8.GetBytes(
            $"addappid(730, 1, \"Test Game\")\naddappid(731, {mode}, \"{key}\")"), "730");
        string lua = System.Text.Encoding.UTF8.GetString(result);
        Assert.Contains(key, lua);
        Assert.Contains("addappid(730, 1)", lua);
        Assert.DoesNotContain("setManifestid", lua);
    }

    [Theory]
    [InlineData("addappid(731)")]
    [InlineData("addappid(730, 1, \"Title\"); os.execute(\"bad\")")]
    [InlineData("<html>error</html>")]
    [InlineData("addappid(730)\nos.execute(\"bad\")")]
    public void InvalidScriptsAreRejected(string lua) =>
        Assert.Throws<InvalidDataException>(() => DexLuaService.Normalize(System.Text.Encoding.UTF8.GetBytes(lua), "730"));
}
