using System.Net;
using System.Net.Http;
using System.Text.Json;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SourceAvailabilityTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(reply(request)); }
    }

    [Fact]
    public async Task OriginalLookupReturnsRyuuAndLuieWithoutCredentials()
    {
        using var transport = new Stub(r =>
        {
            Assert.Equal("http://167.235.229.108/check_apis?appid=620", r.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Null(r.Headers.Authorization);
            Assert.False(r.Headers.Contains("Cookie"));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"Luie\":\"available\",\"Ryuu\":\"available\"}") };
        });
        using var client = AppHttp.CreateSourceAvailability(transport);
        var result = await SourceAvailabilityClient.CheckAsync("620", default, client);
        Assert.Equal("available", result["Ryuu"]);
        Assert.Equal("available", result["Luie"]);
    }

    [Theory]
    [InlineData("http://167.235.229.108/donatekeys/send")]
    [InlineData("http://evil.example/check_apis?appid=620")]
    [InlineData("http://167.235.229.108:8080/check_apis?appid=620")]
    [InlineData("http://167.235.229.108/check_apis?appid=620&token=secret")]
    [InlineData("http://167.235.229.108/check_apis?appid=0")]
    [InlineData("http://167.235.229.108/check_apis?appid=620#fragment")]
    public async Task ExceptionCannotReachOtherDestinationsOrQueries(string url)
    {
        using var transport = new Stub(_ => throw new Exception("Must not reach transport"));
        using var client = AppHttp.CreateSourceAvailability(transport);
        using var request = Request(url);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.Equal(0, transport.Calls);
    }

    private static HttpRequestMessage Request(string url = "http://167.235.229.108/check_apis?appid=620")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", AppConfig.ManifestBackendUserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    [Theory]
    [InlineData("Authorization", "Bearer secret")]
    [InlineData("Cookie", "secret=value")]
    [InlineData("apikey", "secret")]
    [InlineData("Host", "evil.example")]
    public async Task CredentialsAndHostOverridesNeverLeaveClient(string header, string value)
    {
        using var transport = new Stub(_ => throw new Exception("Must not reach transport"));
        using var client = AppHttp.CreateSourceAvailability(transport);
        using var request = Request(); request.Headers.TryAddWithoutValidation(header, value);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task RedirectsAndOversizedResponsesFailWithoutFollowing()
    {
        using var transport = new Stub(_ => new(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://lua.tools/api/manifest/download") } });
        using var client = AppHttp.CreateSourceAvailability(transport);
        await Assert.ThrowsAsync<ApiException>(() => SourceAvailabilityClient.CheckAsync("620", default, client));
        Assert.Equal(1, transport.Calls);
        using var oversized = AppHttp.CreateSourceAvailability(new Stub(_ => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[65537]) }));
        await Assert.ThrowsAsync<ApiException>(() => SourceAvailabilityClient.CheckAsync("620", default, oversized));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"Ryuu\":true}")]
    [InlineData("{\"Ryuu\":\"https://evil.example\"}")]
    [InlineData("{\"<script>\":\"available\"}")]
    [InlineData("{\"Ryuu\":\"available\",\"Ryuu\":\"unavailable\"}")]
    public void MalformedCatalogueCannotPublishPartialOrExecutableValues(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => SourceAvailabilityClient.Parse(doc.RootElement));
    }

    [Fact]
    public async Task GeneralClientStillRejectsThePlaintextEndpoint()
    {
        using var client = AppHttp.CreateForTesting(TimeSpan.FromSeconds(1), new Stub(_ => throw new Exception()));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(SourceAvailabilityClient.Endpoint + "?appid=620"));
    }
}
