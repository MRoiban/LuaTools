using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Startup session restore with an expired access token: only a rejected refresh token signs out.</summary>
public class AuthRestoreTests
{
    // expires_in 60 is inside the 2-minute refresh margin, so the next restore must refresh.
    private const string ShortSession = "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":60,\"user\":{\"id\":\"db101b52-9f32-4663-8896-bdc8fd58b651\"}}";
    private const string FreshSession = "{\"access_token\":\"fresh-access\",\"refresh_token\":\"fresh-refresh\",\"expires_in\":3600,\"user\":{\"id\":\"db101b52-9f32-4663-8896-bdc8fd58b651\"}}";

    private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response(request));
    }

    /// <summary>Signs in through the bot-code flow so auth.dat holds a session that needs a refresh.</summary>
    private static async Task SaveExpiringSessionAsync(string authFile)
    {
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/auth/code/redeem" => Json(HttpStatusCode.OK, "{\"token\":\"test-magic-hash\"}"),
            "/auth/v1/verify" => Json(HttpStatusCode.OK, ShortSession),
            _ => throw new Exception("Unexpected contract"),
        }));
        await new AuthService(http, authFile).SignInWithCodeAsync("abc123");
        Assert.True(File.Exists(authFile));
    }

    private static async Task<(AuthService Auth, bool Restored)> RestoreAsync(string authFile, Func<HttpRequestMessage, HttpResponseMessage> refresh)
    {
        var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(r =>
        {
            Assert.Equal("/auth/v1/token", r.RequestUri!.AbsolutePath);
            return refresh(r);
        }));
        var auth = new AuthService(http, authFile);
        return (auth, await auth.InitializeAsync());
    }

    [Fact]
    public async Task OfflineRestoreKeepsTheSessionAndRefreshesOnFirstUse()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        try
        {
            await SaveExpiringSessionAsync(file);
            var online = false;
            var (auth, restored) = await RestoreAsync(file, _ => online
                ? Json(HttpStatusCode.OK, FreshSession)
                : throw new HttpRequestException("No such host is known."));

            Assert.True(restored);
            Assert.True(auth.IsSignedIn);
            Assert.True(File.Exists(file));

            online = true;
            Assert.Equal("fresh-access", await auth.GetValidAccessTokenAsync());
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ServerTroubleKeepsTheSession(HttpStatusCode status)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        try
        {
            await SaveExpiringSessionAsync(file);
            var (auth, restored) = await RestoreAsync(file, _ => Json(status, "{}"));

            Assert.True(restored);
            Assert.True(auth.IsSignedIn);
            Assert.True(File.Exists(file));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task RejectedRefreshTokenSignsOut(HttpStatusCode status)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        try
        {
            await SaveExpiringSessionAsync(file);
            var (auth, restored) = await RestoreAsync(file,
                _ => Json(status, "{\"error\":\"invalid_grant\",\"error_description\":\"Invalid Refresh Token\"}"));

            Assert.False(restored);
            Assert.True(auth.IsGuest);
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); }
    }
}
