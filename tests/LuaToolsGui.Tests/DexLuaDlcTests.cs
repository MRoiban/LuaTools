using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

public class DexLuaDlcTests
{
    private const string Metadata = """
        {"data":{"1000":{"extended":{"listofdlc":"2000,4000"},"depots":{
          "1001":{"dlcappid":"2000","manifests":{"public":{"gid":"555","size":"10"}}},
          "1002":{"dlcappid":"2000"},"1003":{}
        }}}}
        """;
    private static readonly string Key = new('a', 64);
    private static readonly string Script = $"addappid(1000, 1, \"Base game\")\naddappid(1001,0,\"{Key}\")\n--addappid(1002,0,\"{Key}\")\naddappid(1003,0,\"{Key}\")\n";

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(reply(request));
        }
    }
    private static HttpResponseMessage Reply(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class Fixture : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "DexLuaDlcTests-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient standard;
        private readonly HttpClient dexLua;
        public readonly AuthService Auth;
        public readonly LuaToolsApiClient Api;
        public int StandardCalls, DexLuaCalls;
        public bool StandardLimited = true;
        public string Lua = Script;
        public HttpStatusCode DexLuaStatus = HttpStatusCode.OK;
        public Fixture()
        {
            standard = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(r =>
            {
                if (r.RequestUri!.AbsolutePath == "/api/auth/code/redeem") return Reply("{\"token\":\"test-token\"}");
                if (r.RequestUri.AbsolutePath == "/auth/v1/verify")
                    return Reply("{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":3600,\"user\":{\"id\":\"db101b52-9f32-4663-8896-bdc8fd58b651\"}}");
                StandardCalls++;
                return StandardLimited
                    ? Reply("{\"error\":\"Daily limit reached\"}", HttpStatusCode.TooManyRequests)
                    : Reply("{\"appid\":\"1000\",\"depotCount\":2,\"haveCount\":2,\"missingCount\":0,\"depots\":[]}");
            }));
            Auth = new AuthService(standard, Path.Combine(dir, "auth.dat"));
            dexLua = AppHttp.CreateExternalDataForTesting(TimeSpan.FromSeconds(5), new Uri(AppConfig.DexLuaBaseUrl), new Stub(r =>
            {
                Assert.Equal(AppConfig.DexLuaBaseUrl + "api/download?id=1000", r.RequestUri!.AbsoluteUri);
                Assert.Null(r.Headers.Authorization);
                Assert.False(r.Headers.Contains("apikey"));
                DexLuaCalls++;
                return Reply(Lua, DexLuaStatus);
            }));
            Api = new LuaToolsApiClient(Auth, null!, null!, standard,
                depots: new SteamDepotInfo(new Stub(_ => Reply(Metadata))), dexLua: new DexLuaService(dexLua));
        }
        public Task SignInAsync() => Auth.SignInWithCodeAsync("abc123");
        public PluginAddService Plugin() => new(Api, null!, new SettingsService(new AppSettings()), Auth, null!, null!);
        public ManifestJobFactory Jobs()
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "steam.exe"), "inert fixture");
            var settings = new SettingsService(new AppSettings { SteamPathOverride = dir });
            var steam = new SteamService(settings);
            var installer = new LuaInstaller(steam, settings, new CacheService(Path.Combine(dir, "cache.json")), new LuaVault(() => steam.StPlugInDir, Path.Combine(dir, "vault")));
            return new(Api, null!, settings, installer, null!,
                new CoverCache(new Stub(_ => throw new Exception("Unexpected cover request")), Path.Combine(dir, "covers")),
                null!, null!, null!, null!, null!, null!);
        }
        public void Dispose()
        {
            standard.Dispose(); dexLua.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DexLuaDlcJobNeverUsesStandardQuotaAndIncludesOnlyActiveDlcKeys(bool signedIn)
    {
        using var f = new Fixture();
        if (signedIn) await f.SignInAsync();
        var info = await f.Api.GetDexLuaDlcInfoAsync("2000", "1000");
        Assert.Equal((2, 1, 1), (info.DepotCount, info.HaveCount, info.MissingCount));
        var job = f.Jobs().CreateDlcJob(2000, "1000", "DLC", sourceName: DexLuaService.SourceName);
        Assert.Equal(DexLuaService.SourceName, job.SubTitle);
        var file = await job.DownloadAsync(null!, new Progress<DownloadProgress>(), default);
        try
        {
            Assert.Equal("2000.lua", file.FileName);
            ManifestContentPolicy.ValidateLuaFile(file.FilePath, 2000);
            string text = await File.ReadAllTextAsync(file.FilePath);
            Assert.Contains("addappid(2000)", text);
            Assert.Contains($"addappid(1001,0,\"{Key}\")", text);
            Assert.DoesNotContain("1002", text); // disabled key
            Assert.DoesNotContain("1003", text); // base-game depot
            Assert.DoesNotContain("setManifestid", text); // public metadata is not a pin
            Assert.Equal(0, f.StandardCalls);
        }
        finally { ManifestJobFactory.DeleteStaged(file.FilePath); }
    }

    [Fact]
    public async Task BatchSnapshotReusesValidatedKeysAcrossCoverageAndGeneration()
    {
        using var f = new Fixture();
        await f.SignInAsync();
        var snapshot = await f.Api.GetDexLuaDlcSnapshotAsync("1000");
        Assert.Equal(1, snapshot.GetInfo("2000").HaveCount);
        Assert.Equal(0, snapshot.GetInfo("4000").DepotCount);
        var file = await snapshot.GenerateAsync("2000", null);
        try { ManifestContentPolicy.ValidateLuaFile(file.FilePath, 2000); }
        finally { ManifestJobFactory.DeleteStaged(file.FilePath); }
        Assert.Equal(1, f.DexLuaCalls);
        Assert.Equal(0, f.StandardCalls);
    }

    [Fact]
    public async Task FastFetchPrefersDexLuaWithoutCheckingQuotaLimitedSource()
    {
        using var f = new Fixture();
        await f.SignInAsync();
        var rows = await f.Plugin().CheckDlcSourcesAsync("2000", "1000", fastFetch: true);
        var row = Assert.Single(rows);
        Assert.Equal(DexLuaService.SourceName, row.Name);
        Assert.True(row.CanDownload);
        Assert.Null(row.Stats);
        Assert.Equal(0, f.StandardCalls);
    }

    [Fact]
    public async Task StandardLimitCannotHideDexLuaInPicker()
    {
        using var f = new Fixture();
        await f.SignInAsync();
        var rows = await f.Plugin().CheckDlcSourcesAsync("2000", "1000", fastFetch: false);
        var row = Assert.Single(rows.Where(r => r.Name == DexLuaService.SourceName));
        Assert.True(row.CanDownload);
        Assert.Null(row.Stats);
        Assert.False(Assert.Single(rows.Where(r => r.Name == PluginAddService.DlcSourceName)).CanDownload);
        Assert.Equal(1, f.StandardCalls);
    }

    [Fact]
    public async Task DexLuaFailureLeavesLegacySourceAvailable()
    {
        using var f = new Fixture { DexLuaStatus = HttpStatusCode.NotFound, StandardLimited = false };
        await f.SignInAsync();
        var rows = await f.Plugin().CheckDlcSourcesAsync("2000", "1000", fastFetch: true);
        Assert.False(rows.Single(r => r.Name == DexLuaService.SourceName).CanDownload);
        Assert.True(rows.Single(r => r.Name == PluginAddService.DlcSourceName).CanDownload);
        Assert.Equal(1, f.StandardCalls);
    }

    [Fact]
    public async Task SelectedDexLuaFailureNeverFallsBackToQuotaLimitedGeneration()
    {
        using var f = new Fixture { DexLuaStatus = HttpStatusCode.NotFound };
        await f.SignInAsync();
        var job = f.Jobs().CreateDlcJob(2000, "1000", "DLC", sourceName: DexLuaService.SourceName);
        await Assert.ThrowsAsync<ApiException>(() => job.DownloadAsync(null!, new Progress<DownloadProgress>(), default));
        Assert.Equal(0, f.StandardCalls);
    }

    [Fact]
    public async Task UncoveredDlcCannotProduceMisleadingSuccessfulLua()
    {
        using var f = new Fixture { Lua = "addappid(1000)" };
        var info = await f.Api.GetDexLuaDlcInfoAsync("2000", "1000");
        Assert.Equal(0, info.HaveCount);
        Assert.Equal(2, info.MissingCount);
        await Assert.ThrowsAsync<ApiException>(() => f.Api.GenerateDexLuaDlcAsync("2000", "1000", null));
        Assert.Equal(0, f.StandardCalls);
    }

    [Fact]
    public async Task KnownDepotlessDlcProducesOnlyItsEntitlement()
    {
        using var f = new Fixture();
        var file = await f.Api.GenerateDexLuaDlcAsync("4000", "1000", null);
        try { Assert.Equal("addappid(4000)\n", await File.ReadAllTextAsync(file.FilePath)); }
        finally { ManifestJobFactory.DeleteStaged(file.FilePath); }
        Assert.Equal(0, f.StandardCalls);
    }

    [Fact]
    public async Task WrongParentDoesNotLookLikeADepotlessDlc()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<ApiException>(() => f.Api.GenerateDexLuaDlcAsync("9999", "1000", null));
        Assert.Equal(0, f.DexLuaCalls);
        Assert.Equal(0, f.StandardCalls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("../2000")]
    public async Task InvalidIdsDoNotReachProvider(string id)
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Api.GenerateDexLuaDlcAsync(id, "1000", null));
        Assert.Equal(0, f.DexLuaCalls);
        Assert.Equal(0, f.StandardCalls);
    }
}
