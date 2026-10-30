using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuaTools.Agent;
using LuaToolsCli;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Agent;
using LuaToolsGui.Services.AppInfo;
using LuaToolsGui.Services.Cabinet;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace LuaToolsGui.Tests;

public sealed class AgentFinishTests
{
    private sealed class Network : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            if (r.RequestUri!.Host == "api.steamcmd.net") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"success\",\"data\":{\"570\":{\"depots\":{\"571\":{\"config\":{\"oslist\":\"windows\"},\"manifests\":{\"public\":{\"gid\":\"111\",\"size\":\"1\"}}}}}}}") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    private sealed class Cabinet : ICabinetService
    {
        public CabinetStatus Status { get; set; } = CabinetStatus.Off with { Enabled = true, Cloud = new(true, "fixture", "example.test", null) };
        public event EventHandler<CabinetStatus>? StatusChanged { add { } remove { } }
        public event EventHandler<CabinetNotification>? Notification { add { } remove { } }
        public void SettingsChanged() { }
        public Task SyncNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Modes(UnlockerService unlocker, SteamService steam, CloudRedirectService cloud) : ModeCoordinator(unlocker, steam, cloud)
    {
        public int Enables;
        public override Task<LuaToolsGui.Models.ModeInstallResult> EnrollCloudSavesAsync(CancellationToken ct = default, Action? revalidate = null)
        {
            revalidate?.Invoke(); Enables++;
            return Task.FromResult(LuaToolsGui.Models.ModeInstallResult.Ok());
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "finish-" + Guid.NewGuid().ToString("N"));
        internal readonly SettingsService Settings;
        internal readonly SteamService Steam;
        internal readonly LuaVault Vault;
        internal readonly LocalInstallCoordinator Local;
        internal readonly AgentCommandService Facade;
        internal readonly LaunchOptionsService Launch;
        internal readonly DownloadQueue Queue;
        internal readonly Network Net = new();
        internal readonly Modes Mode;
        internal readonly DepotDownloaderService Tool;
        internal readonly Cabinet Cab = new();
        internal readonly BuildsViewModel Builds;
        internal int Stops;
        internal bool Running;
        private readonly byte[] _capability = RandomNumberGenerator.GetBytes(32);
        private readonly HttpClient _http;
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "steam", "appcache"));
            File.WriteAllText(Path.Combine(Root, "steam", "steam.exe"), "inert fixture");
            Settings = new(new AppSettings { SteamPathOverride = Path.Combine(Root, "steam"), SelectedMode = "Ost" });
            Steam = new(Settings); Directory.CreateDirectory(Steam.StPlugInDir!);
            Vault = new(() => Steam.StPlugInDir, Path.Combine(Root, "vault"));
            var cache = new CacheService(Path.Combine(Root, "cache.json"));
            var names = new SteamAppListCache(Net, Path.Combine(Root, "names.json"));
            File.WriteAllText(Path.Combine(Root, "names.json"), "{\"570\":\"Alpha\",\"571\":\"Content\",\"572\":\"Beta\"}");
            Directory.CreateDirectory(Path.Combine(Root, "details"));
            File.WriteAllText(Path.Combine(Root, "details", "570.json"), "{\"steam_appid\":570,\"type\":\"game\",\"name\":\"Alpha\"}");
            var details = new SteamAppInfoCache(cache, NullLogger<SteamAppInfoCache>.Instance, Net, Path.Combine(Root, "details"));
            var covers = new CoverCache(Net, Path.Combine(Root, "covers"));
            var depots = new SteamDepotInfo(Net);
            _http = new(Net) { BaseAddress = new Uri(AppConfig.ApiBaseUrl) };
            var auth = new AuthService(_http, Path.Combine(Root, "auth"));
            var github = new GithubProxy();
            var unlocker = new UnlockerService(Steam, Settings, cache, github);
            Mode = new(unlocker, Steam, new(github, Settings));
            var installer = new LuaInstaller(Steam, Settings, cache, Vault);
            var diff = new LuaOverwriteCoordinator(installer, names, details, depots);
            Local = new(installer, diff);
            Queue = new(cache, NullLogger<DownloadQueue>.Instance);
            var api = new LuaToolsApiClient(auth, details, covers, _http);
            var hubcap = new HubcapService(_http);
            var tool = Tool = new DepotDownloaderService(github, Steam, auth, cache, NullLogger<DepotDownloaderService>.Instance, Settings);
            var factory = new ManifestJobFactory(api, hubcap, Settings, installer, new(Steam), covers, null!, tool, depots, null!, null!, null!);
            Builds = new(Steam, Vault, names, details, covers, depots, new ToastService(), Settings, tool, Queue, factory);
            string appinfo = Path.Combine(Root, "steam", "appcache", "appinfo.vdf");
            LaunchOptionsServiceTests.WriteAppInfo(appinfo, [("0", "Original.exe", "windows")]);
            Launch = new(() => appinfo, () => Running, () => Stops++, () => { }, new LaunchModStore(Path.Combine(Root, "launchmods.json")), Path.Combine(Root, "backups"));
            Facade = new(Settings, Steam, auth, Queue, unlocker, new(Steam, Settings, github, null!), new(),
                mutations: new(Path.Combine(Root, "journal.json")), vault: Vault, launch: Launch, depotInfo: depots, appList: names, appInfo: details, depotTool: tool, factory: factory,
                libraryNames: names, libraryDetails: details, libraryCache: cache, cabinet: Cab, modes: Mode,
                acquisition: new(new(api, hubcap, Settings, auth, Queue, factory, installer), diff, Local, installer, api, new(api, depots, details, installer), factory));
        }
        internal void Live(string text) => File.WriteAllText(Path.Combine(Steam.StPlugInDir!, "570.lua"), text);
        internal Task<Envelope> Call(string command, string[]? args = null, Dictionary<string, string?>? flags = null, string? input = null, string name = "input.json") => Pipe(new(1, Convert.ToBase64String(_capability), Facade.InstanceId, Guid.NewGuid().ToString("N"), command, args ?? [], flags ?? [], Facade.SteamIdentity), input, name);
        private async Task<Envelope> Pipe(AgentRequest request, string? input, string name)
        {
            byte[]? bytes = input is null ? null : Encoding.UTF8.GetBytes(input);
            if (bytes is not null) { request = request with { Files = [new(name, bytes.Length, FileSubmission.Digest(bytes))] }; request.Flags["file"] = request.Files[0].Sha256; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var service = new AgentPipeHostedService(Facade, NullLogger<AgentPipeHostedService>.Instance, _capability);
            string pipe = "finish-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var host = Task.Run(async () => { await server.WaitForConnectionAsync(timeout.Token); await service.ServeAsync(server, timeout.Token); });
            await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token); await Protocol.WriteAsync(client, request, Protocol.MaxRequest, timeout.Token);
            await AgentClient.UploadAsync(client, bytes is null ? [] : [bytes], timeout.Token);
            var reply = await Protocol.ReadAsync<Envelope>(client, Protocol.MaxFrame, timeout.Token);
            await host; return reply!;
        }
        internal async Task<Envelope> Result(Envelope accepted)
        {
            Assert.True(accepted.Ok, accepted.Error?.Code); string id = accepted.Data!["job_id"]!.ToString();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var result = await Call("job get", [id]);
                if (result.Data!["state"]!.ToString() is "completed" or "failed" or "cancelled") return result;
                await Task.Delay(10, timeout.Token);
            }
        }
        public void Dispose() { _http.Dispose(); Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task LuaAndPresetFilesUseBoundedPipeInputAndPreserveOriginsWithAdmissionChecks()
    {
        using var f = new Fixture(); f.Live("addappid(570)\n"); f.Vault.SyncDefaultFromLive(570);
        string text = "addappid(570)\n-- preset\n";
        var created = await f.Call("preset create", flags: new() { ["appid"] = "570" }, input: text, name: "570.lua");
        Assert.True(created.Ok, created.Error?.Code); string hash = created.Data!["hash"]!.ToString();
        Assert.True(f.Vault.Apply(570, f.Vault.GetVariants(570).Single(v => v.Hash == hash)));
        var dry = await f.Call("lua update", ["570"], new() { ["dry-run"] = null }, "addappid(570)\n-- edited\n", "570.lua");
        Assert.True(dry.Ok); Assert.Contains("live:origin:", dry.Data!.ToJsonString()); Assert.Equal(hash, f.Vault.GetActiveHash(570));
        Assert.Equal("CONSENT_REQUIRED", (await f.Call("lua update", ["570"], input: text, name: "570.lua")).Error!.Code);
        Assert.Equal("READ_ONLY", (await f.Call("lua update", ["570"], new() { ["read-only"] = null }, text, "570.lua")).Error!.Code);
        Assert.Equal("VERSION_CONFLICT", (await f.Call("lua update", ["570"], new() { ["yes"] = null, ["if-version"] = "old" }, text, "570.lua")).Error!.Code);
        var flags = new Dictionary<string, string?> { ["yes"] = null, ["idempotency-key"] = "edit-once" };
        Assert.True((await f.Call("lua update", ["570"], flags, "addappid(570)\n-- edited\n", "570.lua")).Ok);
        Assert.DoesNotContain(f.Vault.GetVariants(570), v => v.Hash == hash); Assert.Null(f.Vault.GetEditBase(570));
        Assert.True((await f.Call("lua update", ["570"], flags, "addappid(570)\n-- edited\n", "570.lua")).Ok);
        var current = f.Vault.GetActiveVariant(570)!; f.Vault.SetEditBase(570, current.Hash); f.Live("addappid(570)\n-- pending\n");
        var save = await f.Call("preset update", [current.Hash], new() { ["appid"] = "570", ["from-live"] = null, ["yes"] = null });
        Assert.True(save.Ok); Assert.Null(f.Vault.GetEditBase(570));
        var fromLive = await f.Call("preset create", flags: new() { ["appid"] = "570", ["from-live"] = null });
        Assert.True(fromLive.Ok); Assert.Equal(save.Data!["hash"]!.ToString(), fromLive.Data!["hash"]!.ToString());
    }

    [Fact]
    public async Task LaunchFileCrudStageApplyRequiresRestartAndRetainsRecoverySnapshot()
    {
        using var f = new Fixture();
        var original = f.Launch.Inspect(603750)!.Current[0].Executable;
        Assert.True((await f.Call("launch-option get", ["603750"])).Ok); Assert.True(f.Launch.Store.IsEmpty);
        var created = await f.Call("launch-option create", ["603750"], input: "{\"executable\":\" Added.exe \",\"description\":\"Added\"}");
        Assert.Equal("completed", (await f.Result(created)).Data!["state"]!.ToString());
        Assert.Equal(2, f.Launch.Store.Get(603750)!.Desired.Count); Assert.Equal(original, f.Launch.Store.Get(603750)!.Original[0].Executable);
        Assert.Equal("INVALID_DATA", (await f.Call("launch-option update", ["603750"], new() { ["entry"] = "0" }, "{\"unknown\":\"bad\"}")).Error!.Code);
        f.Running = true;
        var flags = new Dictionary<string, string?> { ["apply"] = "true", ["yes"] = null };
        Assert.Equal("STEAM_RUNNING", (await f.Call("launch-option update", ["603750"], flags, "[{\"source_entry\":1},{\"source_entry\":0}]")).Error!.Code); Assert.Equal(0, f.Stops);
        flags["restart-steam"] = null;
        var applied = await f.Result(await f.Call("launch-option update", ["603750"], flags, "[{\"source_entry\":1},{\"source_entry\":0}]"));
        Assert.Equal("completed", applied.Data!["state"]!.ToString()); Assert.Equal(1, f.Stops);
        Assert.Equal("Added.exe", f.Launch.Inspect(603750)!.Current[0].Executable);
        var deleted = await f.Result(await f.Call("launch-option delete", ["603750"], new() { ["entry"] = "1" }));
        Assert.Equal("completed", deleted.Data!["state"]!.ToString()); Assert.Single(f.Launch.Store.Get(603750)!.Desired);
        Assert.Equal(original, f.Launch.Store.Get(603750)!.Original[0].Executable);
    }

    [Fact]
    public async Task DepotGamesPagingPlansGetReconcileAndRealQueueIdNeverLeakKeys()
    {
        using var f = new Fixture(); string key = new('a', 64);
        f.Live($"addappid(570)\naddappid(571,0,\"{key}\")\nsetManifestid(571,\"111\",1)\naddappid(572)\nsetManifestid(572,\"222\",50)\n");
        File.WriteAllText(Path.Combine(f.Steam.StPlugInDir!, "572_22.lua"), "addappid(572)\n");
        var first = await f.Call("depot list", flags: new() { ["games"] = null, ["limit"] = "1" });
        Assert.True(first.Ok); Assert.Single(first.Data!.AsArray()); Assert.NotNull(first.Page!.NextCursor);
        var second = await f.Call("depot list", flags: new() { ["games"] = null, ["limit"] = "1", ["cursor"] = first.Page.NextCursor }); Assert.True(second.Ok);
        Assert.Empty(f.Vault.GetVariants(572));
        var depot = await f.Call("depot get", ["571"], new() { ["appid"] = "570" }); Assert.True(depot.Ok); Assert.DoesNotContain(key, JsonSerializer.Serialize(depot));
        var plan = await f.Call("depot plan", ["570"], new() { ["depot"] = "571", ["read-only"] = null }); Assert.True(plan.Ok, plan.Error?.Code); Assert.Empty(f.Queue.Items);
        var all = await f.Call("depot plan", ["570"], new() { ["all-depots"] = null }); Assert.True(all.Ok);
        Assert.Single(all.Data!["plan"]!["targets"]!.AsArray()); Assert.Contains("572", all.Data["plan"]!["impact"]!.ToJsonString());
        string output = Path.Combine(f.Root, "output");
        var flags = new Dictionary<string, string?> { ["depot"] = "571", ["output"] = output, ["yes"] = null };
        Assert.Equal("WORKFLOW_DISABLED", (await f.Call("depot download", ["570"], flags)).Error!.Code); Assert.False(Directory.Exists(output));
        f.Settings.DepotDownloaderEnabled = true;
        var accepted = await f.Call("depot download", ["570"], flags); Assert.True(accepted.Ok, accepted.Error?.Code);
        Assert.Equal(Assert.Single(f.Queue.Items).Id, accepted.Data!["job_id"]!.ToString());
        var queued = Assert.Single(f.Queue.Items); queued.Status = DownloadStatus.Downloading;
        Assert.True((await f.Call("job pause", [queued.Id])).Ok); Assert.Equal(DownloadStatus.Paused, queued.Status);
        Assert.True((await f.Call("job resume", [queued.Id])).Ok); Assert.True(queued.NeedsValidate); Assert.Equal(DownloadStatus.Queued, queued.Status);
        Assert.True((await f.Call("job cancel", [queued.Id], new() { ["keep-files"] = null })).Ok); Assert.Equal(DownloadStatus.Cancelled, queued.Status);
        var reconcile = await f.Call("depot reconcile", ["572"], new() { ["refresh"] = null, ["yes"] = null }); Assert.True(reconcile.Ok); Assert.NotEmpty(f.Vault.GetVariants(572)); Assert.NotNull(f.Vault.GetActiveHash(572));
        Assert.Throws<ArgumentException>(() => DepotDownloadPlanning.ValidateOutput(f.Steam.EffectivePath!, f.Steam.EffectivePath));
        Assert.Throws<ArgumentException>(() => DepotDownloadPlanning.ValidateOutput("relative", f.Steam.EffectivePath));
        Assert.Throws<ArgumentException>(() => DepotDownloadPlanning.ValidateOutput(@"\\?\C:\fixture", f.Steam.EffectivePath));
        Assert.Throws<ArgumentException>(() => DepotDownloadPlanning.ValidateOutput(@"C:\fixture:stream", f.Steam.EffectivePath));
    }

    [Theory]
    [InlineData("replace", true)] [InlineData("skip", false)]
    public async Task LocalGuiBatchJobsExposeDiffAndSupportRealPipeDecisionAndOutcomes(string decision, bool replaced)
    {
        using var f = new Fixture(); f.Live("addappid(570)\n-- old\n");
        string input = Path.Combine(f.Root, "570.lua"); File.WriteAllText(input, "addappid(570)\n-- new\n");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = f.Local.InstallAsync([input], async (_, _, ct) => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, ct); return false; });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var batch = Assert.Single(f.Local.Operations);
        var job = await f.Call("job get", [batch.Id]); Assert.True(job.Ok); Assert.NotNull(job.Data!["pending_decision"]);
        var waitingJob = await f.Call("job wait", [batch.Id]); Assert.Equal("awaiting_confirmation", waitingJob.Data!["state"]!.ToString());
        var observation = AgentClient.WaitOutcome(waitingJob)!; Assert.Equal("CONSENT_REQUIRED", observation.Error!.Code); Assert.NotNull(observation.Error.Details!["pending_decision"]);
        if (replaced) Assert.Equal("CONSENT_REQUIRED", (await f.Call("job confirm", [batch.Id], new() { ["decision"] = decision })).Error!.Code);
        var confirm = await f.Call("job confirm", [batch.Id], new() { ["decision"] = decision, ["yes"] = null }); Assert.True(confirm.Ok);
        var outcomes = await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(!replaced, Assert.Single(outcomes).Skipped);
        var finished = await f.Call("job wait", [batch.Id]); Assert.Equal("completed", finished.Data!["state"]!.ToString()); Assert.NotNull(finished.Data["result"]!["results"]);
        Assert.Equal(replaced, f.Vault.ReadLiveText(570).Contains("-- new"));
    }

    [Fact]
    public async Task CloudSaveDryRunConsentEnableWarningAndGameInstalledStatusAreObservational()
    {
        using var f = new Fixture(); f.Live("addappid(570)\n");
        int before = f.Net.Calls;
        var get = await f.Call("game get", ["570"], new() { ["installed"] = null, ["fields"] = "id,installed,add_status" }); Assert.True(get.Ok); Assert.NotNull(get.Data!["add_status"]); Assert.Equal(before, f.Net.Calls);
        var dry = await f.Call("cloud-save update", flags: new() { ["enabled"] = "true", ["dry-run"] = null }); Assert.True(dry.Ok, dry.Error?.Code); Assert.False(f.Settings.CloudRedirectEnabled); Assert.Equal(0, f.Mode.Enables);
        Assert.Equal("CONSENT_REQUIRED", (await f.Call("cloud-save update", flags: new() { ["enabled"] = "true" })).Error!.Code);
        var done = await f.Result(await f.Call("cloud-save update", flags: new() { ["enabled"] = "true", ["yes"] = null })); Assert.Equal("completed", done.Data!["state"]!.ToString()); Assert.True(f.Settings.CloudRedirectEnabled); Assert.Equal(1, f.Mode.Enables);
        var install = await f.Call("game install", ["570"], new() { ["yes"] = null }); Assert.Equal("STEAM_PROTOCOL_ISOLATION_UNPROVEN", install.Error!.Code);
    }

    [Fact]
    public async Task LaunchStalePlanVersionsReadOnlyAndRetryDoNotStageTwice()
    {
        using var f = new Fixture(); string json = "{\"executable\":\"New.exe\"}";
        var dry = await f.Call("launch-option create", ["603750"], new() { ["dry-run"] = null }, json); Assert.True(dry.Ok); Assert.True(f.Launch.Store.IsEmpty);
        Assert.Equal("READ_ONLY", (await f.Call("launch-option create", ["603750"], new() { ["read-only"] = null }, json)).Error!.Code);
        Assert.Equal("VERSION_CONFLICT", (await f.Call("launch-option create", ["603750"], new() { ["if-version"] = "old" }, json)).Error!.Code);
        var state = f.Launch.Inspect(603750)!; f.Launch.Stage(603750, state.ChangeNumber, state.Current, state.Current);
        Assert.Equal("PLAN_STALE", (await f.Call("launch-option create", ["603750"], new() { ["plan-id"] = dry.Data!["plan"]!["id"]!.ToString() }, json)).Error!.Code);
        var flags = new Dictionary<string, string?> { ["idempotency-key"] = "create-once" };
        var first = await f.Call("launch-option create", ["603750"], flags, json); await f.Result(first);
        var retry = await f.Call("launch-option create", ["603750"], flags, json); Assert.Equal(first.Data!["job_id"]!.ToString(), retry.Data!["job_id"]!.ToString()); Assert.Equal(2, f.Launch.Store.Get(603750)!.Desired.Count);
    }

    [Fact]
    public async Task GuiBuildsEditorAndLaunchAdapterRetainOriginLabelOrderAndOriginalBytes()
    {
        using var f = new Fixture(); f.Live("addappid(570)\n"); f.Vault.SyncDefaultFromLive(570);
        var preset = f.Vault.SaveText(570, "addappid(570)\n-- origin\n", "Keep label")!; Assert.True(f.Vault.Apply(570, preset));
        await f.Builds.SelectAppAsync(570); f.Builds.EditCommand.Execute(null); f.Builds.EditorText = "addappid(570)\n-- GUI editor\n"; f.Builds.SaveEditCommand.Execute(null);
        Assert.False(f.Builds.IsEditing); Assert.Equal("Keep label", f.Vault.GetActiveVariant(570)!.Label); Assert.Null(f.Vault.GetEditBase(570));
        var vm = new LaunchOptionsViewModel(f.Launch, new ToastService()); await vm.LoadAsync(603750, "Fixture");
        vm.AddEntryCommand.Execute(null); vm.Selected!.Executable = " Added.exe "; vm.MoveUpCommand.Execute(null);
        new LaunchEditCoordinator(f.Launch).Stage(603750, vm.Entries.Select(e => e.ToOption()).ToArray());
        Assert.Equal("Added.exe", f.Launch.Store.Get(603750)!.Desired[0].Executable); Assert.Equal("Original.exe", f.Launch.Store.Get(603750)!.Original[0].Executable);
    }

    [Fact]
    public void SharedGuiDepotPickerKeepsPinnedSizeLanguageFallbackAndPlatformAndKeyGates()
    {
        using var f = new Fixture();
        f.Live("addappid(570)\n" + string.Join("\n", new[] { 571,572,573 }.Select(id => $"addappid({id},0,\"{new string('a',64)}\")")));
        DepotRow Row(long id, string? language, string? os = "windows") => new(id, "Fixture", "Fixture", false, false, "https://steamdb.info/depot/" + id, "111", null, "222") { Language = language, Os = os, Size = 100, LuaSize = 50, ToggleId = id };
        var picks = DepotDownloadPlanning.Build([Row(571,"japanese"), Row(572,"german","linux"), Row(574,null)], f.Tool, 570, "french");
        Assert.True(picks.Single(p => p.DepotId == 571).IsSelected); Assert.Equal(50, picks[0].Size); Assert.Equal("111", picks[0].ManifestId);
        Assert.False(picks.Single(p => p.DepotId == 572).IsSelected); Assert.False(picks.Single(p => p.DepotId == 574).CanDownload);
        picks = DepotDownloadPlanning.Build([Row(571,null),Row(573,"japanese")], f.Tool, 570,"french");
        Assert.True(picks[0].IsSelected); Assert.False(picks[1].IsSelected);
    }

    [Fact]
    public async Task SharedGuiPresetSaveFallsBackWhenOriginVanishesAndDeduplicates()
    {
        using var f = new Fixture(); f.Live("addappid(570)\n");
        var coordinator = new DepotPresetCoordinator(f.Vault);
        var origin = coordinator.Create(570, "addappid(570)\n-- origin\n", "Origin")!; f.Vault.SetEditBase(570, origin.Hash); f.Live("addappid(570)\n-- divergence\n");
        Assert.True(f.Vault.Delete(570, origin.Hash));
        var fallback = coordinator.SaveLive(570, origin.Hash); Assert.NotNull(fallback); Assert.Null(f.Vault.GetEditBase(570));
        var dedup = coordinator.Create(570, f.Vault.ReadLiveText(570)); Assert.Equal(fallback!.Hash, dedup!.Hash);
    }

    [Fact]
    public async Task CloudAdmissionAndDepotDownloadPlansAreStableAndRejectChangedContext()
    {
        using var f = new Fixture();
        var dry = await f.Call("cloud-save update", flags: new() { ["enabled"] = "true", ["dry-run"] = null }); Assert.True(dry.Ok);
        Assert.Equal("READ_ONLY", (await f.Call("cloud-save update", flags: new() { ["enabled"] = "true", ["read-only"] = null })).Error!.Code);
        Assert.Equal("VERSION_CONFLICT", (await f.Call("cloud-save update", flags: new() { ["enabled"] = "true", ["yes"] = null, ["if-version"] = "old" })).Error!.Code);
        f.Cab.Status = f.Cab.Status with { Cloud = f.Cab.Status.Cloud with { ServerHost = "changed.test" } };
        Assert.Equal("PLAN_STALE", (await f.Call("cloud-save update", flags: new() { ["enabled"] = "true", ["yes"] = null, ["plan-id"] = dry.Data!["plan"]!["id"]!.ToString() })).Error!.Code);
        f.Live("addappid(570)\naddappid(571,0,\"" + new string('a',64) + "\")\nsetManifestid(571,\"111\",1)\n"); f.Settings.DepotDownloaderEnabled = true;
        var flags = new Dictionary<string, string?> { ["depot"] = "571", ["output"] = Path.Combine(f.Root,"output"), ["dry-run"] = null };
        var plan = await f.Call("depot download", ["570"], flags); Assert.True(plan.Ok); Assert.Empty(f.Queue.Items);
        flags.Remove("dry-run"); flags["yes"] = null; flags["plan-id"] = plan.Data!["plan"]!["id"]!.ToString();
        var accepted = await f.Call("depot download", ["570"], flags); Assert.True(accepted.Ok, accepted.Error?.Code);
        f.Settings.DepotDownloaderEnabled = false;
        var item = Assert.Single(f.Queue.Items);
        var revoked = await Assert.ThrowsAsync<MutationRejectedException>(() => item.Job.DownloadAsync(item, new Progress<DownloadProgress>(), default)); Assert.Equal("WORKFLOW_DISABLED", revoked.Code);
    }

    private sealed class ProcessIdentity(string? path, int session) : ISteamProcess
    {
        public bool HasExited => false;
        public int SessionId => session;
        public string? ExecutablePath => path;
        public void KillTree() => throw new InvalidOperationException("No process controls in protocol tests");
        public bool WaitForExit(int milliseconds) => false;
        public void Dispose() { }
    }
    [Fact]
    public void SteamProtocolNeverForwardsIntoAnUnrelatedOrAmbiguousLiveInstallation()
    {
        Assert.True(SteamInstallHandoff.OwnsProcesses(@"C:\selected\steam.exe",1,[new ProcessIdentity(@"C:\selected\steam.exe",1)]));
        Assert.False(SteamInstallHandoff.OwnsProcesses(@"C:\selected\steam.exe",1,[new ProcessIdentity(@"C:\other\steam.exe",1)]));
        Assert.False(SteamInstallHandoff.OwnsProcesses(@"C:\selected\steam.exe",1,[new ProcessIdentity(null,1)]));
    }

    [Theory]
    [InlineData(false, false, true)] [InlineData(true, false, false)] [InlineData(false, true, false)]
    public void ProtocolProofRequiresExactSelectedExecutableAndNoIsolationOrDelegation(bool isolated, bool delegated, bool expected)
    {
        Assert.Equal(expected, SteamInstallHandoff.Owns(@"C:\fixture\steam.exe", "\"C:\\fixture\\steam.exe\" \"%1\"", isolated, delegated));
        Assert.False(SteamInstallHandoff.Owns(@"C:\other\steam.exe", "\"C:\\fixture\\steam.exe\" \"%1\"", false, false));
    }
}
