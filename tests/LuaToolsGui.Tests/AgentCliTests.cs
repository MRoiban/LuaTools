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
    private AgentCommandService Facade(SettingsService? settings = null, DownloadQueue? queue = null)
    {
        Directory.CreateDirectory(_directory);
        settings ??= new SettingsService(new AppSettings());
        var steam = new SteamService(settings);
        var cache = new CacheService(Path.Combine(_directory, "cache.json"));
        var github = new GithubProxy();
        return new(settings, steam, new AuthService(_http, Path.Combine(_directory, "auth.dat")),
            queue ?? new DownloadQueue(cache, NullLogger<DownloadQueue>.Instance), new UnlockerService(steam, settings, cache, github),
            new PluginInstallerService(steam, settings, github, null!), new UpdateService(), mutations: new MutationCoordinator(Path.Combine(_directory, "journal.json")));
    }
    private static AgentRequest Request(AgentCommandService facade, string command, string[]? args = null, Dictionary<string, string?>? flags = null, string token = "") => new(1, token, facade.InstanceId, Guid.NewGuid().ToString("N"), command, args ?? [], flags ?? []);
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
        byte[] capability = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = LocalDiscovery.Protect(capability);
        Assert.NotEqual(capability, wrapped); Assert.Equal(capability, LocalDiscovery.Protect(wrapped, true));
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
        log.Write(LogLevel.Warning, "Agent", "token=secret-value"); log.Write(LogLevel.Warning, "Agent", "token=secret-value");
        var rows = log.ReadAgentEvents(new() { ["severity"] = "warn" });
        Assert.Equal(2, rows.Count); Assert.DoesNotContain("secret-value", rows.ToJsonString());
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
                    var facade = Facade(queue: queue); byte[] capability = RandomNumberGenerator.GetBytes(32);
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
                        Assert.True((await Call("job retry", [item.Id], new() { ["yes"] = null })).Ok);
                        var next = queue.Items.Single(x => x.IsActive); Assert.NotEqual(item.Id, next.Id);
                        await Call("job cancel", [next.Id]); await next.Completion.WaitAsync(TimeSpan.FromSeconds(5));
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
                        dispatcher.BeginInvoke(() => Thread.Sleep(50), System.Windows.Threading.DispatcherPriority.Send);
                        var timeout = await Call("doctor", flags: new() { ["timeout"] = "1ms" });
                        Assert.Equal("TIMEOUT", timeout.Error!.Code); Assert.Equal(7, timeout.Error.ExitCode);
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
