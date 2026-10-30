using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using LuaTools.Agent;
using LuaToolsCli;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Agent;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class AgentAcquisitionTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "agent-acquisition-" + Guid.NewGuid().ToString("N"));
        internal readonly SettingsService Settings;
        internal readonly SteamService Steam;
        internal readonly LuaInstaller Installer;
        internal readonly LuaOverwriteCoordinator Diff;
        internal readonly LocalInstallCoordinator Local;
        internal readonly SteamAppListCache AppList;
        internal readonly SteamAppInfoCache AppInfo;
        internal readonly SteamDepotInfo Depots;
        internal readonly DownloadQueue Queue;
        internal readonly AgentCommandService Facade;
        internal readonly AcquisitionCoordinator Sources;
        internal readonly HttpClient Http;
        internal readonly byte[] Capability = RandomNumberGenerator.GetBytes(32);
        internal int Spent;
        internal bool FailHubcap;
        internal Fixture(bool key = true)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, "details"));
            File.WriteAllText(Path.Combine(Root, "steam.exe"), "fixture");
            File.WriteAllText(Path.Combine(Root, "details", "730.json"), "{\"type\":\"game\",\"name\":\"Fixture game\",\"steam_appid\":730}");
            Settings = new(new AppSettings { SteamPathOverride = Root });
            if (key) Settings.HubcapApiKey = "smm_" + new string('a', 96);
            Steam = new(Settings);
            var cache = new CacheService(Path.Combine(Root, "cache.json"));
            var transport = new Stub(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.Contains("/status/")) return Json("{\"status\":\"success\",\"manifest_file_exists\":true}");
                if (path.EndsWith("/stats")) return FailHubcap ? new(HttpStatusCode.ServiceUnavailable) : Json("{\"daily_usage\":0,\"daily_limit\":25,\"can_make_requests\":true}");
                if (path.Contains("/manifest/"))
                {
                    Interlocked.Increment(ref Spent);
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("addappid(730)\n") };
                    response.Content.Headers.ContentDisposition = new("attachment") { FileName = "Harvest Moon: Fixture.lua" };
                    return response;
                }
                if (request.RequestUri.Host == new Uri(AppConfig.DexLuaBaseUrl).Host) return new(HttpStatusCode.OK) { Content = new StringContent("addappid(730)\n") };
                if (request.RequestUri.Host == new Uri(AppConfig.SteamMlCdnUrl).Host) return new(HttpStatusCode.NotFound);
                throw new InvalidOperationException("Unexpected synthetic provider route");
            });
            Http = new(transport) { BaseAddress = new Uri(AppConfig.ApiBaseUrl) };
            var metadata = new Stub(_ => new(HttpStatusCode.NotFound));
            var covers = new CoverCache(metadata, Path.Combine(Root, "covers"));
            AppInfo = new(cache, NullLogger<SteamAppInfoCache>.Instance, metadata, Path.Combine(Root, "details"));
            Depots = new(metadata);
            AppList = new(new Stub(_ => Json("[]")), Path.Combine(Root, "applist.json"));
            File.WriteAllText(Path.Combine(Root, "applist.json"), "{\"730\":\"Fixture game\",\"731\":\"Old depot\",\"732\":\"New depot\"}");
            var auth = new AuthService(Http, Path.Combine(Root, "auth.dat"));
            var api = new LuaToolsApiClient(auth, AppInfo, covers, Http, steamMl: new SteamMlService(Http), depots: Depots, dexLua: new DexLuaService(Http));
            var hubcap = new HubcapService(Http);
            Installer = new(Steam, Settings, cache, new LuaVault(() => Steam.StPlugInDir, Path.Combine(Root, "vault")));
            Diff = new(Installer, AppList, AppInfo, Depots);
            Local = new(Installer, Diff);
            Queue = new(cache, NullLogger<DownloadQueue>.Instance);
            var factory = new ManifestJobFactory(api, hubcap, Settings, Installer, new(Steam), covers, null!, null!, Depots, null!, null!, null!);
            Sources = new(api, hubcap, Settings, auth, Queue, factory, Installer);
            var github = new GithubProxy();
            Facade = new(Settings, Steam, auth, Queue, new(Steam, Settings, cache, github), new(Steam, Settings, github, null!), new(),
                mutations: new(Path.Combine(Root, "journal.json")), acquisition: new(Sources, Diff, Local, Installer, api, new(api, Depots, AppInfo, Installer), factory));
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        internal AgentRequest Request(string command = "game add", string[]? args = null, Dictionary<string, string?>? flags = null) => new(1, Convert.ToBase64String(Capability), Facade.InstanceId, Guid.NewGuid().ToString("N"), command, args ?? ["730"], flags ?? new() { ["source"] = AcquisitionCoordinator.HubcapSource }, Facade.SteamIdentity);
        internal async Task<Envelope?> Pipe(AgentRequest request, byte[][]? files = null, bool loseAcknowledgement = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var service = new AgentPipeHostedService(Facade, NullLogger<AgentPipeHostedService>.Instance, Capability);
            string name = "acquisition-test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var host = Task.Run(async () => { await server.WaitForConnectionAsync(timeout.Token); await service.ServeAsync(server, timeout.Token); });
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            await Protocol.WriteAsync(client, request, Protocol.MaxRequest, timeout.Token);
            await AgentClient.UploadAsync(client, files ?? [], timeout.Token);
            Envelope? result = loseAcknowledgement ? null : await Protocol.ReadAsync<Envelope>(client, Protocol.MaxFrame, timeout.Token);
            await host;
            return result;
        }
        internal string CreateFile(string name, string content)
        { string path = Path.Combine(Root, name); System.IO.File.WriteAllText(path, content); return path; }
        internal void Live(string text)
        { Directory.CreateDirectory(Steam.StPlugInDir!); System.IO.File.WriteAllText(Path.Combine(Steam.StPlugInDir!, "730.lua"), text); }
        public void Dispose() { Http.Dispose(); Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task RealPipePlansAndConsentPrecedeQuotaAndQueueAndJournalRecoversLostAcknowledgement()
    {
        using var f = new Fixture();
        var dry = await f.Pipe(f.Request(flags: new() { ["source"] = AcquisitionCoordinator.HubcapSource, ["dry-run"] = null }));
        Assert.True(dry!.Ok); Assert.Equal(0, f.Spent); Assert.Empty(f.Queue.Items);
        var no = await f.Pipe(f.Request());
        Assert.Equal(8, no!.Error!.ExitCode); Assert.Equal(0, f.Spent); Assert.Empty(f.Queue.Items);
        var request = f.Request(flags: new() { ["source"] = AcquisitionCoordinator.HubcapSource, ["yes"] = null, ["idempotency-key"] = "lost-ack" });
        await f.Pipe(request, loseAcknowledgement: true);
        var item = Assert.Single(f.Queue.Items);
        var file = await item.Job.DownloadAsync(item, new Progress<DownloadProgress>(), default);
        Assert.Equal(1, f.Spent); Assert.DoesNotContain(':', Path.GetFileName(file.FilePath));
        Assert.True(await item.Job.ConfirmAsync!(file, item, default));
        Assert.True((await item.Job.InstallAsync(file, item, default)).Ok);
        item.Status = DownloadStatus.Completed;
        var recovered = await f.Pipe(request with { RequestId = Guid.NewGuid().ToString("N") });
        Assert.True(recovered!.Ok); Assert.Equal(item.Id, recovered.Data!["id"]!.ToString()); Assert.Single(f.Queue.Items); Assert.Equal(1, f.Spent);
        Assert.DoesNotContain("smm_", System.IO.File.ReadAllText(Path.Combine(f.Root, "journal.json")));
    }
    [Fact]
    public async Task RealPipeReportsMissingKeyFailedHubcapAndGuestPublicSource()
    {
        using var f = new Fixture(key: false);
        var sources = await f.Pipe(f.Request("source list", [], new() { ["appid"] = "730" }));
        Assert.True(sources!.Ok); Assert.Contains("HUBCAP_KEY_REQUIRED", sources.Data!.ToJsonString());
        var missing = await f.Pipe(f.Request(flags: new() { ["source"] = AcquisitionCoordinator.HubcapSource, ["yes"] = null }));
        Assert.Equal(4, missing!.Error!.ExitCode); Assert.Equal(0, f.Spent);
        f.Settings.HubcapApiKey = "smm_" + new string('a', 96); f.FailHubcap = true;
        sources = await f.Pipe(f.Request("source list", [], new() { ["appid"] = "730" }));
        Assert.Contains("HUBCAP_STATS_UNAVAILABLE", sources!.Data!.ToJsonString());
        var accepted = await f.Pipe(f.Request(flags: new() { ["source"] = DexLuaService.SourceName, ["yes"] = null }));
        Assert.True(accepted!.Ok); var item = Assert.Single(f.Queue.Items);
        var file = await item.Job.DownloadAsync(item, new Progress<DownloadProgress>(), default);
        Assert.True((await item.Job.InstallAsync(file, item, default)).Ok); Assert.Equal(0, f.Spent);
    }
    [Fact]
    public async Task RealPipeRefusesReadOnlyVersionAndStaleTargetsBeforeAcquisition()
    {
        using var f = new Fixture();
        var readOnly = await f.Pipe(f.Request(flags: new() { ["source"] = "auto", ["read-only"] = null, ["yes"] = null }));
        Assert.Equal("READ_ONLY", readOnly!.Error!.Code);
        var version = await f.Pipe(f.Request(flags: new() { ["source"] = "auto", ["if-version"] = "old", ["yes"] = null }));
        Assert.Equal("VERSION_CONFLICT", version!.Error!.Code);
        var plan = await f.Pipe(f.Request(flags: new() { ["source"] = "auto", ["dry-run"] = null }));
        f.Live("addappid(730)\n");
        var stale = await f.Pipe(f.Request(flags: new() { ["source"] = "auto", ["plan-id"] = plan!.Data!["plan"]!["id"]!.ToString(), ["yes"] = null }));
        Assert.Equal("PLAN_STALE", stale!.Error!.Code); Assert.Equal(0, f.Spent); Assert.Empty(f.Queue.Items);
    }
    [Fact]
    public async Task RealPipeUploadsOverwriteDiffWithoutExposingKeysAndInstallsLocalBatch()
    {
        using var f = new Fixture(); f.Live("addappid(730)\naddappid(731,1,\"" + new string('a', 64) + "\")\n");
        byte[] bytes = Encoding.UTF8.GetBytes("addappid(730)\naddappid(732)\n");
        var descriptor = new SubmittedFile("730.lua", bytes.Length, FileSubmission.Digest(bytes));
        var get = f.Request("lua get", flags: new() { ["diff-file"] = descriptor.Sha256 }) with { Files = [descriptor] };
        var diff = await f.Pipe(get, [bytes]);
        Assert.True(diff!.Ok); Assert.Equal(1, diff.Data!["diff"]!["added"]!.AsArray().Count); Assert.Equal(1, diff.Data!["diff"]!["removed"]!.AsArray().Count);
        Assert.DoesNotContain(new string('a', 64), diff.Data.ToJsonString());
        var add = f.Request("lua add", [], new() { ["file"] = descriptor.Sha256, ["dry-run"] = null }) with { Files = [descriptor] };
        Assert.True((await f.Pipe(add, [bytes]))!.Ok); Assert.Contains("731", System.IO.File.ReadAllText(f.Installer.ReadInstalledLua(730)!));
        add.Flags.Remove("dry-run");
        Assert.Equal(8, (await f.Pipe(add, [bytes]))!.Error!.ExitCode);
        add.Flags["yes"] = null;
        var accepted = await f.Pipe(add, [bytes]); Assert.True(accepted!.Ok);
        // Observe the registered service operation through the facade instead of assuming acceptance is completion.
        Envelope? job = null;
        for (int i = 0; i < 100; i++)
        {
            job = await f.Pipe(f.Request("job get", [accepted.Data!["id"]!.ToString()], []));
            if (job!.Data!["state"]!.ToString() != "running") break;
            await Task.Delay(10);
        }
        Assert.Equal("completed", job!.Data!["state"]!.ToString());
        Assert.Contains("732", System.IO.File.ReadAllText(f.Installer.ReadInstalledLua(730)!));
    }
    [Fact]
    public async Task GuiDropBatchesSerializeDecisionsSkipOnlyOneFileAndPreserveLaterDrop()
    {
        using var f = new Fixture(); f.Live("addappid(730)\naddappid(731)\n");
        string replacement = f.CreateFile("730.lua", "addappid(730)\naddappid(732)\n");
        string fresh = f.CreateFile("740.lua", "addappid(740)\n");
        var vm = new DropInstallViewModel(f.Installer, f.AppList, f.AppInfo, f.Depots, f.Local);
        var first = vm.HandleDropAsync([replacement]);
        for (int i = 0; i < 100 && !vm.IsConfirming; i++) await Task.Delay(10);
        Assert.True(vm.IsConfirming); Assert.Single(vm.Added); Assert.Single(vm.Removed);
        var second = vm.HandleDropAsync([fresh]); Assert.False(second.IsCompleted);
        vm.CancelOverwriteCommand.Execute(null);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("731", System.IO.File.ReadAllText(f.Installer.ReadInstalledLua(730)!));
        Assert.NotNull(f.Installer.ReadInstalledLua(740));
    }
    [Fact]
    public async Task QueuedSourceRechecksRevokedKeyBeforeSpendingQuota()
    {
        using var f = new Fixture();
        Assert.True((await f.Pipe(f.Request(flags: new() { ["source"] = AcquisitionCoordinator.HubcapSource, ["yes"] = null })))!.Ok);
        f.Settings.HubcapApiKey = null;
        var item = Assert.Single(f.Queue.Items);
        await Assert.ThrowsAsync<DownloadAbortedException>(() => item.Job.DownloadAsync(item, new Progress<DownloadProgress>(), default));
        Assert.Equal(0, f.Spent);
    }
}
