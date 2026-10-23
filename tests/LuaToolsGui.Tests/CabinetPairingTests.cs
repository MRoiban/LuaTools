using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class CabinetPairingTests
{
    private const string Token = "cab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("ABCDEFGHJKMN", "ABCDEFGHJKMN")]
    [InlineData("abcd-efgh-jkmn", "ABCDEFGHJKMN")]
    [InlineData(" ABCD EFGH JKMN ", "ABCDEFGHJKMN")]
    [InlineData("OIlO-IlOI-lOIl", "011011011011")]
    [InlineData("0123-4567-89TV", "0123456789TV")]
    [InlineData("\tABCD-EFGH-JKMN\n", "ABCDEFGHJKMN")]
    public void CodesNormalizeLikeTheServer(string input, string expected)
    {
        Assert.True(CabinetConnectInput.TryNormalizeCode(input, out var code));
        Assert.Equal(expected, code);
        Assert.Equal(14, CabinetConnectInput.FormatCode(code).Length);
        Assert.Equal(code, CabinetConnectInput.FormatCode(code).Replace("-", ""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("----    ")]
    [InlineData("ABCDEFGHJKM")]
    [InlineData("ABCDEFGHJKMNP")]
    [InlineData("ABCDEFGHJKMU")]
    [InlineData("ABCD_EFGH_JKMN")]
    [InlineData("ABCD/EFGH/JKMN")]
    [InlineData("ABCÉEFGHJKMN")]
    public void InvalidCodesAreRejected(string? input) =>
        Assert.False(CabinetConnectInput.TryNormalizeCode(input, out _));

    [Theory]
    [InlineData("abcd-efgh-jkmn", 1)]
    [InlineData("https://cabinet.mc2.onl/connect#" + Token, 2)]
    [InlineData("cabinet://connect?server=https%3A%2F%2Fexample.com&token=" + Token, 2)]
    [InlineData("https://cabinet.mc2.onl/connect#not-a-token", 0)]
    [InlineData("https://cabinet.mc2.onl/", 0)]
    [InlineData("garbage", 0)]
    [InlineData("ABCDEFGHJKMU", 0)]
    public void InputClassificationKeepsBothKindsOfLinks(string input, int expected) =>
        Assert.Equal(expected, (int)CabinetConnectInput.Classify(input));

    [Theory]
    [InlineData(null, "https://cabinet.mc2.onl")]
    [InlineData("  ", "https://cabinet.mc2.onl")]
    [InlineData(" https://EXAMPLE.com:8443/ ", "https://example.com:8443")]
    [InlineData("https://example.com:443", "https://example.com")]
    public void ServersNormalizeToHttpsOrigins(string? input, string expected) =>
        Assert.Equal(expected, CabinetConnectInput.NormalizeServer(input));

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("http://localhost:8080")]
    [InlineData("cabinet.mc2.onl")]
    [InlineData("ftp://example.com")]
    public void NonHttpsServersGetTheSpecificError(string input) =>
        Assert.Equal(CabinetConnectFailure.NotHttps,
            Assert.Throws<CabinetConnectException>(() => CabinetConnectInput.NormalizeServer(input)).Failure);

    [Theory]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com/?token=secret")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("https://user:password@example.com")]
    public void ServersCannotContainPathsOrCredentials(string input) =>
        Assert.Throws<FormatException>(() => CabinetConnectInput.NormalizeServer(input));

    [Fact]
    public void DeviceNameIsTrimmedBoundedAndHasAFallback()
    {
        Assert.Equal("Windows PC", CabinetConnectInput.DeviceName("  "));
        Assert.Equal("Windows PC", CabinetConnectInput.DeviceName(null));
        Assert.Equal("My PC", CabinetConnectInput.DeviceName(" My PC "));
        Assert.Equal(new string('a', 100), CabinetConnectInput.DeviceName(" " + new string('a', 120) + " "));
    }

    [Fact]
    public async Task PairPostsTheExactContractAndReturnsOneTokenWithoutRetrying()
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.com:8443/api/pair", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("LuaTools/" + typeof(AppConfig).Assembly.GetName().Version!.ToString(3) + " (Windows)",
                request.Headers.UserAgent.ToString());
            Assert.Equal("https://example.com:8443", Assert.Single(request.Headers.GetValues("Origin")));
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
            Assert.Equal("ABCDEFGHJKMN", json.RootElement.GetProperty("code").GetString());
            Assert.Equal(CabinetConnectInput.DeviceName(Environment.MachineName), json.RootElement.GetProperty("deviceName").GetString());
            return Json(HttpStatusCode.OK, "{\"token\":\"" + Token + "\",\"user\":{\"id\":\"user-1\",\"name\":\"Alex\"}}");
        });
        using var pairing = new CabinetPairingClient(handler);
        Assert.Equal(Token, await pairing.RedeemAsync("https://example.com:8443/", "abcd-efgh-jkmn"));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, 0)]
    [InlineData(429, 1)]
    [InlineData(500, 4)]
    [InlineData(302, 4)]
    public async Task PairFailuresAreSpecificSanitizedAndNeverRetried(int status, int expected)
    {
        var handler = new Handler(_ => Task.FromResult(Json((HttpStatusCode)status,
            "{\"error\":\"invalid_code\",\"debug\":\"ABCDEFGHJKMN " + Token + "\"}")));
        using var pairing = new CabinetPairingClient(handler);
        var error = await Assert.ThrowsAsync<CabinetConnectException>(() =>
            pairing.RedeemAsync(AppConfig.CabinetDefaultServer, "ABCDEFGHJKMN"));
        Assert.Equal(expected, (int)error.Failure);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.DoesNotContain("ABCDEFGHJKMN", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TransportFailureDoesNotExposeCredentials()
    {
        var handler = new Handler(_ => throw new HttpRequestException("unsafe detail " + Token));
        using var pairing = new CabinetPairingClient(handler);
        var error = await Assert.ThrowsAsync<CabinetConnectException>(() =>
            pairing.RedeemAsync(AppConfig.CabinetDefaultServer, "ABCDEFGHJKMN"));
        Assert.Equal(CabinetConnectFailure.Unreachable, error.Failure);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"token\":\"bad\",\"user\":{\"id\":\"1\",\"name\":\"Alex\"}}")]
    [InlineData("{\"token\":\"" + Token + "\"}")]
    public async Task MalformedSuccessDoesNotBecomeAConnection(string body)
    {
        using var pairing = new CabinetPairingClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, body))));
        Assert.Equal(CabinetConnectFailure.Server, (await Assert.ThrowsAsync<CabinetConnectException>(() =>
            pairing.RedeemAsync(AppConfig.CabinetDefaultServer, "ABCDEFGHJKMN"))).Failure);
    }

    [Fact]
    public async Task InvalidInputNeverReachesTransport()
    {
        var handler = new Handler(_ => throw new Exception("Must not send"));
        using var pairing = new CabinetPairingClient(handler);
        await Assert.ThrowsAsync<CabinetConnectException>(() => pairing.RedeemAsync("http://example.com", "ABCDEFGHJKMN"));
        await Assert.ThrowsAsync<CabinetConnectException>(() => pairing.RedeemAsync(AppConfig.CabinetDefaultServer, "ABCDEFGHJKMU"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SettingsAwaitsConnectAndKeepsTheCodeOnFailure()
    {
        var cloud = new FakeCloud();
        var settings = new SettingsService(new AppSettings { CabinetEnabled = true });
        var vm = new SettingsViewModel(settings, new AuthService(), new SteamService(settings),
            new HubcapService(), new FakeCabinetService(), cloud)
        { CloudPairingCode = "abcd-efgh-jkmn", CloudServer = "https://example.com:8443" };
        var connect = vm.ConnectCloudCommand.ExecuteAsync(null);
        Assert.True(vm.CloudBusy);
        Assert.Equal("ABCD-EFGH-JKMN", cloud.Input);
        Assert.Equal("https://example.com:8443", cloud.Server);
        cloud.Completion.SetException(new CabinetConnectException(CabinetConnectFailure.TooManyAttempts));
        await connect;
        Assert.False(vm.CloudBusy);
        Assert.Equal(Resources.Strings.CabinetCloud_TooManyAttempts, vm.CloudError);
        Assert.Equal("ABCD-EFGH-JKMN", vm.CloudPairingCode);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return send(request); }
    }

    private sealed class FakeCloud : ICabinetCloud
    {
        public CabinetCloudStatus Status => CabinetCloudStatus.Disconnected;
        public event EventHandler<CabinetCloudStatus>? StatusChanged { add { } remove { } }
        public bool FriendLabels { get; set; }
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Input { get; private set; }
        internal string? Server { get; private set; }
        public Task ConnectAsync(string input, string? server = null, CancellationToken cancellationToken = default)
        { Input = input; Server = server; return Completion.Task; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SyncNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
