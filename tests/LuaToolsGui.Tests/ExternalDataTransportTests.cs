using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class ExternalDataTransportTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.1.1")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:20::1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("3fff::1")]
    public async Task RejectsNonpublicDnsAnswers(string address)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => ExternalDataTransport.ResolvePublicAddressesAsync("signed.example", default,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) })));
    }

    [Fact]
    public async Task MixedPublicPrivateDnsCannotSneakPrivateAddressIntoConnectList()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => ExternalDataTransport.ResolvePublicAddressesAsync("signed.example", default,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("::ffff:10.0.0.1") })));
    }

    [Fact]
    public async Task PublicDnsResolutionReturnsTheExactValidatedAddressListOnce()
    {
        int resolutions = 0;
        var answers = new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700:4700::1111") };
        var resolved = await ExternalDataTransport.ResolvePublicAddressesAsync("signed.example", default, (_, _) =>
        {
            resolutions++;
            return Task.FromResult(answers);
        });
        Assert.Same(answers, resolved);
        Assert.Equal(1, resolutions);
    }

    [Theory]
    [InlineData("http://assets.example/file")]
    [InlineData("https://127.0.0.1/file")]
    [InlineData("https://[::1]/file")]
    [InlineData("https://localhost/file")]
    [InlineData("https://assets.local/file")]
    [InlineData("https://assets.example:8443/file")]
    [InlineData("https://secret@assets.example/file")]
    [InlineData("https://assets.example/file#fragment")]
    public void ScopeRejectsUnsafeInitialDestination(string url)
    {
        Assert.Throws<HttpRequestException>(() => AppHttp.CreateExternalDataClient(TimeSpan.FromSeconds(1), new Uri(url)));
    }

    [Fact]
    public async Task ArbitraryPublicDnsOriginSignedQuerySupportsInertDownload()
    {
        var handler = new Stub(_ => new(HttpStatusCode.OK) { Content = new StringContent("inert data") });
        var destination = new Uri("https://source-defined.example/file?X-Amz-Signature=opaque");
        using var http = AppHttp.CreateExternalDataForTesting(TimeSpan.FromSeconds(1), destination, handler);
        Assert.Equal("inert data", await http.GetStringAsync(destination));
        Assert.Single(handler.Requests);
        Assert.Equal(destination, handler.Requests[0]);
    }

    [Theory]
    [InlineData("https://source-defined.example/other")]
    [InlineData("https://lua.tools/other")]
    [InlineData("http://source-defined.example/other")]
    public async Task ExternalRedirectAlwaysRejectedBeforeSecondRequest(string redirect)
    {
        var handler = new Stub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(redirect);
            return response;
        });
        var destination = new Uri("https://source-defined.example/file?sig=opaque");
        using var http = AppHttp.CreateExternalDataForTesting(TimeSpan.FromSeconds(1), destination, handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(destination));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ScopedClientCannotReachDifferentHttpsOrigin()
    {
        var handler = new Stub(_ => new(HttpStatusCode.OK));
        using var http = AppHttp.CreateExternalDataForTesting(TimeSpan.FromSeconds(1), new("https://source-defined.example/file"), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://lua.tools/file"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExternalDataCannotAttachExistingCredentials()
    {
        var handler = new Stub(_ => new(HttpStatusCode.OK));
        var destination = new Uri("https://source-defined.example/file");
        using var http = AppHttp.CreateExternalDataForTesting(TimeSpan.FromSeconds(1), destination, handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, destination);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "account-secret");
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(req));
        Assert.Empty(handler.Requests);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add(req.RequestUri!);
            return Task.FromResult(respond(req));
        }
    }
}
