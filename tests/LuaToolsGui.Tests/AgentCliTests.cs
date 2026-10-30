using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using LuaTools.Agent;
using LuaToolsCli;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Agent;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class AgentCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-tests-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private AgentCommandService Facade(SettingsService? settings = null, DownloadQueue? queue = null, MutationCoordinator? mutations = null, LuaVault? vault = null, LuaToolsGui.Services.AppInfo.LaunchOptionsService? launch = null)
    {
        Directory.CreateDirectory(_directory);
        settings ??= new SettingsService(new AppSettings());
        var steam = new SteamService(settings);
        var cache = new CacheService(Path.Combine(_directory, "cache.json"));
        var github = new GithubProxy();
        return new(settings, steam, new AuthService(_http, Path.Combine(_directory, "auth.dat")),
            queue ?? new DownloadQueue(cache, NullLogger<DownloadQueue>.Instance), new UnlockerService(steam, settings, cache, github),
            new PluginInstallerService(steam, settings, github, null!), new UpdateService(), mutations: mutations ?? new MutationCoordinator(Path.Combine(_directory, "journal.json")), vault: vault, launch: launch);
    }
    private static AgentRequest Request(AgentCommandService facade, string command, string[]? args = null, Dictionary<string, string?>? flags = null, string token = "") => new(1, token, facade.InstanceId, Guid.NewGuid().ToString("N"), command, args ?? [], flags ?? [], facade.SteamIdentity);


    [Fact]
    public async Task LaunchApplyRequiresBothProcessAndMutationConsentWithoutCallingRealSteam()
    {
        Directory.CreateDirectory(_directory);
        string appinfo = Path.Combine(_directory, "appinfo.vdf");
        LaunchOptionsServiceTests.WriteAppInfo(appinfo, [("0", "Game.exe", "windows")]);
        var store = new LuaToolsGui.Services.AppInfo.LaunchModStore(Path.Combine(_directory, "launchmods.json"));
        int stops = 0;
        var launch = new LuaToolsGui.Services.AppInfo.LaunchOptionsService(() => appinfo, () => true,
            () => stops++, () => { }, store, Path.Combine(_directory, "backups"));
        var state = launch.Read(603750)!;
        launch.Stage(603750, state.ChangeNumber, state.Current, [new LuaToolsGui.Services.AppInfo.LaunchOption { Executable = "Edited.exe" }]);
        var facade = Facade(launch: launch);
        Task<Envelope> Call(Dictionary<string, string?> flags) => facade.ExecuteAsync(Request(facade, "launch-option apply", flags: flags), default);
        var dry = await Call(new() { ["drifted"] = null, ["dry-run"] = null });
        Assert.True(dry.Ok); Assert.True(dry.Data!["plan"]!["requires_steam_restart"]!.GetValue<bool>());
        Assert.Equal("CONSENT_REQUIRED", (await Call(new() { ["drifted"] = null })).Error!.Code);
        Assert.Equal("STEAM_RUNNING", (await Call(new() { ["drifted"] = null, ["yes"] = null })).Error!.Code);
        Assert.Equal(0, stops);
        Assert.NotNull(store.Get(603750));
    }
    [Fact]
    public async Task PresetReadsAndDryRunsAcrossRealPipeNeverChangeDiskOrExposeKeys()
    {
        Directory.CreateDirectory(_directory);
        string plug = Path.Combine(_directory, "plug"); Directory.CreateDirectory(plug);
        var vault = new LuaVault(() => plug, Path.Combine(_directory, "vault"));
        string live = Path.Combine(plug, "570.lua");
        File.WriteAllText(live, "addappid(570)\naddappid(571,0,\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\")\n");
        vault.SyncDefaultFromLive(570);
        string hash = Assert.Single(vault.GetVariants(570)).Hash;
        // External divergence and loose builds must remain unadopted by every read and dry-run.
        File.AppendAllText(live, "-- external change\n");
        File.WriteAllText(Path.Combine(plug, "570_999.lua"), "addappid(570)\n-- loose\n");
        var facade = Facade(vault: vault);
        Dictionary<string, string> Disk() => Directory.GetFiles(_directory, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var before = Disk();
        byte[] capability = RandomNumberGenerator.GetBytes(32);
        using var service = new AgentPipeHostedService(facade, NullLogger<AgentPipeHostedService>.Instance, capability);
        async Task<Envelope> Call(string command, string[] args, Dictionary<string, string?> flags)
        {
            string pipe = "LuaTools-depots-" + Guid.NewGuid().ToString("N");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var host = Task.Run(async () => { await server.WaitForConnectionAsync(timeout.Token); await service.ServeAsync(server, timeout.Token); });
            var result = await AgentClient.ExchangeAsync(pipe, Request(facade, command, args, flags, Convert.ToBase64String(capability)), timeout.Token);
            await host; return result;
        }
        foreach (string command in new[] { "preset list", "preset get" })
        {
            var reply = await Call(command, command.EndsWith("get") ? [hash] : [], new() { ["appid"] = "570" });
            Assert.True(reply.Ok); Assert.DoesNotContain("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", JsonSerializer.Serialize(reply, Protocol.Json));
        }
        var dry = await Call("depot reconcile", ["570"], new() { ["dry-run"] = null });
        Assert.True(dry.Ok); Assert.Contains("discard", dry.Data!.ToJsonString());
        Assert.Equal(before.OrderBy(p => p.Key), Disk().OrderBy(p => p.Key));
        Assert.Single(vault.GetVariants(570));
        Assert.NotEqual(hash, vault.GetActiveHash(570));
    }
    [Fact]
    public async Task PresetMutationAdmissionVersionsConsentReadOnlyPartialFailureAndRetry()
    {
        Directory.CreateDirectory(_directory);
        string plug = Path.Combine(_directory, "plug"); Directory.CreateDirectory(plug);
        var vault = new LuaVault(() => plug, Path.Combine(_directory, "vault"));
        string live = Path.Combine(plug, "570.lua"); File.WriteAllText(live, "addappid(570)\n");
        vault.SyncDefaultFromLive(570); string hash = Assert.Single(vault.GetVariants(570)).Hash;
        File.AppendAllText(live, "-- divergence\n");
        var facade = Facade(vault: vault);
        Task<Envelope> Call(string verb, Dictionary<string, string?> flags) => facade.ExecuteAsync(Request(facade, "preset " + verb, [hash], flags), default);
        Assert.Equal("READ_ONLY", (await Call("delete", new() { ["appid"] = "570", ["read-only"] = null })).Error!.Code);
        Assert.Equal("CONSENT_REQUIRED", (await Call("delete", new() { ["appid"] = "570" })).Error!.Code);
        var dry = await Call("update", new() { ["appid"] = "570", ["name"] = "First", ["dry-run"] = null });
        string plan = dry.Data!["plan"]!["id"]!.ToString();
        vault.Rename(570, hash, "GUI edit");
        Assert.Equal("PLAN_STALE", (await Call("update", new() { ["appid"] = "570", ["name"] = "First", ["plan-id"] = plan })).Error!.Code);
        Assert.Equal("VERSION_CONFLICT", (await Call("update", new() { ["appid"] = "570", ["name"] = "First", ["if-version"] = "old" })).Error!.Code);
        var flags = new Dictionary<string, string?> { ["appid"] = "570", ["name"] = " Trimmed ", ["idempotency-key"] = "rename-once" };
        Assert.True((await Call("update", flags)).Ok); Assert.Equal("Trimmed", Assert.Single(vault.GetVariants(570)).Label);
        vault.Rename(570, hash, "Later GUI edit");
        Assert.True((await Call("update", flags)).Ok); Assert.Equal("Later GUI edit", Assert.Single(vault.GetVariants(570)).Label);
        var partial = await facade.ExecuteAsync(Request(facade, "preset update", [], new() { ["appid"] = "570", ["name"] = "Bulk", ["ids"] = hash + ",missing" }), default);
        Assert.Equal("PARTIAL_FAILURE", partial.Error!.Code); Assert.Equal("Bulk", Assert.Single(vault.GetVariants(570)).Label);
    }
    [Fact]
    public async Task FacadeReadsOnlyAllowlistedSettingsAndRefusesSecretNames()
    {
        const string secret = "smm_secret-never-serialize";
        var settings = new SettingsService(new AppSettings { HubcapApiKey = SettingsService.ProtectApiKey(secret), SteamPathOverride = @"C:\Users\PrivateUser\Steam" });
        var facade = Facade(settings);
        var result = await facade.ExecuteAsync(Request(facade, "setting list"), default);
        string json = JsonSerializer.Serialize(result, Protocol.Json);
        Assert.True(result.Ok); Assert.DoesNotContain(secret, json); Assert.DoesNotContain("HubcapApiKey", json); Assert.DoesNotContain("PrivateUser", json);
        Assert.Equal(23, result.Data!.AsArray().Count);
        Assert.Equal(3, (await facade.ExecuteAsync(Request(facade, "setting get", ["HubcapApiKey"]), default)).Error!.ExitCode);
    }
    [Fact]
    public async Task FacadeRejectsContextAndReadOnlyMutationAndPrivateProjection()
    {
        var facade = Facade();
        Assert.Equal(6, (await facade.ExecuteAsync(Request(facade, "ping") with { InstanceId = "wrong" }, default)).Error!.ExitCode);
        Assert.Equal(6, (await facade.ExecuteAsync(Request(facade, "ping") with { ExpectedSteam = "wrong" }, default)).Error!.ExitCode);
        Assert.Equal(5, (await facade.ExecuteAsync(Request(facade, "setting update", ["Language"], new() { ["value"] = "fr", ["read-only"] = null }), default)).Error!.ExitCode);
        Assert.Equal(2, (await facade.ExecuteAsync(Request(facade, "setting list", flags: new() { ["fields"] = "HubcapApiKey" }), default)).Error!.ExitCode);
        Assert.Equal(5, (await facade.ExecuteAsync(Request(facade, "doctor", flags: new() { ["fix"] = null }), default)).Error!.ExitCode);
    }
    [Fact]
    public async Task FacadePingWhoamiQueueDoctorUseRealServices()
    {
        var facade = Facade();
        Assert.True((await facade.ExecuteAsync(Request(facade, "ping"), default)).Data!["ready"]!.GetValue<bool>());
        Assert.Equal("guest", (await facade.ExecuteAsync(Request(facade, "whoami"), default)).Data!["state"]!.GetValue<string>());
        Assert.Empty((await facade.ExecuteAsync(Request(facade, "download list"), default)).Data!.AsArray());
        Assert.True((await facade.ExecuteAsync(Request(facade, "status"), default)).Ok);
        var doctor = await facade.ExecuteAsync(Request(facade, "doctor"), default);
        Assert.True(doctor.Ok);
        var codes = doctor.Data!.AsArray().Select(n => n!["code"]!.GetValue<string>()).ToArray();
        Assert.Contains("STEAM_LOCATION", codes); Assert.Contains("WORKFLOW_DISABLED", codes); Assert.Contains("UPDATE_STATE", codes);
    }
    [Theory]
    [InlineData(null, @"C:\bin", @"C:\bin")]
    [InlineData(@"C:\one", @"C:\bin", @"C:\one;C:\bin")]
    [InlineData(@"C:\one;", @"C:\bin", @"C:\one;C:\bin")]
    [InlineData(@"C:\BIN\;C:\one", @"c:\bin", @"C:\BIN\;C:\one")]
    [InlineData("\"C:\\bin\";C:\\one", @"C:\bin", "\"C:\\bin\";C:\\one")]
    public void UserPathIsIdempotentAndPreservesExistingText(string? before, string directory, string expected) => Assert.Equal(expected, AgentCliInstallation.AddToPath(before, directory));
    [Fact]
    public void DiscoveryOwnerAclAndDpapiRoundTrip()
    {
        LocalDiscovery.SecureDirectory(_directory);
        LocalDiscovery.Verify(_directory, true);
        var owner = LocalDiscovery.FindOwner(new LauncherRecord(Environment.ProcessPath!));
        Assert.NotNull(owner); Assert.Equal(LocalDiscovery.IsElevated(), owner.Value.Elevated);
        byte[] capability = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = LocalDiscovery.Protect(capability);
        Assert.NotEqual(capability, wrapped); Assert.Equal(capability, LocalDiscovery.Protect(wrapped, true));
        string newFile = Path.Combine(_directory, "owned.new"); File.WriteAllText(newFile, "private");
        LocalDiscovery.SecureNewFile(newFile); LocalDiscovery.Verify(newFile);
        var security = new DirectoryInfo(_directory).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(security);
        Assert.Throws<UnauthorizedAccessException>(() => LocalDiscovery.Verify(_directory, true));
    }
    [Fact]
    public async Task RealClientHostPipeRoundTripRejectsBadCapability()
    {
        var facade = Facade(); byte[] capability = RandomNumberGenerator.GetBytes(32);
        using var service = new AgentPipeHostedService(facade, NullLogger<AgentPipeHostedService>.Instance, capability);
        foreach (string supplied in new[] { Convert.ToBase64String(capability), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })
        {
            string name = "LuaTools-test-" + Guid.NewGuid().ToString("N");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var host = Task.Run(async () => { await server.WaitForConnectionAsync(timeout.Token); await service.ServeAsync(server, timeout.Token); });
            var reply = await AgentClient.ExchangeAsync(name, Request(facade, "ping", token: supplied), timeout.Token);
            Assert.Equal(supplied == Convert.ToBase64String(capability), reply.Ok);
            if (!reply.Ok) Assert.Equal(5, reply.Error!.ExitCode);
            await host;
        }
    }
    [Fact]
    public async Task StalledConnectionDoesNotBlockSecondClient()
    {
        var facade = Facade(); byte[] capability = RandomNumberGenerator.GetBytes(32);
        using var service = new AgentPipeHostedService(facade, NullLogger<AgentPipeHostedService>.Instance, capability);
        string name = "LuaTools-test-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var stalled = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = first.WaitForConnectionAsync(timeout.Token); await stalled.ConnectAsync(timeout.Token); await accepted;
        using var cancelStall = new CancellationTokenSource(); var stallTask = service.ServeAsync(first, cancelStall.Token);
        var second = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var host = Task.Run(async () => { await second.WaitForConnectionAsync(timeout.Token); await service.ServeAsync(second, timeout.Token); });
        Assert.True((await AgentClient.ExchangeAsync(name, Request(facade, "ping", token: Convert.ToBase64String(capability)), timeout.Token)).Ok);
        await host; cancelStall.Cancel(); await stallTask;
    }
    [Fact]
    public void ActivityLogDeduplicatesDualStreamsPreservesEqualMessagesAndRedacts()
    {
        var log = new AppEventLog(_directory);
        string? published = null; log.EntryWritten += value => published = value;
        log.Write(LogLevel.Warning, "Agent", "token=secret-value"); log.Write(LogLevel.Warning, "Agent", "token=secret-value");
        var rows = log.ReadAgentEvents(new() { ["severity"] = "warn" });
        Assert.Equal(2, rows.Count); Assert.DoesNotContain("secret-value", rows.ToJsonString());
        Assert.NotNull(published); Assert.DoesNotContain("secret-value", published);
        Assert.NotEqual(rows[0]!["id"]!.ToString(), rows[1]!["id"]!.ToString());
        Assert.Empty(log.ReadAgentEvents(new() { ["category"] = "Other" }));
    }
    [Fact]
    public async Task PortableCliHarnessPassesOnWindows()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "LuaToolsGui.sln"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new InvalidOperationException();
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root, "tests", "LuaToolsCli.Tests", "bin", "Release", "net8.0", "LuaToolsCli.Tests.dll"));
        using var child = System.Diagnostics.Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(child.ExitCode == 0, await output + await error);
        Assert.Contains("CLI TEST PASS", await output);
    }
    [Fact]
    public async Task MutationPlansVersionsBulkConsentAndJournalUseRealSettings()
    {
        var settings = new SettingsService(new AppSettings()); var facade = Facade(settings);
        Task<Envelope> Update(string key, string value, Dictionary<string, string?>? options = null)
        {
            var flags = options ?? []; flags["value"] = value;
            return facade.ExecuteAsync(Request(facade, "setting update", [key], flags), default);
        }
        var dry = await Update("FastFetch", "true", new() { ["dry-run"] = null });
        Assert.True(dry.Ok); Assert.False(settings.FastFetch); Assert.False(File.Exists(Path.Combine(_directory, "journal.json")));
        var consent = await Update("SteamlessEnabled", "true"); Assert.Equal(8, consent.Error!.ExitCode); Assert.NotNull(consent.Error.Details!["plan"]);
        Assert.False(settings.IsExecutableWorkflowEnabled("Steamless"));
        Assert.Equal(8, (await Update("SteamlessEnabled", "True")).Error!.ExitCode);
        Assert.True((await Update("SteamlessEnabled", "true", new() { ["yes"] = null, ["actor-note"] = "unit-proof" })).Ok);
        Assert.True(settings.IsExecutableWorkflowEnabled("Steamless"));
        Assert.True((await Update("SteamlessEnabled", "false")).Ok); Assert.False(settings.IsExecutableWorkflowEnabled("Steamless"));
        Assert.Equal("READ_ONLY", (await Update("FastFetch", "true", new() { ["read-only"] = null })).Error!.Code);
        Assert.Equal("VERSION_CONFLICT", (await Update("FastFetch", "true", new() { ["if-version"] = "old" })).Error!.Code);
        settings.FastFetch = true;
        Assert.Equal("PLAN_STALE", (await Update("FastFetch", "true", new() { ["plan-id"] = dry.Data!["plan"]!["id"]!.ToString() })).Error!.Code);
        var first = await Update("Language", "fr", new() { ["idempotency-key"] = "unit-key" });
        settings.Language = "en";
        var repeat = await Update("Language", "fr", new() { ["idempotency-key"] = "unit-key" });
        Assert.Equal(JsonSerializer.Serialize(first, Protocol.Json), JsonSerializer.Serialize(repeat, Protocol.Json)); Assert.Equal("en", settings.Language);
        Assert.Equal("REQUEST_ID_CONFLICT", (await Update("Language", "en", new() { ["idempotency-key"] = "unit-key" })).Error!.Code);
        var restarted = Facade(settings);
        Assert.True((await restarted.ExecuteAsync(Request(restarted, "setting update", ["Language"], new() { ["value"] = "fr", ["idempotency-key"] = "unit-key" }), default)).Ok);
        var partial = await facade.ExecuteAsync(Request(facade, "setting update", flags: new() { ["ids"] = "FastFetch,unknown", ["value"] = "false" }), default);
        Assert.Equal("PARTIAL_FAILURE", partial.Error!.Code); Assert.Equal(2, partial.Error.Details!["results"]!.AsArray().Count); Assert.False(settings.FastFetch);
        Assert.True((await facade.ExecuteAsync(Request(facade, "setting update", flags: new() { ["data"] = "{\"key\":\"Language\",\"value\":\"fr\"}" }), default)).Ok);
        string journal = File.ReadAllText(Path.Combine(_directory, "journal.json")); Assert.DoesNotContain("unit-key", journal); Assert.DoesNotContain("unit-proof", journal);
    }
    [Fact]
    public async Task SyntheticQueueJobRunsAndControlsCrossTheRealPipe()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    var queue = new DownloadQueue(new CacheService(Path.Combine(_directory, "queue.json")), NullLogger<DownloadQueue>.Instance, dispatcher);
                    var coordinator = new MutationCoordinator(Path.Combine(_directory, "journal.json"));
                    var facade = Facade(queue: queue, mutations: coordinator); byte[] capability = RandomNumberGenerator.GetBytes(32);
                    using var service = new AgentPipeHostedService(facade, NullLogger<AgentPipeHostedService>.Instance, capability);
                    async Task<Envelope> Call(string command, string[]? args = null, Dictionary<string, string?>? flags = null)
                    {
                        string name = "LuaTools-test-" + Guid.NewGuid().ToString("N");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        var host = Task.Run(async () => { await server.WaitForConnectionAsync(timeout.Token); await service.ServeAsync(server, timeout.Token); });
                        var reply = await AgentClient.ExchangeAsync(name, Request(facade, command, args, flags, Convert.ToBase64String(capability)), timeout.Token); await host; return reply;
                    }
                    await queue.StartAsync(default);
                    try
                    {
                        var item = queue.Enqueue(new DownloadJob(DownloadKind.Depot, "depot:synthetic", 570, "Synthetic", "No network", null,
                            async (_, _, ct) => { await Task.Delay(TimeSpan.FromSeconds(60), ct); return new DownloadedFile(_directory, "synthetic"); },
                            (_, _, _) => Task.FromResult(new JobResult(true, null))));
                        Assert.True((await Call("job list")).Ok);
                        Assert.Equal(item.Id, (await Call("job get", [item.Id])).Data!["id"]!.ToString());
                        Assert.True((await Call("job pause", [item.Id])).Ok); Assert.Equal(DownloadStatus.Paused, item.Status);
                        if (item.RunningAttempt is { } attempt) await attempt.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.True((await Call("job resume", [item.Id])).Ok);
                        Assert.True((await Call("job cancel", [item.Id])).Ok);
                        await item.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(DownloadStatus.Cancelled, item.Status);
                        var retried = await Call("job retry", [item.Id], new() { ["yes"] = null, ["idempotency-key"] = "queue-retry" }); Assert.True(retried.Ok);
                        var next = queue.Items.Single(x => x.IsActive); Assert.NotEqual(item.Id, next.Id);
                        await Call("job cancel", [next.Id]); await next.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.Equal(next.Id, (await Call("job retry", [item.Id], new() { ["yes"] = null, ["idempotency-key"] = "queue-retry" })).Data!["id"]!.ToString());
                        Assert.Equal(next.Id, (await Call("job list", flags: new() { ["idempotency-key"] = "queue-retry" })).Data![0]!["id"]!.ToString());
                        string operation = facade.Jobs.Register("synthetic-operation", _ => Task.FromResult(Envelope.Success(new { complete = true })), default);
                        Assert.Equal("completed", (await Call("job wait", [operation])).Data!["state"]!.ToString());
                        var decision = queue.Enqueue(new DownloadJob(DownloadKind.Manifest, "manifest:synthetic", 571, "Decision", "No network", null,
                            (_, _, _) => Task.FromResult(new DownloadedFile(Path.Combine(_directory, "absent.bin"), "absent.bin")),
                            (_, _, _) => Task.FromResult(new JobResult(true, null)),
                            (_, item, ct) => ConsentCoordinator.WaitAsync(item.Id, ct)));
                        while (!ConsentCoordinator.HasPending(decision.Id)) await Task.Delay(10);
                        Assert.Equal("CONSENT_REQUIRED", (await Call("job confirm", [decision.Id], new() { ["decision"] = "replace" })).Error!.Code);
                        Assert.True((await Call("job confirm", [decision.Id], new() { ["decision"] = "replace", ["yes"] = null })).Ok);
                        await decision.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(DownloadStatus.Completed, decision.Status);
                        queue.Remove(decision);
                        Assert.True((await Call("job get", [decision.Id])).Ok);
                        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var holding = coordinator.ExecuteAsync(Request(facade, "setting update", ["FastFetch"], new() { ["value"] = "false", ["dry-run"] = null }), async ct =>
                        {
                            held.SetResult(); await Task.Delay(200, ct);
                            return new(new("test", "test", "test", [], [], false), _ => Task.FromResult(Envelope.Success(null)));
                        }, default);
                        await held.Task;
                        var timeout = await Call("setting update", ["FastFetch"], new() { ["value"] = "true", ["timeout"] = "10ms" });
                        Assert.Equal("TIMEOUT", timeout.Error!.Code); Assert.Equal(7, timeout.Error.ExitCode); await holding;
                    }
                    finally { await queue.StopAsync(default); }
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    public void Dispose() { _http.Dispose(); try { Directory.Delete(_directory, true); } catch { } }
}
