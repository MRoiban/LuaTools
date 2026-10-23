using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LuaToolsGui.Services;
using LuaToolsGui.Services.OnlineFixMe;
using Xunit;

namespace LuaToolsGui.Tests;

public class OnlineFixMeClientTests
{
    private const string ArchiveName = "Raft_Fix_Repair_Steam.rar";
    private static Uri Folder => OnlineFixMeListing.FolderFromName("Raft")!;

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task MissingCandidateFallsBackToNextTitle(HttpStatusCode missing)
    {
        var handler = new Stub((_, index) => index == 0 ? new(missing) : Listing());
        using var http = Client(handler);
        var result = await new OnlineFixMeClient(http).LookupAsync("Hollow Knight: Silksong", null, false, default);
        Assert.Equal(OnlineFixMeLookupStatus.Found, result.Status);
        Assert.Equal("Hollow Knight Silksong", result.FolderName);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/Hollow%20Knight%3A%20Silksong/Fix%20Repair/", handler.Requests[0].Url.AbsoluteUri);
        Assert.EndsWith("/Hollow%20Knight%20Silksong/Fix%20Repair/", handler.Requests[1].Url.AbsoluteUri);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(AppConfig.OnlineFixMeUserAgent, r.UserAgent);
            Assert.Null(r.Cookie);
            Assert.Null(r.Referer);
            Assert.Equal(1024 * 1024, r.Budget.MaxBytes);
            Assert.Equal(TimeSpan.FromSeconds(15), r.Budget.BodyTimeout);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task OtherStatusesStopCandidateFallback(HttpStatusCode status)
    {
        var handler = new Stub((_, _) => new(status));
        using var http = Client(handler);
        var result = await new OnlineFixMeClient(http).LookupAsync("Sid Meier's: Game", null, false, default);
        Assert.Equal(OnlineFixMeLookupStatus.Unavailable, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TransportFailureStopsCandidateFallback()
    {
        var handler = new Stub((_, _) => throw new HttpRequestException("Transport unavailable."));
        using var http = Client(handler);
        var result = await new OnlineFixMeClient(http).LookupAsync("Hollow Knight: Silksong", null, false, default);
        Assert.Equal(OnlineFixMeLookupStatus.Unavailable, result.Status);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("cloudflare-challenge.html")]
    [InlineData("uploads-root-401.html")]
    public async Task SuccessWithoutIndexTitleIsUnavailable(string fixture)
    {
        var handler = new Stub((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(OnlineFixMeListingTests.Fixture(fixture)) });
        using var http = Client(handler);
        var result = await new OnlineFixMeClient(http).LookupAsync("Hollow Knight: Silksong", null, false, default);
        Assert.Equal(OnlineFixMeLookupStatus.Unavailable, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EmptyRepairListingIsNotFoundAndMayTryNextCandidate()
    {
        var handler = new Stub((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("<title>Index of /</title><a href=\"FullGame.rar\">game</a>") });
        using var http = Client(handler);
        var result = await new OnlineFixMeClient(http).LookupAsync("Hollow Knight: Silksong", null, false, default);
        Assert.Equal(OnlineFixMeLookupStatus.NotFound, result.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ManualOverrideWinsAndInvalidOverrideMakesNoRequests()
    {
        var handler = new Stub((_, _) => Listing());
        using var http = Client(handler);
        var client = new OnlineFixMeClient(http);
        var found = await client.LookupAsync("Other Game: Edition", Folder.AbsoluteUri + "Fix%20Repair/" + ArchiveName, false, default);
        Assert.Equal(OnlineFixMeLookupStatus.Found, found.Status);
        Assert.Equal(Folder, found.FolderUrl);
        Assert.Equal("Raft", found.FolderName);
        Assert.Single(handler.Requests);
        var invalid = await client.LookupAsync("Raft", "https://evil.example/uploads/Raft/", false, default);
        Assert.Equal(OnlineFixMeLookupStatus.InvalidFolder, invalid.Status);
        Assert.Single(handler.Requests);
        Assert.Equal(OnlineFixMeLookupStatus.InvalidFolder, (await client.LookupAsync("..", null, false, default)).Status);
    }

    [Fact]
    public async Task LookupCachesEachFolderForFiveMinutesAndFreshBypassesCache()
    {
        DateTimeOffset now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var handler = new Stub((_, _) => Listing());
        using var http = Client(handler);
        var client = new OnlineFixMeClient(http, () => now);
        var first = await client.LookupAsync("Raft", null, false, default);
        now += TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1);
        Assert.Same(first, await client.LookupAsync("Raft", null, false, default));
        Assert.Single(handler.Requests);
        now += TimeSpan.FromMilliseconds(1);
        await client.LookupAsync("Raft", null, false, default);
        Assert.Equal(2, handler.Requests.Count);
        await client.LookupAsync("Raft", null, true, default);
        Assert.Equal(3, handler.Requests.Count);
        await client.LookupAsync("Palworld", null, false, default);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task CandidateMissesAreCachedPerFolder()
    {
        var handler = new Stub((_, index) => index == 0 ? new(HttpStatusCode.Unauthorized) : Listing());
        using var http = Client(handler);
        var client = new OnlineFixMeClient(http);
        await client.LookupAsync("Hollow Knight: Silksong", null, false, default);
        await client.LookupAsync("Hollow Knight: Silksong", null, false, default);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ListingBodyBudgetIsAppliedBeforeBuffering()
    {
        var handler = new Stub((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1024 * 1024 + 1)) });
        using var http = Client(handler);
        var result = await new OnlineFixMeClient(http).LookupAsync("Raft", null, false, default);
        Assert.Equal(OnlineFixMeLookupStatus.Unavailable, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OnlyFreshListingCookiesReachArchiveWithRefererAndUserAgent()
    {
        var handler = new Stub((_, index) => index switch
        {
            0 => Listing("old_session=not-reused; Path=/"),
            1 => Listing("invalid.name=value", "quoted=\"value\"", "long=" + new string('x', 4097),
                "online_fix_auth=fresh%2Ftoken; Path=/; Secure; HttpOnly", "extra=a+b/c==; Path=/", "third=value", "fourth=value", "fifth=omitted"),
            2 => Archive(),
            3 => Listing("online_fix_auth=second-session; Secure"),
            _ => Archive()
        });
        using var http = Client(handler);
        var client = new OnlineFixMeClient(http);
        await client.LookupAsync("Raft", null, false, default);
        var file = await client.DownloadArchiveAsync(Folder, ArchiveName, null, default);
        try
        {
            Assert.Equal(ArchiveName, file.FileName);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(file.FilePath));
        }
        finally { Remove(file); }
        var second = await client.DownloadArchiveAsync(Folder, ArchiveName, null, default);
        Remove(second);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Null(handler.Requests[0].Cookie);
        Assert.Null(handler.Requests[1].Cookie);
        Assert.Null(handler.Requests[3].Cookie);
        Assert.Equal("online_fix_auth=fresh%2Ftoken; extra=a+b/c==; third=value; fourth=value", handler.Requests[2].Cookie);
        Assert.Equal("online_fix_auth=second-session", handler.Requests[4].Cookie);
        foreach (var request in handler.Requests.Where(r => r.Cookie is not null))
        {
            Assert.Equal(new Uri(Folder, "Fix%20Repair/"), request.Referer);
            Assert.Equal(HttpCredentialPurpose.OnlineFixMe, request.Purpose);
            Assert.Equal(ManifestContentPolicy.MaxArchiveBytes, request.Budget.MaxBytes);
            Assert.Equal(TimeSpan.FromMinutes(5), request.Budget.BodyTimeout);
        }
        Assert.All(handler.Requests, r => Assert.Equal(AppConfig.OnlineFixMeUserAgent, r.UserAgent));
    }

    [Fact]
    public async Task ServerContentDispositionCannotChangeValidatedArchiveName()
    {
        var handler = new Stub((_, index) =>
        {
            if (index == 0) return Listing("online_fix_auth=session");
            var response = Archive();
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "../escape.rar" };
            return response;
        });
        using var http = Client(handler);
        var file = await new OnlineFixMeClient(http).DownloadArchiveAsync(Folder, ArchiveName, null, default);
        try { Assert.Equal(ArchiveName, file.FileName); }
        finally { Remove(file); }
    }

    [Theory]
    [InlineData("Other_Fix_Repair.rar")]
    [InlineData("raft_fix_repair_steam.rar")]
    public async Task ArchiveMustHaveExactNameInFreshListingEvenAfterCachedHit(string name)
    {
        var handler = new Stub((_, _) => Listing("online_fix_auth=session"));
        using var http = Client(handler);
        var client = new OnlineFixMeClient(http);
        await client.LookupAsync("Raft", null, false, default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadArchiveAsync(Folder, name, null, default));
        Assert.Contains("fresh folder listing", error.Message);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Null(r.Cookie));
    }

    [Fact]
    public async Task RemovedArchiveCannotBeDownloadedFromCachedLookup()
    {
        var handler = new Stub((_, index) => index == 0 ? Listing("online_fix_auth=session") : new(HttpStatusCode.NotFound));
        using var http = Client(handler);
        var client = new OnlineFixMeClient(http);
        await client.LookupAsync("Raft", null, false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadArchiveAsync(Folder, ArchiveName, null, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("FullGame.rar")]
    [InlineData("Game_fix_repair.part1.rar")]
    [InlineData("../Game_fix_repair.rar")]
    [InlineData("Game:fix_repair.rar")]
    [InlineData("Game*fix_repair.rar")]
    public async Task UnsafeOrIneligibleArchiveIsRejectedBeforeNetwork(string name)
    {
        var handler = new Stub((_, _) => Listing());
        using var http = Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OnlineFixMeClient(http).DownloadArchiveAsync(Folder, name, null, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingValidCookiePreventsArchiveRequest()
    {
        var handler = new Stub((_, _) => Listing("invalid.name=value", "empty="));
        using var http = Client(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new OnlineFixMeClient(http).DownloadArchiveAsync(Folder, ArchiveName, null, default));
        Assert.Contains("anonymous download session", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedArchiveLengthIsRejectedBeforeReadingBody()
    {
        var content = new UnreadContent();
        content.Headers.ContentLength = ManifestContentPolicy.MaxArchiveBytes + 1;
        var handler = new Stub((_, index) => index == 0 ? Listing("online_fix_auth=session") : new(HttpStatusCode.OK) { Content = content });
        using var http = Client(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new OnlineFixMeClient(http).DownloadArchiveAsync(Folder, ArchiveName, null, default));
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(content.WasRead);
    }

    [Fact]
    public async Task CallerCancellationIsNotMappedToUnavailable()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Stub((_, _) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
        using var http = Client(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OnlineFixMeClient(http).LookupAsync("Raft", null, false, cancellation.Token));
        Assert.Single(handler.Requests);
    }

    private static HttpClient Client(Stub handler) => AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);

    private static HttpResponseMessage Listing(params string[] cookies)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OnlineFixMeListingTests.Fixture("raft-fix-repair.html")) };
        if (cookies.Length > 0) response.Headers.TryAddWithoutValidation("Set-Cookie", cookies);
        return response;
    }

    private static HttpResponseMessage Archive() => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

    private static void Remove(DownloadedFile file) => Directory.Delete(Path.GetDirectoryName(file.FilePath)!, recursive: true);

    private sealed record Sent(Uri Url, string? Cookie, Uri? Referer, string UserAgent, HttpCredentialPurpose? Purpose, HttpResponseBudget Budget);

    private sealed class Stub(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var sent = new Sent(request.RequestUri!, request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null,
                request.Headers.Referrer, request.Headers.UserAgent.ToString(),
                request.Options.TryGetValue(HttpCredentialPurposeExtensions.RequestOption, out var purpose) ? purpose : null,
                request.Options.TryGetValue(HttpResponseBudget.RequestOption, out var budget) ? budget : HttpResponseBudget.Json);
            int index = Requests.Count;
            Requests.Add(sent);
            return Task.FromResult(respond(request, index));
        }
    }

    private sealed class UnreadContent : HttpContent
    {
        public bool WasRead { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("Oversized content must not be read.");
        }
    }
}
