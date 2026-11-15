using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Cabinet's shared lua cache: when a cached copy replaces the chosen source, and when a download is shared.</summary>
public sealed class CabinetLuaCacheTests : IDisposable
{
    private const long App = 570, Depot = 571;
    private static readonly string Key = new('a', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cabinet-lua-" + Guid.NewGuid().ToString("N"));

    public CabinetLuaCacheTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    internal sealed class FakeCabinet : ICabinetLuaCacheClient
    {
        public bool IsConnected { get; set; } = true;
        public CabinetLuaMeta? Meta;
        public byte[]? Package;
        public Exception? Failure;
        public int MetaCalls, DownloadCalls;
        public readonly List<(long AppId, byte[] Package, string Sha256, string Source)> Uploads = [];
        public readonly TaskCompletionSource Uploaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? UploadFailure;

        public Task<CabinetLuaMeta?> GetMetaAsync(long appId, CancellationToken ct)
        {
            MetaCalls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Meta);
        }
        public Task<byte[]?> DownloadAsync(long appId, string sha256, CancellationToken ct)
        {
            DownloadCalls++;
            return Task.FromResult(Package);
        }
        public Task UploadAsync(long appId, byte[] package, string sha256, string source, CancellationToken ct)
        {
            lock (Uploads) Uploads.Add((appId, package, sha256, source));
            Uploaded.TrySetResult();
            return UploadFailure is null ? Task.CompletedTask : Task.FromException(UploadFailure);
        }
    }

    private static string Lua(string manifest, bool commented = false) =>
        $"addappid({App})\naddappid({Depot}, 1, \"{Key}\")\n{(commented ? "-- " : "")}setManifestid({Depot}, \"{manifest}\")\n";

    private static byte[] Zip(params (string Name, object Data)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data as byte[] ?? Encoding.UTF8.GetBytes((string)data));
            }
        return output.ToArray();
    }

    // A manifest-shaped file (framing and identity only), as in GuestModeTests.
    private static byte[] Manifest(long depotId, ulong gid)
    {
        static IEnumerable<byte> Varint(ulong v)
        {
            while (v >= 0x80) { yield return (byte)(v | 0x80); v >>= 7; }
            yield return (byte)v;
        }
        var meta = new List<byte> { 0x08 };
        meta.AddRange(Varint((ulong)depotId));
        meta.Add(0x10);
        meta.AddRange(Varint(gid));
        var file = new byte[20 + meta.Count];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(file, 0x71F617D0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 0x1F4812BE);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)meta.Count);
        meta.CopyTo(file, 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16 + meta.Count), 0x32C415AB);
        return file;
    }

    private static AppDepotInfo Steam(string? publicManifest, params ContentDepot[] more) =>
        new(App, [new ContentDepot(Depot, 1, null, false, "windows", null, publicManifest), .. more], [], []);

    private static CabinetLuaMeta MetaFor(byte[] package, string pin) =>
        new(App, CabinetLuaCache.Sha256(package), package.Length, new Dictionary<long, string> { [Depot] = pin },
            "Morrenus", DateTimeOffset.UnixEpoch, "Alex");

    private CabinetLuaCache Cache(FakeCabinet cabinet, AppDepotInfo? steam, Action? onDepots = null) =>
        new(cabinet, (_, _) => { onDepots?.Invoke(); return Task.FromResult(steam); }, stagingFolder: Path.Combine(_root, "staging"));

    // ── Decisions ────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false, false, true)]   // connected, normal source: always shared
    [InlineData(false, false, false, false)] // not connected
    [InlineData(true, true, false, false)]   // Hubcap: the user's own key
    [InlineData(true, false, true, false)]   // the copy came from Cabinet
    public void UploadsOnlyNormalSourceDownloadsWhileConnected(bool connected, bool needsKey, bool fromCabinet, bool expected) =>
        Assert.Equal(expected, CabinetLuaCachePolicy.ShouldUpload(connected, needsKey, fromCabinet));

    [Fact]
    public void PinsAreCurrentOnlyWhenEveryComparableDepotMatchesSteam()
    {
        var pins = new Dictionary<long, string> { [Depot] = "111", [228989] = "999", [9999] = "5" };
        // 228989 (a shared redistributable stub) and 9999 (unknown to Steam) cannot be compared.
        var shared = new ContentDepot(228989, 0, null, true, null, null) { FromAppId = 228980 };
        Assert.True(ManifestPins.AreCurrent(pins, Steam("111", shared)));
        Assert.False(ManifestPins.AreCurrent(pins, Steam("112", shared)));
        Assert.True(ManifestPins.AreCurrent(pins, Steam(null)));
        Assert.False(ManifestPins.AreCurrent(pins, null)); // depot info unavailable: not current
        Assert.True(ManifestPins.AreCurrent(new Dictionary<long, string>(), Steam("111")));
        // The Builds page's "pinned to an older build" rule is the same comparison.
        Assert.True(ManifestPins.IsBehind("1", "2"));
        Assert.False(ManifestPins.IsBehind("1", "1"));
        Assert.False(ManifestPins.IsBehind(null, "2"));
        Assert.False(ManifestPins.IsBehind("1", null));
    }

    [Fact]
    public void ActivePinsWinOverCommentedOnes()
    {
        var lua = LuaFileParser.ParseText("addappid(1)\n-- setManifestid(2, \"1\")\nsetManifestid(2, \"5\")\n-- setManifestid(3, \"7\")\n", 1)!;
        var pins = ManifestPins.Of(lua);
        Assert.Equal("5", pins[2]);
        Assert.Equal("7", pins[3]);
    }

    // ── Lookup ───────────────────────────────────────────────────────

    [Fact]
    public async Task CurrentCopyIsStagedForTheSameInstall()
    {
        byte[] package = Zip(($"{App}.lua", Lua("111")), ($"{Depot}_111.manifest", Manifest(Depot, 111)), ("readme.txt", "notes"));
        var cabinet = new FakeCabinet { Meta = MetaFor(package, "111"), Package = package };
        var file = await Cache(cabinet, Steam("111")).TryGetCurrentAsync(App, null, CancellationToken.None);
        Assert.NotNull(file);
        Assert.IsType<CabinetCachedFile>(file);
        Assert.Equal(package, File.ReadAllBytes(file!.FilePath));
        Assert.Equal($"{App}.zip", file.FileName);
        // A manifest that is not what its name claims is refused like from any other source.
        byte[] lying = Zip(($"{App}.lua", Lua("111")), ($"{Depot}_111.manifest", Manifest(Depot, 112)));
        var refused = new FakeCabinet { Meta = MetaFor(lying, "111"), Package = lying };
        Assert.Null(await Cache(refused, Steam("111")).TryGetCurrentAsync(App, null, CancellationToken.None));
    }

    [Fact]
    public async Task NotConnectedAsksNothing()
    {
        var cabinet = new FakeCabinet { IsConnected = false };
        bool depots = false;
        Assert.Null(await Cache(cabinet, Steam("111"), () => depots = true).TryGetCurrentAsync(App, null, CancellationToken.None));
        Assert.Equal(0, cabinet.MetaCalls);
        Assert.False(depots);
    }

    [Fact]
    public async Task NothingCachedSkipsSteamLookup()
    {
        var cabinet = new FakeCabinet();
        bool depots = false;
        Assert.Null(await Cache(cabinet, Steam("111"), () => depots = true).TryGetCurrentAsync(App, null, CancellationToken.None));
        Assert.False(depots);
        Assert.Equal(0, cabinet.DownloadCalls);
    }

    [Fact]
    public async Task StaleOrUncheckableCopiesAreNotDownloaded()
    {
        byte[] package = Zip(($"{App}.lua", Lua("111")));
        foreach (var steam in new[] { Steam("222"), null })
        {
            var cabinet = new FakeCabinet { Meta = MetaFor(package, "111"), Package = package };
            Assert.Null(await Cache(cabinet, steam).TryGetCurrentAsync(App, null, CancellationToken.None));
            Assert.Equal(0, cabinet.DownloadCalls);
        }
    }

    [Fact]
    public async Task CopiesThatDoNotMatchTheirMetaOrPolicyFallBack()
    {
        byte[] stale = Zip(($"{App}.lua", Lua("100")));
        byte[] good = Zip(($"{App}.lua", Lua("111")));
        var cases = new (string Why, byte[] Package, CabinetLuaMeta Meta)[]
        {
            ("bytes differ from the meta", good, MetaFor(stale, "111") with { Sha256 = CabinetLuaCache.Sha256(stale) }),
            ("the lua itself is stale", stale, MetaFor(stale, "111")),
            ("an executable inside", Zip(($"{App}.lua", Lua("111")), ("setup.exe", "MZ")), MetaFor(Zip(($"{App}.lua", Lua("111")), ("setup.exe", "MZ")), "111")),
            ("another game's lua", Zip(("440.lua", "addappid(440)\n")), MetaFor(Zip(("440.lua", "addappid(440)\n")), "111")),
            ("not a zip", Encoding.UTF8.GetBytes(Lua("111")), MetaFor(Encoding.UTF8.GetBytes(Lua("111")), "111")),
        };
        foreach (var (why, package, meta) in cases)
        {
            var cabinet = new FakeCabinet { Meta = meta, Package = package };
            var file = await Cache(cabinet, Steam("111")).TryGetCurrentAsync(App, null, CancellationToken.None);
            Assert.True(file is null, why);
        }
        // Evicted between meta and download.
        var gone = new FakeCabinet { Meta = MetaFor(good, "111"), Package = null };
        Assert.Null(await Cache(gone, Steam("111")).TryGetCurrentAsync(App, null, CancellationToken.None));
    }

    [Fact]
    public async Task UnreachableServerFallsBackButCancellationPropagates()
    {
        var cabinet = new FakeCabinet { Failure = new HttpRequestException("down") };
        Assert.Null(await Cache(cabinet, Steam("111")).TryGetCurrentAsync(App, null, CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new FakeCabinet { Failure = new OperationCanceledException(cts.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cache(cancelled, Steam("111")).TryGetCurrentAsync(App, null, cts.Token));
    }

    // ── Sharing ──────────────────────────────────────────────────────

    [Fact]
    public void PackagesAreSharedAsDownloadedOrWrappedDeterministically()
    {
        var cache = Cache(new FakeCabinet(), Steam("111"));
        byte[] zip = Zip(($"{App}.lua", Lua("111")), ("readme.txt", "hi"));
        string zipPath = Path.Combine(_root, $"{App}.zip");
        File.WriteAllBytes(zipPath, zip);
        Assert.Equal(zip, cache.ReadPackage(zipPath, App));

        // A bare lua keeps a build-id name, so the cached copy installs pinned like the original.
        string luaPath = Path.Combine(_root, $"{App}_18234567.lua");
        File.WriteAllText(luaPath, Lua("111"));
        byte[] wrapped = cache.ReadPackage(luaPath, App)!;
        Assert.Equal(wrapped, cache.ReadPackage(luaPath, App));
        using (var archive = new ZipArchive(new MemoryStream(wrapped)))
            Assert.Equal($"{App}_18234567.lua", Assert.Single(archive.Entries).FullName);
        Assert.Equal($"{App}.lua", CabinetLuaCache.LuaEntryName($"{App}.zip", App));
        Assert.Equal($"{App}.lua", CabinetLuaCache.LuaEntryName("../x.lua", App));

        // Nothing a cache could serve: no lua at all.
        string manifests = Path.Combine(_root, "only.zip");
        File.WriteAllBytes(manifests, Zip(($"{Depot}_111.manifest", "m")));
        Assert.Null(cache.ReadPackage(manifests, App));
        Assert.Null(cache.ReadPackage(Path.Combine(_root, "missing.zip"), App));
    }

    [Fact]
    public async Task UploadFailuresAreSwallowed()
    {
        var cabinet = new FakeCabinet { UploadFailure = new HttpRequestException("403") };
        byte[] zip = Zip(($"{App}.lua", Lua("111")));
        await Cache(cabinet, Steam("111")).ShareAsync(App, zip, "Morrenus", CancellationToken.None);
        var upload = Assert.Single(cabinet.Uploads);
        Assert.Equal(CabinetLuaCache.Sha256(zip), upload.Sha256);
        Assert.Equal("Morrenus", upload.Source);
        cabinet.IsConnected = false;
        await Cache(cabinet, Steam("111")).ShareAsync(App, zip, "Morrenus", CancellationToken.None);
        Assert.Single(cabinet.Uploads);
    }

    // ── The manifest job ─────────────────────────────────────────────

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(respond(request)); }
    }

    private sealed class Job
    {
        public required ManifestJobFactory Factory;
        public required Stub DexLua, Hubcap;
        public required SteamService Steam;
    }

    private Job Factory(FakeCabinet cabinet, AppDepotInfo? steam, string sourceLua)
    {
        string steamDir = Path.Combine(_root, "steam");
        Directory.CreateDirectory(steamDir);
        File.WriteAllText(Path.Combine(steamDir, "steam.exe"), "inert fixture");
        var settings = new SettingsService(new AppSettings { SteamPathOverride = steamDir }) { HubcapApiKey = "smm_" + new string('a', 96) };
        var steamService = new SteamService(settings);
        var installer = new LuaInstaller(steamService, settings, new CacheService(Path.Combine(_root, "cache.json")),
            new LuaVault(() => steamService.StPlugInDir, Path.Combine(_root, "vault")));
        var dexLua = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sourceLua) });
        var hubcap = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(($"{App}.lua", sourceLua))) });
        var unused = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var api = new LuaToolsApiClient(new AuthService(unused, Path.Combine(_root, "auth.dat")), null!, null!, unused,
            dexLua: new DexLuaService(new HttpClient(dexLua)));
        var covers = new CoverCache(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), Path.Combine(_root, "covers"));
        var factory = new ManifestJobFactory(api, new HubcapService(new HttpClient(hubcap) { BaseAddress = new Uri(AppConfig.HubcapBaseUrl) }),
            settings, installer, null!, covers, null!, null!, null!, null!, null!, null!, Cache(cabinet, steam));
        return new Job { Factory = factory, DexLua = dexLua, Hubcap = hubcap, Steam = steamService };
    }

    private static async Task<(DownloadedFile File, JobResult Result)> Run(DownloadJob job)
    {
        var item = new DownloadItem(job);
        var file = await job.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);
        return (file, await job.InstallAsync(file, item, CancellationToken.None));
    }

    [Fact]
    public async Task NormalSourceInstallIsSharedInTheBackground()
    {
        var cabinet = new FakeCabinet();
        var f = Factory(cabinet, Steam("111"), Lua("111"));
        var (file, result) = await Run(f.Factory.CreateManifestJob(App, "Alpha", DexLuaService.SourceName, needsKey: false));
        Assert.True(result.Ok, result.Message);
        Assert.IsNotType<CabinetCachedFile>(file);
        Assert.Equal(1, f.DexLua.Calls);
        await cabinet.Uploaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var upload = Assert.Single(cabinet.Uploads);
        Assert.Equal(DexLuaService.SourceName, upload.Source);
        Assert.Equal("111", Assert.Single(CabinetLuaCache.PackagePins(upload.Package, App)).Value);
    }

    [Fact]
    public async Task CurrentCachedCopyReplacesTheSourceAndIsNotReuploaded()
    {
        byte[] package = Zip(($"{App}.lua", Lua("111")), ($"{Depot}_111.manifest", Manifest(Depot, 111)));
        var cabinet = new FakeCabinet { Meta = MetaFor(package, "111"), Package = package };
        var f = Factory(cabinet, Steam("111"), Lua("999"));
        var (file, result) = await Run(f.Factory.CreateManifestJob(App, "Alpha", DexLuaService.SourceName, needsKey: false));
        Assert.True(result.Ok, result.Message);
        Assert.IsType<CabinetCachedFile>(file);
        Assert.Equal(0, f.DexLua.Calls);
        Assert.Contains("\"111\"", File.ReadAllText(Path.Combine(f.Steam.StPlugInDir!, $"{App}.lua")));
        Assert.True(File.Exists(Path.Combine(f.Steam.DepotCacheDir!, $"{Depot}_111.manifest")));
        Assert.False(File.Exists(file.FilePath)); // consumed by the install, like any staged download
        await Task.Delay(200);
        Assert.Empty(cabinet.Uploads);
    }

    [Fact]
    public async Task StaleCachedCopyIsReplacedByTheFreshDownload()
    {
        byte[] stale = Zip(($"{App}.lua", Lua("100")));
        var cabinet = new FakeCabinet { Meta = MetaFor(stale, "100"), Package = stale };
        var f = Factory(cabinet, Steam("111"), Lua("111"));
        var (_, result) = await Run(f.Factory.CreateManifestJob(App, "Alpha", DexLuaService.SourceName, needsKey: false));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, f.DexLua.Calls);
        Assert.Equal(0, cabinet.DownloadCalls);
        await cabinet.Uploaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("111", CabinetLuaCache.PackagePins(Assert.Single(cabinet.Uploads).Package, App)[Depot]);
    }

    [Fact]
    public async Task HubcapDownloadsAreNeverShared()
    {
        var cabinet = new FakeCabinet();
        var f = Factory(cabinet, Steam("111"), Lua("111"));
        var (_, result) = await Run(f.Factory.CreateManifestJob(App, "Alpha", "Hubcap", needsKey: true));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, f.Hubcap.Calls);
        await Task.Delay(200);
        Assert.Empty(cabinet.Uploads);
    }

    [Fact]
    public async Task FailedInstallsAreNotShared()
    {
        var cabinet = new FakeCabinet();
        var f = Factory(cabinet, Steam("111"), Lua("111"));
        var job = f.Factory.CreateManifestJob(App, "Alpha", DexLuaService.SourceName, needsKey: false);
        var item = new DownloadItem(job);
        var file = await job.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);
        // A file where stplug-in should be: the install cannot write the lua.
        string plugIn = f.Steam.StPlugInDir!;
        if (Directory.Exists(plugIn)) Directory.Delete(plugIn, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(plugIn)!);
        File.WriteAllText(plugIn, "blocker");
        var result = await job.InstallAsync(file, item, CancellationToken.None);
        Assert.False(result.Ok);
        await Task.Delay(200);
        Assert.Empty(cabinet.Uploads);
    }

    // ── Transport ────────────────────────────────────────────────────

    private sealed class CredentialBox(CabinetCredentials? value) : ICabinetCredentialSource
    {
        public CabinetCredentials? Credentials { get; set; } = value;
    }

    private const string Token = "cab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ClientSendsBearerAndOriginAndParsesMeta()
    {
        var requests = new List<(HttpRequestMessage Request, byte[]? Body)>();
        string sha = new('b', 64);
        var transport = new Stub(r =>
        {
            requests.Add((r, r.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult()));
            if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/apps/570/lua/meta")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    $"{{\"appid\":\"570\",\"sha256\":\"{sha}\",\"size\":12,\"pins\":{{\"571\":\"111\"}},\"source\":\"Morrenus\",\"uploadedAt\":1700000000000,\"uploader\":\"Alex\"}}") };
            if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/apps/440/lua/meta")
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\"}") };
            if (r.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var credentials = new CredentialBox(new CabinetCredentials("https://cabinet.example.test", Token, "device1"));
        using var client = new CabinetLuaCacheClient(credentials, transport);

        var meta = await client.GetMetaAsync(App, CancellationToken.None);
        Assert.Equal(sha, meta!.Sha256);
        Assert.Equal("111", meta.Pins[Depot]);
        Assert.Equal("Alex", meta.Uploader);
        Assert.Null(await client.GetMetaAsync(440, CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, await client.DownloadAsync(App, sha, CancellationToken.None));
        await client.UploadAsync(App, [9, 9], sha, "Ryuu\n", CancellationToken.None);

        foreach (var (request, _) in requests)
        {
            Assert.Equal("Bearer " + Token, request.Headers.Authorization!.ToString());
            Assert.Equal("https://cabinet.example.test", request.Headers.GetValues("Origin").Single());
            Assert.Equal("cabinet.example.test", request.RequestUri!.Host);
        }
        Assert.Equal($"/api/apps/570/lua?sha256={sha}", requests[2].Request.RequestUri!.PathAndQuery);
        var put = requests[3];
        Assert.Equal(HttpMethod.Put, put.Request.Method);
        Assert.Equal($"/api/apps/570/lua?sha256={sha}&source=Ryuu&deviceId=device1", put.Request.RequestUri!.PathAndQuery);
        Assert.Equal("application/zip", put.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 9, 9 }, put.Body);

        // The token never appears where a log line could pick it up.
        Assert.DoesNotContain(Token, credentials.Credentials!.ToString());
        credentials.Credentials = null;
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetMetaAsync(App, CancellationToken.None));
    }

    [Fact]
    public void MalformedMetaIsRefused()
    {
        string sha = new('c', 64);
        string Meta(string appid = "570", string hash = "", string pins = "{\"571\":\"111\"}", string size = "1") =>
            $"{{\"appid\":\"{appid}\",\"sha256\":\"{(hash == "" ? sha : hash)}\",\"size\":{size},\"pins\":{pins},\"source\":\"s\",\"uploadedAt\":1,\"uploader\":\"u\"}}";
        Assert.NotNull(CabinetLuaCacheClient.ParseMeta(Encoding.UTF8.GetBytes(Meta()), App));
        foreach (string bad in new[] { Meta(appid: "440"), Meta(hash: "ABC"), Meta(pins: "{\"x\":\"1\"}"), Meta(pins: "{\"1\":\"a\"}"),
                     Meta(pins: "[]"), Meta(size: "0"), Meta(size: "999999999999"), "{", "[]" })
            Assert.Throws<InvalidDataException>(() => CabinetLuaCacheClient.ParseMeta(Encoding.UTF8.GetBytes(bad), App));
    }
}
