using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Signed out, the app uses only what needs no account: SteamML for games, DLC and depot
/// manifests. Base game 1000 has DLC 2000 (depots 1001, 1002) and DLC 4000 (no depots).</summary>
public sealed class GuestModeTests : IDisposable
{
    private const string KeyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GuestModeTests-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousCache = SteamMlService.CacheFolder;
    private int _steamMlRequests, _depotRequests, _apiRequests;

    public GuestModeTests() => SteamMlService.CacheFolder = Path.Combine(_dir, "steamml");
    public void Dispose()
    {
        SteamMlService.CacheFolder = _previousCache;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    // A manifest-shaped file (framing and identity only), as in DepotCacheMigrationTests.
    private static byte[] ManifestBytes(long depotId, ulong gid)
    {
        var meta = new List<byte> { 0x08 };
        meta.AddRange(Varint((ulong)depotId));
        meta.Add(0x10);
        meta.AddRange(Varint(gid));
        var file = new byte[20 + meta.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x71F617D0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 0x1F4812BE);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)meta.Count);
        meta.CopyTo(file, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16 + meta.Count), 0x32C415AB);
        return file;
    }
    private static IEnumerable<byte> Varint(ulong v)
    {
        while (v >= 0x80) { yield return (byte)(v | 0x80); v >>= 7; }
        yield return (byte)v;
    }

    // SteamML's archive for 1000: keys for 1001 (with manifest 555) and 1003, none for 1002.
    private static byte[] BaseArchive()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("1000.lua").Open()))
                w.Write($"addappid(1000)\naddappid(1001,0,\"{KeyA}\")\nsetManifestid(1001,\"555\")\naddappid(1003,0,\"{KeyC}\")\n");
            using var m = zip.CreateEntry("1001_555.manifest").Open();
            m.Write(ManifestBytes(1001, 555));
        }
        return ms.ToArray();
    }

    private const string DepotInfo = "{\"data\":{\"1000\":{\"depots\":{" +
        "\"1001\":{\"dlcappid\":\"2000\",\"config\":{\"oslist\":\"windows\"},\"manifests\":{\"public\":{\"gid\":\"555\",\"size\":\"10\"}}}," +
        "\"1002\":{\"dlcappid\":\"2000\",\"manifests\":{\"public\":{\"gid\":\"777\",\"size\":\"10\"}}}," +
        "\"1003\":{\"manifests\":{\"public\":{\"gid\":\"888\",\"size\":\"10\"}}}}}}}";

    private LuaToolsApiClient GuestApi()
    {
        var steamMl = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(r =>
        {
            Interlocked.Increment(ref _steamMlRequests);
            return r.RequestUri!.AbsolutePath.EndsWith("/1000.zip")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BaseArchive()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var depots = new SteamDepotInfo(new Stub(r =>
        {
            Interlocked.Increment(ref _depotRequests);
            Assert.EndsWith("/v1/info/1000", r.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DepotInfo, Encoding.UTF8, "application/json") };
        }));
        var api = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(_ =>
        {
            Interlocked.Increment(ref _apiRequests);
            throw new Exception("lua.tools must not be called when signed out");
        }));
        var auth = new AuthService(api, Path.Combine(_dir, "auth.dat"));
        Assert.True(auth.IsGuest);
        return new LuaToolsApiClient(auth, null!, null!, api, steamMl: new SteamMlService(steamMl), depots: depots, dexLua: new DexLuaService(new HttpClient(new Stub(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)))));
    }

    [Fact]
    public async Task SignedOutSourcesArePublicProviders()
    {
        var sources = await GuestApi().CheckSourcesAsync("1000");
        Assert.Equal(new[] { SteamMlService.SourceName, DexLuaService.SourceName }, sources.Keys);
        Assert.Equal("available", sources[SteamMlService.SourceName]);
        Assert.Equal(0, _apiRequests);
    }

    [Fact]
    public async Task SignedOutDlcInfoComesFromDepotInfoAndTheBaseGamesArchive()
    {
        var api = GuestApi();
        var info = (await api.GetDlcInfoAsync("2000", "1000"))!;
        Assert.Equal((2, 1, 1), (info.DepotCount, info.HaveCount, info.MissingCount));
        Assert.Equal(new[] { ("1001", true), ("1002", false) },
            info.Depots.OrderBy(d => d.DepotId).Select(d => (d.DepotId, d.Included)));
        Assert.Equal("windows", info.Depots.Single(d => d.DepotId == "1001").OsList);

        // A DLC with no content of its own needs no archive: zero counts, generatable.
        var empty = (await api.GetDlcInfoAsync("4000", "1000"))!;
        Assert.Equal((0, 0, 0), (empty.DepotCount, empty.HaveCount, empty.MissingCount));
        Assert.Equal(1, _steamMlRequests);   // the archive is fetched once and reused
        Assert.Equal(0, _apiRequests);
    }

    [Fact]
    public async Task SignedOutDlcPackageHasOnlyThatDlcsKeysAndManifests()
    {
        var api = GuestApi();
        var file = await api.GenerateDlcAsync("2000", "1000", null, null);
        Assert.Equal("2000.zip", file.FileName);
        using (var zip = ZipFile.OpenRead(file.FilePath))
        {
            ManifestContentPolicy.ValidateArchive(zip, manifestPackage: true);
            Assert.Equal(new[] { "1001_555.manifest", "2000.lua" }, zip.Entries.Select(e => e.Name).OrderBy(n => n));
            using var reader = new StreamReader(zip.GetEntry("2000.lua")!.Open());
            Assert.Equal($"addappid(2000)\naddappid(1001,0,\"{KeyA}\")\nsetManifestid(1001,\"555\")\n", reader.ReadToEnd());
        }

        var plain = await api.GenerateDlcAsync("4000", "1000", null, null);
        Assert.Equal("4000.lua", plain.FileName);
        Assert.Equal("addappid(4000)\n", File.ReadAllText(plain.FilePath));
        Assert.Equal(0, _apiRequests);
        foreach (var f in new[] { file.FilePath, plain.FilePath }) Directory.Delete(Path.GetDirectoryName(f)!, true);
    }

    [Fact]
    public async Task ArchiveManifestsAreExtractedOnlyForTheVersionSteamMlHas()
    {
        var archive = (await GuestApi().SteamMl.GetArchiveAsync("1000"))!;
        Assert.Equal(new[] { 1001L, 1003L }, archive.Keys.Keys.OrderBy(k => k));
        string out1 = Path.Combine(_dir, "out");
        Assert.NotNull(SteamMlService.ExtractManifest(archive, 1001, "555", out1));
        Assert.True(ManifestJobFactory.IsSteamManifest(Path.Combine(out1, "1001_555.manifest")));
        Assert.Null(SteamMlService.ExtractManifest(archive, 1001, "556", out1));
        Assert.Null(await GuestApi().SteamMl.GetArchiveAsync("9999"));   // not on SteamML
    }
}
