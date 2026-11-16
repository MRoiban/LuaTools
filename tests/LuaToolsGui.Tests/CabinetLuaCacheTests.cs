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
        public Exception? DownloadFailure;
        public Task<byte[]?> DownloadAsync(long appId, string sha256, long size, CancellationToken ct)
        {
            DownloadCalls++;
            if (DownloadFailure is not null) throw DownloadFailure;
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

    [Theory]
    [InlineData("Sadie (Morrenus)", false, true)] // the catalogue marks it as the user's own key
    [InlineData("Hubcap", true, true)]
    [InlineData("Ryuu", false, false)]
    [InlineData("SomethingNew", false, false)]
    public void PersonalKeySourcesAreRecognisedWhateverPathRequestedThem(string source, bool needsKey, bool expected) =>
        Assert.Equal(expected, CabinetLuaCachePolicy.UsesPersonalKey(source, needsKey));

    [Fact]
    public void PinsAreReadOnlyFromActiveFullLineDeclarations()
    {
        // Both quote styles, an optional size, ';' and a trailing comment, as ManifestContentPolicy allows.
        var pins = CabinetPackage.ReadPins($"addappid({App}) -- Alpha\nsetManifestid({Depot}, '111', 5); -- note\nsetManifestid(572, \"222\")\n-- setManifestid(573, \"9\")\n", App);
        Assert.Equal(new Dictionary<long, string> { [Depot] = "111", [572] = "222" }, pins);
    }

    [Theory]
    // A single-quoted stale pin with a "current" pin hidden in its trailing comment.
    [InlineData("addappid(570)\nsetManifestid(571,'100') -- setManifestid(571,\"111\")\n")]
    // A stale pin followed by a commented-out "current" one.
    [InlineData("addappid(570)\nsetManifestid(571, '100')\n-- setManifestid(571, \"111\")\n")]
    public void HiddenPinsCannotVouchForAStalePin(string lua)
    {
        Assert.Equal("100", CabinetPackage.ReadPins(lua, App)[Depot]);
        Assert.False(new CabinetPackage(CabinetPackage.ReadPins(lua, App), []).IsCurrent(Steam("111")));
    }

    [Theory]
    [InlineData("addappid(570)\nsetManifestid(571, \"111\")\nsetManifestid(571, \"100\")\n")] // pinned twice
    [InlineData("addappid(570)\nsetManifestid(571, \"111\")\nsetManifestid(571, \"111\")\n")] // even identically
    [InlineData("-- addappid(570)\naddappid(571, 1, \"aa\")\n")]                              // app only commented out
    [InlineData("addappid(440)\n")]                                                              // another app
    public void AmbiguousOrUndeclaredLuasAreRefused(string lua) =>
        Assert.Throws<InvalidDataException>(() => CabinetPackage.ReadPins(lua, App));

    [Fact]
    public void ManifestFilesMustAlsoBeCurrent()
    {
        // A pin-less lua follows Steam's latest build, unless it carries a stale manifest file.
        byte[] pinless = Zip(($"{App}.lua", $"addappid({App})\naddappid({Depot}, 1, \"{Key}\")\n"), ($"{Depot}_100.manifest", Manifest(Depot, 100)));
        var package = CabinetPackage.Read(pinless, App);
        Assert.Empty(package.Pins);
        Assert.False(package.IsCurrent(Steam("111")));
        Assert.True(package.IsCurrent(Steam("100")));
        Assert.True(CabinetPackage.Read(Zip(($"{App}.lua", $"addappid({App})\n")), App).IsCurrent(Steam("111")));
        Assert.False(CabinetPackage.Read(Zip(($"{App}.lua", $"addappid({App})\n")), App).IsCurrent(null));
        // A current pin with a stale manifest file next to it.
        byte[] mixed = Zip(($"{App}.lua", Lua("111")), ($"{Depot}_100.manifest", Manifest(Depot, 100)));
        Assert.False(CabinetPackage.Read(mixed, App).IsCurrent(Steam("111")));
    }

    [Fact]
    public void TransferDeadlineGrowsWithSizeWithinBounds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), CabinetLuaCachePolicy.TransferDeadline(1));
        Assert.Equal(TimeSpan.FromSeconds(14), CabinetLuaCachePolicy.TransferDeadline(1024 * 1024));
        Assert.Equal(TimeSpan.FromSeconds(120), CabinetLuaCachePolicy.TransferDeadline(CabinetLuaCacheClient.MaxPackageBytes));
        Assert.Equal(TimeSpan.FromSeconds(3), CabinetLuaCacheClient.MetaTimeout);
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
            ("spoofed pin in a comment", Spoof, MetaFor(Spoof, "111")),
            ("stale manifest file", StaleManifest, MetaFor(StaleManifest, "111")),
            ("app only commented out", Commented, MetaFor(Commented, "111")),
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

    private static readonly byte[] Spoof = Zip(($"{App}.lua", $"addappid({App})\naddappid({Depot}, 1, \"{Key}\")\nsetManifestid({Depot},'100') -- setManifestid({Depot},\"111\")\n"));
    private static readonly byte[] StaleManifest = Zip(($"{App}.lua", Lua("111")), ($"{Depot}_100.manifest", Manifest(Depot, 100)));
    private static readonly byte[] Commented = Zip(($"{App}.lua", $"-- addappid({App})\naddappid({Depot}, 1, \"{Key}\")\nsetManifestid({Depot}, \"111\")\n"));

    [Fact]
    public async Task ServerFailuresSkipTheCacheForFiveMinutes()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        byte[] package = Zip(($"{App}.lua", Lua("111")));
        var cabinet = new FakeCabinet { Failure = new TimeoutException("slow"), Meta = MetaFor(package, "111"), Package = package };
        var cache = new CabinetLuaCache(cabinet, (_, _) => Task.FromResult<AppDepotInfo?>(Steam("111")), stagingFolder: Path.Combine(_root, "staging"))
            { UtcNow = () => now };
        Assert.Null(await cache.TryGetCurrentAsync(App, null, CancellationToken.None));
        Assert.True(cache.BackingOff);
        cabinet.Failure = null;
        // Within the window: no request at all, for any game, and no uploads either.
        now += CabinetLuaCache.FailureBackoff - TimeSpan.FromSeconds(1);
        Assert.Null(await cache.TryGetCurrentAsync(440, null, CancellationToken.None));
        await cache.ShareAsync(App, package, "Ryuu", CancellationToken.None);
        Assert.Equal(1, cabinet.MetaCalls);
        Assert.Empty(cabinet.Uploads);
        // After it, the cache is used again.
        now += TimeSpan.FromSeconds(2);
        Assert.NotNull(await cache.TryGetCurrentAsync(App, null, CancellationToken.None));
        Assert.Equal(2, cabinet.MetaCalls);

        // A stalled blob transfer trips the window too; "nothing cached" and stale copies do not.
        cabinet.DownloadFailure = new TimeoutException("stalled");
        Assert.Null(await cache.TryGetCurrentAsync(App, null, CancellationToken.None));
        Assert.True(cache.BackingOff);
        var quiet = new CabinetLuaCache(new FakeCabinet(), (_, _) => Task.FromResult<AppDepotInfo?>(Steam("111")), stagingFolder: Path.Combine(_root, "staging"));
        Assert.Null(await quiet.TryGetCurrentAsync(App, null, CancellationToken.None));
        Assert.False(quiet.BackingOff);
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

    [Fact]
    public async Task OnlyCurrentPackagesAreShared()
    {
        var cabinet = new FakeCabinet();
        // An older build, Steam's build unknown, a stale manifest file, a spoofed pin, an undeclared app.
        await Cache(cabinet, Steam("222")).ShareAsync(App, Zip(($"{App}.lua", Lua("111"))), "Ryuu", CancellationToken.None);
        await Cache(cabinet, null).ShareAsync(App, Zip(($"{App}.lua", Lua("111"))), "Ryuu", CancellationToken.None);
        foreach (byte[] bad in new[] { StaleManifest, Spoof, Commented })
            await Cache(cabinet, Steam("111")).ShareAsync(App, bad, "Ryuu", CancellationToken.None);
        Assert.Empty(cabinet.Uploads);
        // Slow depot info counts as unavailable rather than holding the upload.
        var slow = new CabinetLuaCache(cabinet, (_, _) => new TaskCompletionSource<AppDepotInfo?>().Task, stagingFolder: Path.Combine(_root, "staging"));
        await slow.ShareAsync(App, Zip(($"{App}.lua", Lua("111"))), "Ryuu", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(cabinet.Uploads);
        await Cache(cabinet, Steam("111")).ShareAsync(App, Zip(($"{App}.lua", Lua("111"))), "Ryuu", CancellationToken.None);
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

    private Job Factory(FakeCabinet cabinet, AppDepotInfo? steam, string sourceLua, RelayServer? relay = null, HttpStatusCode dexLuaStatus = HttpStatusCode.OK)
    {
        string steamDir = Path.Combine(_root, "steam");
        Directory.CreateDirectory(steamDir);
        File.WriteAllText(Path.Combine(steamDir, "steam.exe"), "inert fixture");
        var settings = new SettingsService(new AppSettings { SteamPathOverride = steamDir }) { HubcapApiKey = "smm_" + new string('a', 96) };
        var steamService = new SteamService(settings);
        var installer = new LuaInstaller(steamService, settings, new CacheService(Path.Combine(_root, "cache.json")),
            new LuaVault(() => steamService.StPlugInDir, Path.Combine(_root, "vault")));
        var dexLua = new Stub(_ => new HttpResponseMessage(dexLuaStatus) { Content = new StringContent(sourceLua) });
        var hubcap = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(($"{App}.lua", sourceLua))) });
        var unused = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var api = new LuaToolsApiClient(new AuthService(unused, Path.Combine(_root, "auth.dat")), null!, null!, unused,
            dexLua: new DexLuaService(new HttpClient(dexLua)));
        var covers = new CoverCache(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), Path.Combine(_root, "covers"));
        var cache = Cache(cabinet, steam);
        var factory = new ManifestJobFactory(api, new HubcapService(new HttpClient(hubcap) { BaseAddress = new Uri(AppConfig.HubcapBaseUrl) }),
            settings, installer, null!, covers, null!, null!, null!, null!, null!, null!, cache, relay is null ? null
            : new CabinetHubcapRelay(relay, cache, Microsoft.Extensions.Logging.Abstractions.NullLogger<CabinetHubcapRelay>.Instance) { Delay = relay.Delay, UtcNow = () => relay.Clock, ConfirmCopy = relay.Confirm });
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
        Assert.Equal("111", Assert.Single(CabinetPackage.Read(upload.Package, App).Pins).Value);
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
        Assert.Equal("111", CabinetPackage.Read(Assert.Single(cabinet.Uploads).Package, App).Pins[Depot]);
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

    // ── Cabinet first, automatic fallback ────────────────────────────

    /// <summary>The server's relay: the POST answer is also every poll's answer.</summary>
    internal sealed class RelayServer : ICabinetHubcapClient
    {
        public bool IsConnected { get; set; } = true;
        public CabinetRelayStatus Answer = new(CabinetRelayStatus.Failed, Error: "no_manifest");
        public Exception? RequestFailure;
        public readonly List<string?> Requests = [];
        public int Polls;
        public DateTimeOffset Clock = DateTimeOffset.UnixEpoch;
        public Action<int>? OnDelay;
        public int Delays;
        public bool Accept = true;
        public readonly List<CabinetCopyQuestion> Asked = [];
        public Func<TimeSpan, CancellationToken, Task> Delay => (span, ct) =>
        {
            Clock += span;
            OnDelay?.Invoke(++Delays);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        public Func<long, string, CabinetLuaMeta, CabinetCopyQuestion, CancellationToken, Task<bool>> Confirm =>
            (_, _, _, question, _) => { Asked.Add(question); return Task.FromResult(Accept); };
        public Task<bool> GetRelayEnabledAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<CabinetRelayStatus> RequestRelayAsync(long appId, string? staleSha256, CancellationToken ct)
        {
            Requests.Add(staleSha256);
            return RequestFailure is null ? Task.FromResult(Answer) : Task.FromException<CabinetRelayStatus>(RequestFailure);
        }
        public Task<CabinetRelayStatus?> GetRelayStatusAsync(long appId, CancellationToken ct) { Polls++; return Task.FromResult<CabinetRelayStatus?>(Answer); }
    }

    private static Func<CancellationToken, Task<IReadOnlyList<ManifestJobFactory.FallbackSource>>> Then(params (string Name, bool NeedsKey)[] sources) =>
        _ => Task.FromResult<IReadOnlyList<ManifestJobFactory.FallbackSource>>(sources.Select(s => new ManifestJobFactory.FallbackSource(s.Name, s.NeedsKey)).ToList());

    private static DownloadJob Automatic(Job f, Func<CancellationToken, Task<IReadOnlyList<ManifestJobFactory.FallbackSource>>> fallbacks, bool interactive = true) =>
        f.Factory.CreateManifestJob(App, "Alpha", CabinetHubcapRelay.SourceName, needsKey: false, interactive: interactive, fallbacks: fallbacks);

    private static async Task<(DownloadItem Item, DownloadedFile File, JobResult Result)> RunItem(DownloadJob job, CancellationToken ct = default)
    {
        var item = new DownloadItem(job);
        var file = await job.DownloadAsync(item, new Progress<DownloadProgress>(), ct);
        return (item, file, await job.InstallAsync(file, item, ct));
    }

    [Theory]
    [InlineData("no_manifest")]
    [InlineData("quota_exhausted")]
    [InlineData("key_rejected")]
    [InlineData("relay_disabled")]
    [InlineData("failed")]
    [InlineData("upstream_not_newer")]
    [InlineData("ready_but_stale")]
    [InlineData("unverifiable")]
    [InlineData("disconnected")]
    [InlineData("unreachable")]
    [InlineData("timeout")]
    public async Task AnAutomaticCabinetAddFallsBackSilentlyAndTheFallbackFillsTheCache(string kind)
    {
        byte[] stale = Zip(($"{App}.lua", Lua("100")));
        var cabinet = new FakeCabinet();
        var relay = new RelayServer();
        AppDepotInfo? steam = Steam("111");
        switch (kind)
        {
            case "failed": relay.Answer = new(CabinetRelayStatus.Failed, Error: "hubcap_unavailable"); break;
            case "upstream_not_newer":
                cabinet.Meta = MetaFor(stale, "100"); cabinet.Package = stale;
                relay.Answer = new(CabinetRelayStatus.NotNewer, Meta: MetaFor(stale, "100")); break;
            case "ready_but_stale": cabinet.Package = stale; relay.Answer = new(CabinetRelayStatus.Ready, Meta: MetaFor(stale, "100")); break;
            case "unverifiable": cabinet.Meta = MetaFor(stale, "100"); cabinet.Package = stale; steam = null; break;
            case "disconnected": relay.IsConnected = false; break;
            case "unreachable": relay.RequestFailure = new HttpRequestException("down"); break;
            case "timeout": relay.Answer = new(CabinetRelayStatus.Running); break;
            default: relay.Answer = new(CabinetRelayStatus.Failed, Error: kind); break;
        }
        var f = Factory(cabinet, steam, Lua("111"), relay);
        var (item, file, result) = await RunItem(Automatic(f, Then((DexLuaService.SourceName, false))));
        Assert.True(result.Ok, result.Message);
        Assert.IsNotType<CabinetCachedFile>(file);
        Assert.Equal(1, f.DexLua.Calls);
        Assert.Empty(relay.Asked);                              // no question on the way
        Assert.Equal(DexLuaService.SourceName, item.SubTitle);  // the item names the source that installed it
        if (kind == "timeout") Assert.Equal(CabinetHubcapRelay.AutomaticTimeout, relay.Clock - DateTimeOffset.UnixEpoch);
        if (kind == "unverifiable") Assert.Empty(relay.Requests);
        if (steam is null) return; // without Steam's build nothing is shared, by the existing rule
        await cabinet.Uploaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var upload = Assert.Single(cabinet.Uploads);
        Assert.Equal(DexLuaService.SourceName, upload.Source);
        Assert.Equal("111", CabinetPackage.Read(upload.Package, App).Pins[Depot]);
    }

    [Fact]
    public async Task AFallbackToTheUsersOwnHubcapKeyIsNeverShared()
    {
        var cabinet = new FakeCabinet();
        var f = Factory(cabinet, Steam("111"), Lua("111"), new RelayServer());
        var (item, _, result) = await RunItem(Automatic(f, Then((AcquisitionCoordinator.HubcapSource, true), (DexLuaService.SourceName, false))));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, f.Hubcap.Calls);
        Assert.Equal(0, f.DexLua.Calls); // the first usable fallback delivered
        Assert.Equal("Sadie (Hubcap)", item.SubTitle);
        await Task.Delay(200);
        Assert.Empty(cabinet.Uploads);
    }

    [Fact]
    public async Task ACurrentCopyFromCabinetNeedsNoFallbackAndIsNotReuploaded()
    {
        byte[] current = Zip(($"{App}.lua", Lua("111")));
        var cabinet = new FakeCabinet { Package = current };
        var relay = new RelayServer { Answer = new(CabinetRelayStatus.Ready, Meta: MetaFor(current, "111")) };
        var f = Factory(cabinet, Steam("111"), Lua("999"), relay);
        var (item, file, result) = await RunItem(Automatic(f, Then((DexLuaService.SourceName, false))));
        Assert.True(result.Ok, result.Message);
        Assert.IsType<CabinetCachedFile>(file);
        Assert.Equal(0, f.DexLua.Calls);
        Assert.Equal(CabinetHubcapRelay.SourceName, item.SubTitle);
        await Task.Delay(200);
        Assert.Empty(cabinet.Uploads);
    }

    [Fact]
    public async Task PickingCabinetExplicitlyNeverFallsBack()
    {
        var relay = new RelayServer();
        var f = Factory(new FakeCabinet(), Steam("111"), Lua("111"), relay);
        var job = f.Factory.CreateManifestJob(App, "Alpha", CabinetHubcapRelay.SourceName, needsKey: false);
        var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => job.DownloadAsync(new DownloadItem(job), new Progress<DownloadProgress>(), CancellationToken.None));
        Assert.Equal(LuaToolsGui.Resources.Strings.Cabinet_Relay_Err_NoManifest, ex.Message);
        Assert.Equal(0, f.DexLua.Calls);
        // ...and keeps asking its own question about an older copy.
        byte[] stale = Zip(($"{App}.lua", Lua("100")));
        var cabinet = new FakeCabinet { Meta = MetaFor(stale, "100"), Package = stale };
        relay = new RelayServer { Answer = new(CabinetRelayStatus.NotNewer, Meta: MetaFor(stale, "100")) };
        f = Factory(cabinet, Steam("111"), Lua("111"), relay);
        var (_, file, result) = await RunItem(f.Factory.CreateManifestJob(App, "Alpha", CabinetHubcapRelay.SourceName, needsKey: false));
        Assert.True(result.Ok, result.Message);
        Assert.IsType<CabinetCachedFile>(file);
        Assert.Equal(new[] { CabinetCopyQuestion.NothingNewer }, relay.Asked);
        Assert.Equal(0, f.DexLua.Calls);
    }

    [Fact]
    public async Task CancellingDuringCabinetOrAFallbackCancelsTheWholeAdd()
    {
        // While Cabinet is still queued on the server.
        using var during = new CancellationTokenSource();
        var relay = new RelayServer { Answer = new(CabinetRelayStatus.Queued, 2), OnDelay = n => { if (n == 2) during.Cancel(); } };
        var f = Factory(new FakeCabinet(), Steam("111"), Lua("111"), relay);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunItem(Automatic(f, Then((DexLuaService.SourceName, false))), during.Token));
        Assert.Equal(0, f.DexLua.Calls);
        // While moving on to the fallbacks.
        using var moving = new CancellationTokenSource();
        f = Factory(new FakeCabinet(), Steam("111"), Lua("111"), new RelayServer());
        var fallbacks = Then((DexLuaService.SourceName, false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunItem(Automatic(f, ct => { moving.Cancel(); return fallbacks(ct); }), moving.Token));
        Assert.Equal(0, f.DexLua.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenEverySourceFailedCabinetsOwnCopyIsTheAskedLastResort(bool interactive)
    {
        byte[] stale = Zip(($"{App}.lua", Lua("100")));
        var cabinet = new FakeCabinet { Meta = MetaFor(stale, "100"), Package = stale };
        var relay = new RelayServer { Answer = new(CabinetRelayStatus.NotNewer, Meta: MetaFor(stale, "100")) };
        var f = Factory(cabinet, Steam("111"), Lua("111"), relay, dexLuaStatus: HttpStatusCode.NotFound);
        var job = Automatic(f, Then((DexLuaService.SourceName, false)), interactive);
        if (interactive)
        {
            var (item, file, result) = await RunItem(job);
            Assert.True(result.Ok, result.Message);
            Assert.IsType<CabinetCachedFile>(file);
            Assert.Equal(new[] { CabinetCopyQuestion.NoOtherSource }, relay.Asked);
            Assert.Equal(CabinetHubcapRelay.SourceName, item.SubTitle);
        }
        else
        {
            // CLI/agent adds never install it.
            var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => RunItem(job));
            Assert.True(ex.IsCancellation);
            Assert.Empty(relay.Asked);
        }
        Assert.Equal(1, f.DexLua.Calls); // the fallback was tried first
        await Task.Delay(200);
        Assert.Empty(cabinet.Uploads);
    }

    [Fact]
    public async Task WithoutACachedCopyTheLastFallbacksErrorIsReported()
    {
        var relay = new RelayServer();
        var f = Factory(new FakeCabinet(), Steam("111"), Lua("111"), relay, dexLuaStatus: HttpStatusCode.NotFound);
        await Assert.ThrowsAnyAsync<Exception>(() => RunItem(Automatic(f, Then((DexLuaService.SourceName, false)))));
        Assert.Empty(relay.Asked);
        // No fallback at all: Cabinet's own reason.
        f = Factory(new FakeCabinet(), Steam("111"), Lua("111"), relay);
        var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => RunItem(Automatic(f, Then())));
        Assert.Equal(LuaToolsGui.Resources.Strings.Cabinet_Relay_Err_NoManifest, ex.Message);
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
        var credentials = new CredentialBox(new CabinetCredentials("https://Cabinet.Example.test:443/", Token, "device1")); // normalized like the engine
        using var client = new CabinetLuaCacheClient(credentials, transport);

        var meta = await client.GetMetaAsync(App, CancellationToken.None);
        Assert.Equal(sha, meta!.Sha256);
        Assert.Equal("111", meta.Pins[Depot]);
        Assert.Equal("Alex", meta.Uploader);
        Assert.Null(await client.GetMetaAsync(440, CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, await client.DownloadAsync(App, sha, 3, CancellationToken.None));
        // A blob larger than its meta said is refused.
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(App, sha, 2, CancellationToken.None));
        await client.UploadAsync(App, [9, 9], sha, "Ryuu\n", CancellationToken.None);

        foreach (var (request, _) in requests)
        {
            Assert.Equal("Bearer " + Token, request.Headers.Authorization!.ToString());
            Assert.Equal("https://cabinet.example.test", request.Headers.GetValues("Origin").Single());
            Assert.Equal("cabinet.example.test", request.RequestUri!.Host);
        }
        Assert.Equal($"/api/apps/570/lua?sha256={sha}", requests[2].Request.RequestUri!.PathAndQuery);
        var put = requests[4];
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

    // ── For the Hubcap relay (CabinetHubcapRelayTests covers the flow) ──

    [Fact]
    public async Task LookupNamesTheStaleCopyAndNothingCachedSeparately()
    {
        byte[] package = Zip(($"{App}.lua", Lua("100")));
        var cabinet = new FakeCabinet { Meta = MetaFor(package, "100"), Package = package };
        var stale = await Cache(cabinet, Steam("111")).LookupAsync(App, null, CancellationToken.None);
        Assert.Equal(CabinetLuaCache.LookupOutcome.Stale, stale.Outcome);
        Assert.Equal(CabinetLuaCache.Sha256(package), stale.StaleSha256);
        Assert.Equal(0, cabinet.DownloadCalls); // a stale copy isn't downloaded
        var none = await Cache(new FakeCabinet(), Steam("111")).LookupAsync(App, null, CancellationToken.None);
        Assert.Equal(new CabinetLuaCache.Lookup(CabinetLuaCache.LookupOutcome.NotCached), none);
        var current = await Cache(cabinet, Steam("100")).LookupAsync(App, null, CancellationToken.None);
        Assert.Equal(CabinetLuaCache.LookupOutcome.Current, current.Outcome);
        Assert.NotNull(current.File);
        Assert.Null(current.StaleSha256);
    }

    [Fact]
    public async Task MissingDepotInfoIsUnverifiableNotStale()
    {
        byte[] package = Zip(($"{App}.lua", Lua("100")));
        var cabinet = new FakeCabinet { Meta = MetaFor(package, "100"), Package = package };
        var lookup = await Cache(cabinet, null).LookupAsync(App, null, CancellationToken.None);
        Assert.Equal(CabinetLuaCache.LookupOutcome.Unverifiable, lookup.Outcome);
        Assert.Equal(cabinet.Meta, lookup.Meta);
        Assert.Null(lookup.StaleSha256); // never named as stale: that would spend shared Hubcap quota
        Assert.Null(lookup.File);        // the other sources' cache-first path still skips it
        Assert.Null(await Cache(cabinet, null).TryGetCurrentAsync(App, null, CancellationToken.None));
        // The relay's re-check of a named copy uses the same three answers.
        Assert.Equal(CabinetLuaCache.BuildCheck.Unverifiable, await Cache(cabinet, null).CheckBuildAsync(cabinet.Meta!, CancellationToken.None));
        Assert.Equal(CabinetLuaCache.BuildCheck.Stale, await Cache(cabinet, Steam("111")).CheckBuildAsync(cabinet.Meta!, CancellationToken.None));
        Assert.Equal(CabinetLuaCache.BuildCheck.Current, await Cache(cabinet, Steam("100")).CheckBuildAsync(cabinet.Meta!, CancellationToken.None));
    }

    [Fact]
    public async Task ACurrentCopyThatCantBeDownloadedIsAFailureNotStale()
    {
        byte[] package = Zip(($"{App}.lua", Lua("111")));
        var cabinet = new FakeCabinet { Meta = MetaFor(package, "111"), DownloadFailure = new HttpRequestException("reset") };
        var lookup = await Cache(cabinet, Steam("111")).LookupAsync(App, null, CancellationToken.None);
        Assert.Equal(CabinetLuaCache.LookupOutcome.Failed, lookup.Outcome);
        Assert.Null(lookup.StaleSha256);
        // Bytes that don't match their meta are the server's failure too, not a stale copy.
        cabinet.DownloadFailure = null;
        cabinet.Package = Zip(($"{App}.lua", Lua("111") + "-- other bytes\n"));
        Assert.Equal(CabinetLuaCache.LookupOutcome.Failed, (await Cache(cabinet, Steam("111")).LookupAsync(App, null, CancellationToken.None)).Outcome);
        // A copy evicted between meta and bytes is read again once, then reported as a failure.
        cabinet.Package = null;
        int before = cabinet.MetaCalls;
        Assert.Equal(CabinetLuaCache.LookupOutcome.Failed, (await Cache(cabinet, Steam("111")).LookupAsync(App, null, CancellationToken.None)).Outcome);
        Assert.Equal(before + 2, cabinet.MetaCalls);
    }

    [Fact]
    public async Task LookupForTheRelayIgnoresTheBackoffButReportsServerFailures()
    {
        var cabinet = new FakeCabinet { Failure = new HttpRequestException("down") };
        var cache = Cache(cabinet, Steam("111"));
        Assert.Equal(CabinetLuaCache.LookupOutcome.Failed, (await cache.LookupAsync(App, null, CancellationToken.None)).Outcome);
        Assert.True(cache.BackingOff);
        Assert.Equal(1, cabinet.MetaCalls);
        await cache.LookupAsync(App, null, CancellationToken.None); // backing off: not asked
        Assert.Equal(1, cabinet.MetaCalls);
        cabinet.Failure = null;
        Assert.Equal(new CabinetLuaCache.Lookup(CabinetLuaCache.LookupOutcome.NotCached), await cache.LookupAsync(App, null, CancellationToken.None, ignoreBackoff: true));
        Assert.Equal(2, cabinet.MetaCalls);
    }

    [Fact]
    public async Task ExactDownloadVerifiesTheNamedVersionButNotSteamsBuild()
    {
        byte[] package = Zip(($"{App}.lua", Lua("100")));
        var meta = MetaFor(package, "100");
        var cabinet = new FakeCabinet { Package = package };
        var file = await Cache(cabinet, Steam("111")).DownloadExactAsync(meta, null, CancellationToken.None);
        Assert.IsType<CabinetCachedFile>(file);
        Assert.Equal(package, File.ReadAllBytes(file.FilePath));
        // Other bytes than the meta names, a version no longer kept, or a package a shared copy may not be.
        cabinet.Package = Zip(($"{App}.lua", Lua("101")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Cache(cabinet, null).DownloadExactAsync(meta, null, CancellationToken.None));
        cabinet.Package = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => Cache(cabinet, null).DownloadExactAsync(meta, null, CancellationToken.None));
        byte[] undeclared = Zip(($"{App}.lua", "addappid(440)\n"));
        cabinet.Package = undeclared;
        await Assert.ThrowsAsync<InvalidDataException>(() => Cache(cabinet, null).DownloadExactAsync(MetaFor(undeclared, "1"), null, CancellationToken.None));
    }

    [Fact]
    public async Task RelayTransportSendsTheStaleShaAndParsesStatuses()
    {
        var requests = new List<HttpRequestMessage>();
        string sha = new('d', 64);
        string meta = $"{{\"appid\":\"570\",\"sha256\":\"{sha}\",\"size\":12,\"pins\":{{}},\"source\":\"Hubcap (Cabinet)\",\"uploadedAt\":1,\"uploader\":\"Cabinet Cloud\"}}";
        var transport = new Stub(r =>
        {
            requests.Add(r);
            string path = r.RequestUri!.AbsolutePath;
            if (path == "/api/hubcap") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"enabled\":true}") };
            if (r.Method == HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"state\":\"queued\",\"position\":3}") };
            if (path == "/api/apps/440/hubcap") return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\"}") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"state\":\"ready\",\"meta\":{meta}}}") };
        });
        using var client = new CabinetLuaCacheClient(new CredentialBox(new CabinetCredentials("https://cabinet.example.test", Token, "device1")), transport);
        Assert.True(await client.GetRelayEnabledAsync(CancellationToken.None));
        Assert.Equal(new CabinetRelayStatus("queued", 3), await client.RequestRelayAsync(App, sha, CancellationToken.None));
        await client.RequestRelayAsync(App, null, CancellationToken.None);
        var ready = await client.GetRelayStatusAsync(App, CancellationToken.None);
        Assert.Equal(("ready", sha, "Cabinet Cloud"), (ready!.State, ready.Meta!.Sha256, ready.Meta.Uploader));
        Assert.Null(await client.GetRelayStatusAsync(440, CancellationToken.None));
        Assert.Equal($"/api/apps/570/hubcap?stale={sha}", requests[1].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal("/api/apps/570/hubcap", requests[2].RequestUri!.PathAndQuery);
        foreach (var request in requests)
        {
            Assert.Equal("Bearer " + Token, request.Headers.Authorization!.ToString());
            Assert.Equal("https://cabinet.example.test", request.Headers.GetValues("Origin").Single());
        }
        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestRelayAsync(App, "not-a-sha", CancellationToken.None));
    }

    [Fact]
    public void MalformedRelayStatusesAreRefused()
    {
        string meta = $"{{\"appid\":\"570\",\"sha256\":\"{new string('e', 64)}\",\"size\":1,\"pins\":{{}},\"source\":\"s\",\"uploadedAt\":1,\"uploader\":\"u\"}}";
        CabinetRelayStatus Parse(string json) => CabinetLuaCacheClient.ParseRelayStatus(Encoding.UTF8.GetBytes(json), App);
        Assert.Equal(new CabinetRelayStatus("failed", null, "no_manifest"), Parse("{\"state\":\"failed\",\"error\":\"no_manifest\"}"));
        Assert.Equal("upstream_not_newer", Parse($"{{\"state\":\"upstream_not_newer\",\"meta\":{meta}}}").State);
        Assert.True(Parse("{\"state\":\"running\"}").IsPending);
        foreach (string bad in new[] { "{\"state\":\"done\"}", "{\"state\":\"ready\"}", "{\"state\":\"queued\",\"position\":0}",
                     "{\"state\":\"failed\",\"error\":\"<b>x</b>\"}", $"{{\"state\":\"ready\",\"meta\":{meta.Replace("\"570\"", "\"440\"")}}}", "[]", "{" })
            Assert.Throws<InvalidDataException>(() => Parse(bad));
    }
}
