using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using LuaTools.Agent;
using LuaToolsCli;
using LuaToolsGui.Services;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Agent;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

[Collection("Agent dispatcher regression")]
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
        internal readonly LuaVault Vault;
        internal readonly LuaOverwriteCoordinator Diff;
        internal readonly LocalInstallCoordinator Local;
        internal readonly SteamAppListCache AppList;
        internal readonly SteamAppInfoCache AppInfo;
        internal readonly SteamDepotInfo Depots;
        internal readonly DownloadQueue Queue;
        internal readonly AgentCommandService Facade;
        internal readonly AcquisitionCoordinator Sources;
        internal readonly ManifestJobFactory Factory;
        internal readonly HttpClient Http;
        internal readonly byte[] Capability = RandomNumberGenerator.GetBytes(32);
        internal int Spent;
        internal bool FailHubcap;
        internal Fixture(bool key = true, DlcBatchService? batchService = null)
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
                if (path == "/api/denuvo/fixes") return Json("{\"appid\":\"730\",\"name\":\"Fixture\",\"fixes\":[{\"id\":\"fixture-fix\",\"title\":\"Fixture manifest\",\"hasManifest\":true,\"manifestFilename\":\"730.zip\"}]}");
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
            Installer = new(Steam, Settings, cache, Vault = new LuaVault(() => Steam.StPlugInDir, Path.Combine(Root, "vault")));
            Diff = new(Installer, AppList, AppInfo, Depots);
            Local = new(Installer, Diff);
            Queue = new(cache, NullLogger<DownloadQueue>.Instance);
            var factory = Factory = new ManifestJobFactory(api, hubcap, Settings, Installer, new(Steam), covers, null!, null!, Depots, null!, null!, null!);
            Sources = new(api, hubcap, Settings, auth, Queue, factory, Installer);
            var github = new GithubProxy();
            Facade = new(Settings, Steam, auth, Queue, new(Steam, Settings, cache, github), new(Steam, Settings, github, null!), new(),
                mutations: new(Path.Combine(Root, "journal.json")), acquisition: new(Sources, Diff, Local, Installer, api, batchService ?? new(api, Depots, AppInfo, Installer), factory));
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
    public async Task RealDispatcherInstallInterleavingAndCancelledOverwriteAlwaysReleaseUi()
    {
        var ready = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(ui));
            ready.SetResult(ui);
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var dispatcher = await ready.Task;
        try
        {
            using var f = new Fixture();
            // Force exactly: worker owns InstallGate, dispatcher starts loose-manifest drop,
            // worker publishes its vault notification and synchronously needs the dispatcher.
            using var held = new ManualResetEventSlim(); using var uiEntered = new ManualResetEventSlim();
            using var uiReturned = new ManualResetEventSlim();
            var builds = await dispatcher.InvokeAsync(() => new BuildsViewModel(f.Steam, f.Vault, f.AppList, f.AppInfo, new CoverCache(new Stub(_ => new(HttpStatusCode.NotFound)), Path.Combine(f.Root, "ui-covers")), f.Depots, new ToastService(), f.Settings, null!, f.Queue, f.Factory, dispatcher));
            bool notificationTimedOut = false;
            Task notificationDeadline = Task.CompletedTask;
            // Bound the actual production Builds subscriber's synchronous dispatch on the
            // unfixed code. Aborting its pending operation breaks the deadlock for cleanup;
            // a timeout still fails this test, rather than hanging the entire WPF suite.
            System.Windows.Threading.DispatcherHookEventHandler hook = (_, e) =>
            {
                if (!Monitor.IsEntered(LuaInstaller.InstallGate) || dispatcher.CheckAccess()) return;
                notificationDeadline = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    if (e.Operation.Status != System.Windows.Threading.DispatcherOperationStatus.Pending) return;
                    notificationTimedOut = true; e.Operation.Abort();
                });
            };
            dispatcher.Hooks.OperationPosted += hook;
            string lua = f.CreateFile("730.lua", "addappid(730)\n");
            string manifest = f.CreateFile("731_123.manifest", "fixture");
            var worker = Task.Run(() =>
            {
                lock (LuaInstaller.InstallGate)
                {
                    held.Set(); Assert.True(uiEntered.Wait(TimeSpan.FromSeconds(3)));
                    Assert.True(SpinWait.SpinUntil(() => uiReturned.IsSet || (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3)));
                    return f.Installer.InstallLuaFile(lua, 730);
                }
            });
            Assert.True(held.Wait(TimeSpan.FromSeconds(3)));
            Task? drop = null;
            await dispatcher.InvokeAsync(() =>
            {
                uiEntered.Set();
                drop = f.Local.InstallAsync([manifest], (_, _, _) => Task.FromResult(true));
                uiReturned.Set();
            }).Task.WaitAsync(TimeSpan.FromSeconds(6));
            await worker.WaitAsync(TimeSpan.FromSeconds(5)); await drop!.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Hooks.OperationPosted -= hook;
            await notificationDeadline;

            // The production Builds notification must itself post, even if another UI caller
            // happens to wait on a worker holding InstallGate.
            bool blocked = false;
            await dispatcher.InvokeAsync(() =>
            {
                var notify = Task.Run(() => typeof(BuildsViewModel).GetMethod("OnUi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(builds, [(Action)(() => { })]));
                blocked = !notify.Wait(TimeSpan.FromSeconds(2));
            });


            f.Live("addappid(730)\naddappid(731)\n");
            var vm = await dispatcher.InvokeAsync(() => new DownloadViewModel(null!, null!, f.Settings, null!, new ToastService(), f.Installer, f.AppList, f.AppInfo, f.Depots, null!, new(f.Installer, f.AppList, f.AppInfo, f.Depots, f.Local), f.Queue, f.Factory, f.Sources, f.Diff, dispatcher));
            using var cancel = new CancellationTokenSource();
            var confirm = (Task<bool>)typeof(DownloadViewModel).GetMethod("ConfirmOverwriteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [new DownloadedFile(lua, "730.lua"), "cancel-ui", 730L, "Fixture", cancel.Token])!;
            for (int i = 0; i < 100 && !vm.IsConfirmingOverwrite; i++) await Task.Delay(10);
            Assert.True(vm.IsConfirmingOverwrite); cancel.Cancel();
            try { await confirm.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            bool stuck = await dispatcher.InvokeAsync(() => vm.IsConfirmingOverwrite);
            Assert.False(ConsentCoordinator.HasPending("cancel-ui"));
            Assert.True(!notificationTimedOut && !blocked && !stuck, $"local install deadlock={notificationTimedOut}; Builds blocking={blocked}; cancelled overlay stuck={stuck}");
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            }).Task.ConfigureAwait(false);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "dispatcher thread did not stop");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquisitionPinsAdmissionBeforeQuotaAndInstallAndSeparatesDedupe(bool dlc)
    {
        using var f = new Fixture();
        var row = new PluginAddService.SourceRow { Name = DexLuaService.SourceName, Status = "available" };
        var first = f.Sources.Enqueue(730, "fixture", row, dlc ? "700" : null);
        string other = Path.Combine(f.Root, "other-steam"); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "steam.exe"), "fixture");
        f.Settings.SteamPathOverride = other;
        await Assert.ThrowsAsync<DownloadAbortedException>(() => first.Job.DownloadAsync(first, new Progress<DownloadProgress>(), default));
        string file = f.CreateFile("730.lua", "addappid(730)\n");
        Assert.False(await first.Job.ConfirmAsync!(new(file, "730.lua"), first, default));
        Assert.False((await first.Job.InstallAsync(new(file, "730.lua"), first, default)).Ok);
        Assert.Null(f.Installer.ReadInstalledLua(730)); Assert.Equal(0, f.Spent);
        var second = f.Sources.Enqueue(730, "fixture", row, dlc ? "700" : null);
        Assert.NotEqual(first.Id, second.Id); Assert.NotEqual(first.Job.DedupeKey, second.Job.DedupeKey);
    }

    [Fact]
    public async Task InstallationRemainsPinnedWhenSelectionChangesInsideInstallDelegate()
    {
        using var f = new Fixture();
        string admittedPlugin = f.Steam.StPlugInDir!;
        string other = Path.Combine(f.Root, "other-steam"); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "steam.exe"), "fixture");
        string source = f.CreateFile("730.lua", "addappid(730)\n");
        var job = f.Installer.CaptureInstallation().Bind(new DownloadJob(DownloadKind.Manifest, "pin-proof", 730, "fixture", "fixture", null,
            (_, _, _) => Task.FromResult(new DownloadedFile(source, "730.lua")),
            (file, _, _) =>
            {
                // Simulate Settings changing immediately after the final admission check.
                f.Settings.SteamPathOverride = other;
                var result = f.Installer.InstallLuaFile(file.FilePath, 730);
                return Task.FromResult(new JobResult(!result.AnyFailed, result.Error));
            }));
        var item = f.Queue.Enqueue(job);
        Assert.True((await job.InstallAsync(new(source, "730.lua"), item, default)).Ok);
        Assert.True(File.Exists(Path.Combine(admittedPlugin, "730.lua")));
        Assert.Equal(other, f.Steam.EffectivePath);
        Assert.Null(f.Installer.ReadInstalledLua(730));
    }

    [Fact]
    public async Task DepotFactoryAlsoRejectsChangedSteamBeforeReadingKeysOrAcquiringTools()
    {
        using var f = new Fixture();
        var job = f.Factory.CreateDepotJob(730, "fixture", [], Path.Combine(f.Root, "output"));
        var item = f.Queue.Enqueue(job);
        string other = Path.Combine(f.Root, "other-steam"); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "steam.exe"), "fixture"); f.Settings.SteamPathOverride = other;
        await Assert.ThrowsAsync<DownloadAbortedException>(() => job.DownloadAsync(item, new Progress<DownloadProgress>(), default));
        Assert.False((await job.InstallAsync(new("unused", "unused"), item, default)).Ok);
        Assert.Equal(0, f.Spent);
    }

    [Fact]
    public async Task ManifestJobRejectsChangedInstallationEvenWithIdenticalMissingVersion()
    {
        using var f = new Fixture();
        // Admit directly through the factory, the shared GUI and agent manifest path.
        // Use the fixture's factory so the regression also covers GUI factory callers.
        var factory = f.Factory;
        var job = factory.CreateDenuvoJob("fixture-fix", "manifest", "730.lua", 730, "Fixture", "Fixture");
        var item = f.Queue.Enqueue(job);
        string other = Path.Combine(f.Root, "other-steam"); Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "steam.exe"), "fixture");
        f.Settings.SteamPathOverride = other;
        await Assert.ThrowsAsync<DownloadAbortedException>(() => job.DownloadAsync(item, new Progress<DownloadProgress>(), default));
        string file = f.CreateFile("730.lua", "addappid(730)\n");
        Assert.False((await job.InstallAsync(new(file, "730.lua"), item, default)).Ok);
        Assert.Null(f.Installer.ReadInstalledLua(730));
    }

    [Fact]
    public async Task DlcBatchRejectsSelectionChangeBeforeGenerationAndAfterDownload()
    {
        foreach (bool during in new[] { false, true })
        {
            string selection = "A"; int spent = 0, installs = 0;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ports = new DlcBatchService.Ports((id, _) => Task.FromResult<GameDetails?>(new() { AppId = id, Type = "game" }),
                (_, _) => Task.FromResult<IReadOnlyList<long>>([741]), _ => [], _ => false,
                (_, _, _) => Task.FromResult<DlcInfo?>(new() { DepotCount = 1, HaveCount = 1 }),
                async (_, _, _) => { spent++; start.SetResult(); await release.Task; return new("unused", "741.lua"); },
                (_, _) => { installs++; return new(true, 0, [], null); });
            // Selection ports are injected without a real Steam installation.
            var property = typeof(DlcBatchService.Ports).GetProperty("Installation");
            Assert.NotNull(property); property!.SetValue(ports, (Func<string?>)(() => selection));
            var service = new DlcBatchService(ports);
            Assert.True(service.Check(730, [])); var batch = service.GetState(730)!; await batch.Work;
            if (!during) selection = "B";
            Assert.True(service.Apply(730, batch.Id));
            if (during) { await start.Task.WaitAsync(TimeSpan.FromSeconds(3)); selection = "B"; }
            release.TrySetResult(); await batch.Work.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(during ? 1 : 0, spent); Assert.Equal(0, installs); Assert.Equal("failed", batch.Items[0].State);
        }
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
    [Fact]
    public async Task RealPipeDlcBatchReadOnlyCheckDryRunApplyPartialFailureAndCancelUseVerifiedGeneration()
    {
        var service = new DlcBatchService(new DlcBatchService.Ports(
            (id, _) => Task.FromResult<GameDetails?>(new() { AppId = id, Type = "game", Name = "Fixture" }),
            (_, _) => Task.FromResult<IReadOnlyList<long>>([741, 742, 743]), _ => [], _ => false,
            (id, parent, _) => Task.FromResult<DlcInfo?>(id == "743" ? new() { DepotCount = 1, MissingCount = 1 } : new()),
            (_, _, _) => throw new InvalidOperationException("No quota path is allowed"),
            (path, id) => { ManifestContentPolicy.ValidateLuaFile(path, id); return id == 742 ? InstallResult.Fail("synthetic failure") : new(true, 0, [], null); }));
        using var f = new Fixture(batchService: service);
        var check = await f.Pipe(f.Request("dlc check", flags: new() { ["read-only"] = null }));
        Assert.True(check!.Ok); string batchId = check.Data!["batch_id"]!.ToString();
        var batch = service.Find(batchId)!; await batch.Work;
        Assert.Equal("ready", batch.Phase); Assert.Equal("unavailable", batch.Items.Single(i => i.AppId == 743).State);
        var get = await f.Pipe(f.Request("dlc get", flags: new() { ["batch"] = batchId })); Assert.True(get!.Ok);
        var add = f.Request("dlc add", flags: new() { ["batch"] = batchId, ["dry-run"] = null });
        Assert.True((await f.Pipe(add))!.Ok); Assert.Equal("ready", batch.Phase);
        add.Flags.Remove("dry-run"); Assert.Equal(8, (await f.Pipe(add))!.Error!.ExitCode);
        add.Flags["yes"] = null; var accepted = await f.Pipe(add); Assert.True(accepted!.Ok);
        await batch.Work; Assert.Equal("installed", batch.Items.Single(i => i.AppId == 741).State); Assert.Equal("failed", batch.Items.Single(i => i.AppId == 742).State);
        var job = await f.Pipe(f.Request("job get", [accepted.Data!["id"]!.ToString()], []));
        Assert.Contains("PARTIAL_FAILURE", job!.Data!.ToJsonString());
        var next = await f.Pipe(f.Request("dlc check", flags: []));
        var ready = service.Find(next!.Data!["id"]!.ToString())!; await ready.Work;
        var cancel = f.Request("dlc cancel", [ready.Id], new() { ["dry-run"] = null });
        Assert.True((await f.Pipe(cancel))!.Ok); Assert.Equal("ready", ready.Phase);
        cancel.Flags.Remove("dry-run"); Assert.True((await f.Pipe(cancel))!.Ok); Assert.Equal("cancelled", ready.Phase);
        Assert.Equal(0, f.Spent);
    }
    [Fact]
    public async Task RealPipeManifestPlanAndLoginGateNeverAcquireAsGuest()
    {
        using var f = new Fixture();
        var request = f.Request("manifest add", ["fixture-fix"], new() { ["appid"] = "730", ["dry-run"] = null });
        Assert.True((await f.Pipe(request))!.Ok); Assert.Empty(f.Queue.Items);
        request.Flags.Remove("dry-run"); Assert.Equal(8, (await f.Pipe(request))!.Error!.ExitCode);
        request.Flags["yes"] = null; Assert.Equal("LOGIN_REQUIRED", (await f.Pipe(request))!.Error!.Code);
        Assert.Equal(0, f.Spent); Assert.Empty(f.Queue.Items);
    }

    [Fact]
    public async Task ChangedTargetAfterAcquisitionRequiresSharedDecisionAndSkipPreservesLiveBytes()
    {
        using var f = new Fixture();
        Assert.True((await f.Pipe(f.Request(flags: new() { ["source"] = AcquisitionCoordinator.HubcapSource, ["yes"] = null })))!.Ok);
        var item = Assert.Single(f.Queue.Items);
        var file = await item.Job.DownloadAsync(item, new Progress<DownloadProgress>(), default);
        try
        {
            f.Live("addappid(730)\naddappid(731)\n");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var decision = item.Job.ConfirmAsync!(file, item, timeout.Token);
            for (int i = 0; i < 100 && !ConsentCoordinator.HasPending(item.Id); i++) await Task.Delay(10);
            Assert.True(ConsentCoordinator.HasPending(item.Id)); Assert.NotNull(ConsentCoordinator.Details(item.Id));
            Assert.True(ConsentCoordinator.Decide(item.Id, false)); Assert.False(await decision);
            Assert.Contains("731", System.IO.File.ReadAllText(f.Installer.ReadInstalledLua(730)!));
            Assert.Equal(1, f.Spent);
        }
        finally { ManifestJobFactory.DeleteStaged(file.FilePath); }
    }

}

[CollectionDefinition("Agent dispatcher regression", DisableParallelization = true)]
public sealed class AgentDispatcherRegressionCollection { }
