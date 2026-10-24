using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamMlTests
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
            Assert.Equal(AppConfig.SteamMlCdnUrl + "730.zip", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(reply(request));
        }
    }

    private static HttpClient Client(Stub stub) => AppHttp.CreateExternalDataForTesting(
        TimeSpan.FromSeconds(2), new Uri(AppConfig.SteamMlCdnUrl), stub);

    [Theory]
    [InlineData(200, "available")]
    [InlineData(404, "unavailable")]
    [InlineData(429, "unknown")]
    [InlineData(503, "unknown")]
    public async Task AvailabilityUsesHeadWithoutCredentials(int code, string expected)
    {
        using var stub = new Stub(r => {
            Assert.Equal(HttpMethod.Head, r.Method);
            return new((HttpStatusCode)code);
        });
        using var client = Client(stub);
        Assert.Equal(expected, await new SteamMlService(client).CheckStatusAsync("730"));
    }

    [Fact]
    public async Task StandardOutageDoesNotHideSteamMl()
    {
        using var client = Client(new Stub(_ => new(HttpStatusCode.OK)));
        var sources = await new SteamMlService(client).CheckSourcesAsync("730",
            (_, _) => throw new ApiException("offline"), default);
        Assert.Equal("available", sources[SteamMlService.SourceName]);
    }

    [Fact]
    public async Task DirectStatusOverridesUntrustedLegacyName()
    {
        using var client = Client(new Stub(_ => new(HttpStatusCode.NotFound)));
        var sources = await new SteamMlService(client).CheckSourcesAsync("730",
            (_, _) => Task.FromResult(new Dictionary<string, string> { ["SteamML"] = "available", ["Ryuu"] = "available" }), default);
        Assert.Equal("unavailable", sources["SteamML"]);
        Assert.Equal("available", sources["Ryuu"]);
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
        var service = new SteamMlService(client);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SteamMlService(client).CheckStatusAsync("730", cancel.Token));
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
        Assert.Equal("unknown", await new SteamMlService(client).CheckStatusAsync("730"));
        await Assert.ThrowsAsync<HttpRequestException>(() => new SteamMlService(client).DownloadManifestAsync("730", null));
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task GuestApiRoutesToPublicZipWithoutTouchingAuthOrStandardTransport()
    {
        using var stub = new Stub(r => {
            Assert.Equal(HttpMethod.Get, r.Method);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x50, 0x4b, 3, 4]) };
            response.Content.Headers.ContentDisposition = new("attachment") { FileName = "untrusted.exe" };
            return response;
        });
        using var client = Client(stub);
        // Null auth is deliberate: direct downloads must not access the lua.tools session.
        var api = new LuaToolsApiClient(null!, null!, null!, client, steamMl: new SteamMlService(client));
        var file = await api.DownloadManifestAsync("730", "SteamML", null, null);
        try { Assert.Equal("730.zip", file.FileName); Assert.True(ManifestJobFactory.IsZip(file.FilePath)); }
        finally { File.Delete(file.FilePath); Directory.Delete(Path.GetDirectoryName(file.FilePath)!); }
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task HtmlIsRejectedAsAnArchive()
    {
        using var client = Client(new Stub(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>") }));
        await Assert.ThrowsAsync<ApiException>(() => new SteamMlService(client).DownloadManifestAsync("730", null));
    }

    [Theory]
    [InlineData("SteamML.exe")]
    [InlineData("730.zip?api_key=secret")]
    [InlineData("other/730.zip")]
    public void OrdinaryPolicyDoesNotAllowOtherCdnResources(string path)
    {
        var policy = new OutboundNetworkPolicy();
        policy.Validate(new Uri(AppConfig.SteamMlCdnUrl + "730.zip"));
        Assert.Throws<HttpRequestException>(() => policy.Validate(new Uri(AppConfig.SteamMlCdnUrl + path)));
    }
}
