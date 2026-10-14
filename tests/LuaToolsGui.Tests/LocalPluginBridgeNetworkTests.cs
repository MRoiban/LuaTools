using System.Net;
using System.Net.Http;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class LocalPluginBridgeNetworkTests
{
    [Fact]
    public async Task BridgeTokenCanReachOnlyOriginalLocalEndpoint()
    {
        var transport = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new OutboundPolicyHandler(new OutboundNetworkPolicy([6767, 8080]), transport));
        using var request = BridgeRequest("http://127.0.0.1:6767/has/620");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.Count);
    }

    [Theory]
    [InlineData("https://lua.tools/api/denuvo/listings", false)]
    [InlineData("http://127.0.0.1:8080/json", true)]
    [InlineData("http://localhost:6767/has/620", true)]
    [InlineData("http://user@127.0.0.1:6767/has/620", true)]
    public async Task BridgeTokenCannotReachRemoteOrDebuggerBeforeTransport(string url, bool localClient)
    {
        var transport = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var policy = localClient ? new OutboundNetworkPolicy([6767, 8080]) : new OutboundNetworkPolicy();
        using var client = new HttpClient(new OutboundPolicyHandler(policy, transport));
        using var request = BridgeRequest(url);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.Equal(0, transport.Count);
    }

    [Theory]
    [InlineData("http://127.0.0.1:6767/other")]
    [InlineData("http://127.0.0.1:8080/json")]
    [InlineData("https://lua.tools/")]
    public async Task AnyBridgeRedirectFailsWithoutSecondRequest(string target)
    {
        var transport = new Stub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(target);
            return response;
        });
        using var client = new HttpClient(new OutboundPolicyHandler(new OutboundNetworkPolicy([6767, 8080]), transport));
        using var request = BridgeRequest("http://127.0.0.1:6767/has/620");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.Equal(1, transport.Count);
    }

    private static HttpRequestMessage BridgeRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(LocalPluginBridgePolicy.TokenHeader, LocalPluginBridgePolicy.Token);
        return request;
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(respond(request));
        }
    }
}
