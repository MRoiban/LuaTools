using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LuaToolsGui.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class CatalogHttpContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LuaToolsCatalogTests", Guid.NewGuid().ToString("N"));
    public CatalogHttpContractTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private string PathFor(string name) => Path.Combine(_directory, name);
    private sealed class Transport(params string[] bodies) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bodies[Math.Min(Requests.Count - 1, bodies.Length - 1)]) });
        }
    }
    [Fact]
    public async Task MorrenusFlatListLoadsAndPersistsWithoutFallback()
    {
        var transport = new Transport("""[{"appid":10,"name":"Counter-Strike"}]""");
        var cache = new SteamAppListCache(transport, PathFor("apps.json"));
        await cache.EnsureLoadedAsync();
        Assert.Equal("Counter-Strike", cache.GetName(10));
        Assert.Equal("applist.morrenus.xyz", Assert.Single(transport.Requests).Host);
        Assert.True(File.Exists(PathFor("apps.json")));
    }
    [Theory]
    [InlineData("[{\"appid\":10,\"name\":\"Poison\"},{\"appid\":-1,\"name\":\"Bad\"}]")]
    [InlineData("[{\"appid\":10,\"name\":\"Poison\"},")]
    [InlineData("<html>error</html>")]
    public async Task MalformedPrimaryUsesOfficialFallbackWithoutPartialMutation(string primary)
    {
        var transport = new Transport(primary, """{"applist":{"apps":[{"appid":20,"name":"Official"}]}}""");
        var cache = new SteamAppListCache(transport, PathFor("apps.json"));
        await cache.EnsureLoadedAsync();
        Assert.Null(cache.GetName(10));
        Assert.Equal("Official", cache.GetName(20));
        Assert.Equal("api.steampowered.com", transport.Requests[1].Host);
    }
    [Fact]
    public async Task FailedEndpointsUseValidStaleCache()
    {
        File.WriteAllText(PathFor("apps.json"), """{"10":"Stale"}""");
        File.SetLastWriteTimeUtc(PathFor("apps.json"), DateTime.UtcNow.AddDays(-30));
        var cache = new SteamAppListCache(new Transport("broken"), PathFor("apps.json"));
        await cache.EnsureLoadedAsync();
        Assert.Equal("Stale", cache.GetName(10));
    }
    [Fact]
    public async Task InvalidStaleCacheDoesNotPartiallyLoad()
    {
        File.WriteAllText(PathFor("apps.json"), """{"10":"Poison","-2":"Bad"}""");
        var cache = new SteamAppListCache(new Transport("broken"), PathFor("apps.json"));
        await cache.EnsureLoadedAsync();
        Assert.Null(cache.GetName(10));
    }
    private SteamAppInfoCache Details(string body) => new(new CacheService(PathFor("bookkeeping.json")),
        NullLogger<SteamAppInfoCache>.Instance, new Transport(body), PathFor("details"));
    [Theory]
    [InlineData("10")]
    [InlineData("1000")]
    public async Task DirectAndChildKeyedDetailsMatchInnerIdentity(string key)
    {
        var cache = Details("{\"" + key + "\":{\"success\":true,\"data\":{\"steam_appid\":10,\"name\":\"Correct\"}}}");
        Assert.Equal("Correct", (await cache.ResolveAsync(10))!.Name);
        Assert.True(cache.HasFullDetails(10));
    }
    [Theory]
    [InlineData("{\"10\":{\"success\":true}}")]
    [InlineData("{\"10\":{\"success\":\"false\"}}")]
    [InlineData("{\"10\":{\"success\":true,\"data\":{\"steam_appid\":20,\"name\":\"Wrong\"}}}")]
    [InlineData("{\"10\":{\"success\":true,\"data\":{\"steam_appid\":10,\"name\":\"Correct\",\"recommendations\":{\"total\":-1}}}}")]
    [InlineData("{\"10\":")]
    public async Task MalformedDetailsDoNotBecomePermanentUnavailableMarkers(string body)
    {
        var cache = Details(body);
        Assert.Null(await cache.ResolveAsync(10));
        Assert.False(cache.HasFullDetails(10));
        Assert.False(File.Exists(PathFor("details/10.json")));
    }
    [Fact]
    public async Task ExplicitUnavailableDetailsRetainUpstreamNegativeMarker()
    {
        var cache = Details("""{"10":{"success":false}}""");
        Assert.Null(await cache.ResolveAsync(10));
        Assert.Equal("{}", cache.GetFullDetails(10));
    }
    [Theory]
    [InlineData("\"-1\"")]
    [InlineData("\"18446744073709551616\"")]
    public async Task InvalidDepotSizesRejectEntireResponse(string size)
    {
        var cache = new SteamDepotInfo(new Transport("{\"data\":{\"10\":{\"depots\":{\"11\":{\"manifests\":{\"public\":{\"gid\":\"18446744073709551615\",\"size\":" + size + "}}}}}}}"));
        Assert.Null(await cache.GetAsync(10));
    }
    [Fact]
    public async Task DepotNumericAndStringUnsignedFieldsPreserveLargeManifestIds()
    {
        var cache = new SteamDepotInfo(new Transport("""{"data":{"10":{"depots":{"11":{"manifests":{"public":{"gid":"18446744073709551615","size":100}}},"branches":{"public":{"buildid":123}}}}}}"""));
        var info = await cache.GetAsync(10);
        Assert.NotNull(info);
        Assert.Equal(100, Assert.Single(info.Depots).Size);
        Assert.Equal("18446744073709551615", info.Depots[0].PublicManifestId);
        Assert.Equal("123", info.PublicBuildId);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"appid\":10},{\"appid\":-1}]")]
    [InlineData("[{\"appid\":4294967296}]")]
    [InlineData("[{\"appid\":10},")]
    public void HardwareRejectsEntireMalformedList(string json) => Assert.ThrowsAny<Exception>(() => HardwareAppIdService.ParseIds(json));
    [Fact]
    public void HardwareAcceptsRealContractAndDeduplicates() => Assert.Equal(new long[] { 10, 20 }, HardwareAppIdService.ParseIds("""[{"appid":10,"name":"Deck"},{"appid":20},{"appid":10}]"""));
    private sealed class ImageTransport(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    [Fact]
    public async Task JpegMagicWithoutDecodableImageNeverReachesCache()
    {
        var bytes = new byte[1024]; bytes[0] = 255; bytes[1] = 216; bytes[2] = 255;
        var cache = new CoverCache(new ImageTransport(bytes), PathFor("covers"));
        Assert.Null(await cache.EnsureAsync(10, SteamAppInfoCache.GuessHeaderImageUrl(10)));
        Assert.False(File.Exists(PathFor("covers/10.jpg")));
    }
    [Fact]
    public async Task ValidDecodedJpegIsAtomicallyCachedAndAvailableOffline()
    {
        var pixels = new byte[32 * 32 * 3];
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Rgb24, null, pixels, 32 * 3);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        var cache = new CoverCache(new ImageTransport(stream.ToArray()), PathFor("covers"));
        var path = await cache.EnsureAsync(10, SteamAppInfoCache.GuessHeaderImageUrl(10));
        Assert.NotNull(path);
        Assert.Equal(path, cache.GetLocalPath(10));
        Assert.Empty(Directory.GetFiles(PathFor("covers"), "*.tmp"));
    }
    [Fact]
    public void BoundedDiskReadRejectsOversizedFiles()
    {
        File.WriteAllBytes(PathFor("oversize"), new byte[101]);
        Assert.Throws<InvalidDataException>(() => CatalogDataPolicy.Read(PathFor("oversize"), 100));
    }
    [Fact]
    public async Task InvalidCachedDetailsAreRefetchedInsteadOfPermanentlySkipped()
    {
        Directory.CreateDirectory(PathFor("details"));
        File.WriteAllText(PathFor("details/10.json"), "{\"steam_appid\":20,\"name\":\"Wrong\"}");
        var cache = Details("{\"10\":{\"success\":true,\"data\":{\"steam_appid\":10,\"name\":\"Correct\"}}}");
        Assert.False(cache.HasFullDetails(10));
        Assert.True(await cache.EnsureFullDetailsAsync(10));
        Assert.Equal("Correct", cache.GetCached(10)!.Name);
    }
    [Fact]
    public async Task ExistingMalformedCoverIsRedownloadedInsteadOfServed()
    {
        Directory.CreateDirectory(PathFor("covers"));
        File.WriteAllBytes(PathFor("covers/10.jpg"), [255, 216, 255, 0]);
        var cache = new CoverCache(new ImageTransport([255, 216, 255]), PathFor("covers"));
        Assert.Null(cache.GetLocalPath(10));
        Assert.Null(await cache.EnsureAsync(10, SteamAppInfoCache.GuessHeaderImageUrl(10)));
    }

}
