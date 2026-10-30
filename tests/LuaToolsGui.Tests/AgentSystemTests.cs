using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using LuaTools.Agent;
using LuaToolsCli;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Agent;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.Services.OnlineFixMe;
using LuaToolsGui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class AgentSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-system-" + Guid.NewGuid().ToString("N"));
    private sealed class BlockNetwork : HttpMessageHandler
    {
        public int Requests;
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return Task.FromResult(Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound)); }
    }
    private sealed class FakeSteam(SettingsService settings) : SteamService(settings)
    {
        public bool Running;
        public int Stops, Starts;
        public override bool IsSelectedSteamRunning() => Running;
        public override void StopSteam() { Stops++; Running = false; }
        public override bool StartSteam() { Starts++; Running = true; return true; }
        public override bool RestartSteam() { StopSteam(); return StartSteam(); }
    }
    private readonly BlockNetwork _network = new();
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly FakeSteam _steam;
    private readonly DownloadQueue _queue;
    private readonly ModeCoordinator _modes;
    private readonly FixCoordinator _fixes;
    private readonly AgentCommandService _facade;
    private readonly string _game;
    public AgentSystemTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_network);
        _settings = new(new AppSettings { SteamPathOverride = _root, SelectedMode = "Custom" }, Path.Combine(_root, "settings"));
        File.WriteAllText(Path.Combine(_root, "steam.exe"), "inert test marker; never launched");
        _game = Path.Combine(_root, "steamapps", "common", "TestGame"); Directory.CreateDirectory(_game);
        File.WriteAllText(Path.Combine(_root, "steamapps", "appmanifest_620.acf"), "\"appid\" \"620\"\n\"name\" \"TestGame\"\n\"installdir\" \"TestGame\"");
        _steam = new(_settings);
        var cache = new CacheService(Path.Combine(_root, "cache.json"));
        var gh = new GithubProxy(_http);
        var unlocker = new UnlockerService(_steam, _settings, cache, gh);
        var cloud = new CloudRedirectService(gh, _settings);
        _modes = new(unlocker, _steam, cloud);
        var library = new SteamLibraryService(_steam);
        var covers = new CoverCache(_network, Path.Combine(_root, "covers"));
        var auth = new AuthService(_http, Path.Combine(_root, "auth.dat"));
        var api = new LuaToolsApiClient(auth, null!, covers, _http);
        var online = new OnlineFixMeClient(_http);
        var factory = new ManifestJobFactory(api, null!, _settings, null!, library, covers, new ToastService(), null!, null!, null!, new AppliedFixIndexService(library, NullLogger<AppliedFixIndexService>.Instance, Path.Combine(_root, "index")), online);
        _fixes = new(api, auth, _settings, _steam, library, online, factory);
        _queue = new(cache, NullLogger<DownloadQueue>.Instance);
        _facade = new(_settings, _steam, auth, _queue, unlocker, new PluginInstallerService(_steam, _settings, gh, null!), new UpdateService(),
            mutations: new MutationCoordinator(Path.Combine(_root, "journal.json")), modes: _modes, fixes: _fixes, processes: new SteamCoordinator(_steam));
    }
    private AgentRequest Request(string command, string[]? args = null, Dictionary<string, string?>? flags = null) => new(1, "", _facade.InstanceId, Guid.NewGuid().ToString("N"), command, args ?? [], flags ?? [], _facade.SteamIdentity);
    private Task<Envelope> Run(string command, string[]? args = null, Dictionary<string, string?>? flags = null) => _facade.ExecuteAsync(Request(command, args, flags), default);
    [Fact]
    public async Task DisabledGatesPrecedeStopDiscoveryAndAcquisition()
    {
        _steam.Running = true;
        foreach (var (command, args, flags) in new[]
        {
            ("mode update", new[] { "bst" }, new Dictionary<string, string?> { ["yes"] = null, ["restart-steam"] = null }),
            ("plugin update", Array.Empty<string>(), new Dictionary<string, string?> { ["yes"] = null, ["restart-steam"] = null, ["install"] = null }),
            ("fix apply", new[] { "test-fix" }, new Dictionary<string, string?> { ["yes"] = null, ["appid"] = "620", ["provider"] = "online-fix.me" }),
            ("tool launch", new[] { "steamautocrack" }, new Dictionary<string, string?> { ["yes"] = null }),
        }) Assert.Equal("WORKFLOW_DISABLED", (await Run(command, args, flags)).Error!.Code);
        Assert.Equal(0, _steam.Stops); Assert.Equal(0, _steam.Starts); Assert.Equal(0, _network.Requests); Assert.Empty(_queue.Items);
    }
    [Fact]
    public async Task RunningSteamRequiresRestartFlagDryRunConsentAndStalePlansWork()
    {
        _steam.Running = true;
        Assert.Equal("STEAM_RUNNING", (await Run("mode update", ["custom"], new() { ["yes"] = null })).Error!.Code);
        Assert.Equal("STEAM_RUNNING", (await Run("plugin delete", flags: new() { ["yes"] = null })).Error!.Code);
        var dry = await Run("mode update", ["custom"], new() { ["dry-run"] = null, ["restart-steam"] = null });
        Assert.True(dry.Ok); Assert.Contains(Resources.Strings.Agent_System_ModeStartsSteam, dry.Data!.ToJsonString());
        Assert.Equal(0, _steam.Stops);
        Assert.Equal("CONSENT_REQUIRED", (await Run("mode update", ["custom"], new() { ["restart-steam"] = null })).Error!.Code);
        Assert.Equal("VERSION_CONFLICT", (await Run("mode update", ["custom"], new() { ["yes"] = null, ["restart-steam"] = null, ["if-version"] = "stale" })).Error!.Code);
        _settings.FastFetch = !_settings.FastFetch;
        Assert.Equal("PLAN_STALE", (await Run("mode update", ["custom"], new() { ["yes"] = null, ["restart-steam"] = null, ["plan-id"] = dry.Data!["plan"]!["id"]!.ToString() })).Error!.Code);
        Assert.Equal(0, _steam.Stops);
    }
    [Fact]
    public async Task GuiModeConfirmationChecksGateBeforeShutdownAndCustomStillStartsClosedSteam()
    {
        var cache = new CacheService(Path.Combine(_root, "gui-cache.json")); var gh = new GithubProxy(_http);
        var unlocker = new UnlockerService(_steam, _settings, cache, gh);
        var vm = new ModeViewModel(unlocker, new ToastService(), _steam, new CloudRedirectService(gh, _settings), operations: _modes);
        vm.InstallCommand.Execute(new ModeCardViewModel(UnlockerMode.Bst, "BST", ""));
        await vm.ConfirmInstallCommand.ExecuteAsync(null);
        Assert.Equal(0, _steam.Stops); Assert.Equal(0, _steam.Starts); Assert.False(vm.IsBusy); Assert.Equal(0, _network.Requests);
        vm.InstallCommand.Execute(new ModeCardViewModel(UnlockerMode.Custom, "Custom", ""));
        await vm.ConfirmInstallCommand.ExecuteAsync(null);
        Assert.Equal(0, _steam.Stops); Assert.Equal(1, _steam.Starts);
    }
    [Fact]
    public async Task ActualFixJobHonoursRevocationBetweenQueueAndAcquisition()
    {
        _settings.ExecutableGameFixesEnabled = true;
        var job = _fixes.CreateJob(620, "TestGame", new DenuvoFix { Id = "test", Title = "test", FixFilename = "test.zip" }, new Uri("https://uploads.online-fix.me/Game/"), "fix");
        _settings.ExecutableGameFixesEnabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.DownloadAsync(null!, null!, default));
        Assert.Equal(0, _network.Requests); Assert.Equal(0, _steam.Stops);
    }
    [Fact]
    public async Task RevertWithDisabledWorkflowRestoresOnlyRecordedFiles()
    {
        string id = "test-fix", record = ManifestJobFactory.GetFixRecordPath(_game, id);
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(Path.Combine(_game, "added.txt"), "inert file");
        File.WriteAllText(record, JsonSerializer.Serialize(new DenuvoFixRecord { FixId = id, Files = [new DenuvoFixRecordEntry { RelativePath = "added.txt", Action = "added" }] }));
        Assert.False(_settings.ExecutableGameFixesEnabled);
        var dry = await Run("fix revert", [id], new() { ["appid"] = "620", ["dry-run"] = null }); Assert.True(dry.Ok); Assert.True(File.Exists(record));
        var result = _fixes.Revert(620, id, "TestGame");
        Assert.True(result.Ok, result.Message); Assert.False(File.Exists(record)); Assert.False(File.Exists(Path.Combine(_game, "added.txt"))); Assert.Equal(0, _network.Requests);
    }
    [Fact]
    public async Task DetectedModeIsReadOnlyUntilAnExplicitMutation()
    {
        _settings.SelectedMode = null;
        byte[] bytes = [1, 2, 3, 4];
        File.WriteAllBytes(Path.Combine(_root, "OpenSteamTool.dll"), bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _network.Respond = _ => new(HttpStatusCode.OK) { Content = new StringContent($"version = \"1.0.0\"\npath = \"opensteamtool/1.0.0/OpenSteamTool.dll\"\nsha256 = \"{hash}\"") };
        var state = await Run("mode get");
        Assert.True(state.Ok); Assert.Equal("bst", state.Data!["detected_mode"]!.GetValue<string>());
        Assert.Null(_settings.SelectedMode); Assert.Equal(0, _steam.Stops);
    }
    [Fact]
    public async Task SteamSelectionIsPinnedAcrossAnAcceptedOperation()
    {
        string admitted = _steam.EffectivePath!;
        string other = Path.Combine(_root, "other-installation"); Directory.CreateDirectory(other);
        using (_steam.PinSelectedInstallation())
        {
            _settings.SteamPathOverride = other;
            await Task.Yield();
            Assert.Equal(admitted, _steam.EffectivePath);
        }
        Assert.Equal(other, _steam.EffectivePath);
        Assert.Equal(0, _steam.Stops); Assert.Equal(0, _steam.Starts);
    }
    [Fact]
    public async Task LifecycleRefusesActiveJobsAndDrainWaitsOutsideAdmission()
    {
        var done = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _facade.Jobs.Register("fake", _ => done.Task, default);
        foreach (string command in new[] { "application exit", "application restart", "update apply" })
        {
            var result = await Run(command, flags: new() { ["yes"] = null });
            Assert.Equal("JOBS_ACTIVE", result.Error!.Code); Assert.Equal(6, result.Error.ExitCode);
        }
        var waiting = Run("application exit", flags: new() { ["yes"] = null, ["drain"] = null });
        Assert.False(waiting.IsCompleted);
        Assert.True((await Run("steam get")).Ok);
        done.SetResult(Envelope.Success(new { state = "completed" }));
        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
    }
    [Fact]
    public async Task RealPipeSystemReadsDryRunReadOnlyConsentVersionsAndIdempotency()
    {
        byte[] capability = RandomNumberGenerator.GetBytes(32);
        using var host = new AgentPipeHostedService(_facade, NullLogger<AgentPipeHostedService>.Instance, capability);
        async Task<Envelope> Pipe(string command, string[]? args = null, Dictionary<string, string?>? flags = null)
        {
            string name = "LuaTools-system-test-" + Guid.NewGuid().ToString("N");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var serving = Task.Run(async () => { await pipe.WaitForConnectionAsync(deadline.Token); await host.ServeAsync(pipe, deadline.Token); });
            var reply = await AgentClient.ExchangeAsync(name, Request(command, args, flags) with { Capability = Convert.ToBase64String(capability) }, deadline.Token);
            await serving; return reply;
        }
        foreach (string command in new[] { "steam get", "mode list", "plugin get", "update get", "cloudredirect get", "fix list" }) Assert.True((await Pipe(command)).Ok, command);
        Assert.True((await Pipe("update check", flags: new() { ["dry-run"] = null })).Ok);
        Assert.Equal("READ_ONLY", (await Pipe("update check", flags: new() { ["read-only"] = null })).Error!.Code);
        Assert.Equal("READ_ONLY", (await Pipe("steam update", flags: new() { ["path"] = _root, ["read-only"] = null })).Error!.Code);
        var dry = await Pipe("steam update", flags: new() { ["path"] = _root, ["dry-run"] = null }); Assert.True(dry.Ok);
        Assert.Equal("CONSENT_REQUIRED", (await Pipe("steam update", flags: new() { ["path"] = _root })).Error!.Code);
        Assert.Equal("VERSION_CONFLICT", (await Pipe("steam update", flags: new() { ["path"] = _root, ["yes"] = null, ["if-version"] = "stale" })).Error!.Code);
        var flags = new Dictionary<string, string?> { ["path"] = _root, ["yes"] = null, ["idempotency-key"] = "system-pipe" };
        var accepted = await Pipe("steam update", flags: flags); Assert.True(accepted.Ok);
        Assert.Equal(JsonSerializer.Serialize(accepted, Protocol.Json), JsonSerializer.Serialize(await Pipe("steam update", flags: flags), Protocol.Json));
        _settings.FastFetch = !_settings.FastFetch;
        Assert.Equal("PLAN_STALE", (await Pipe("steam update", flags: new() { ["path"] = _root, ["yes"] = null, ["plan-id"] = dry.Data!["plan"]!["id"]!.ToString() })).Error!.Code);
        Assert.Equal("WORKFLOW_DISABLED", (await Pipe("mode update", ["bst"], new() { ["yes"] = null, ["restart-steam"] = null })).Error!.Code);
        _steam.Running = true;
        Assert.Equal("STEAM_RUNNING", (await Pipe("mode update", ["custom"], new() { ["yes"] = null })).Error!.Code);
        _steam.Running = false;
        var partial = await Pipe("fix revert", flags: new() { ["appid"] = "620", ["ids"] = "missing-one,missing-two", ["yes"] = null });
        Assert.Equal("PARTIAL_FAILURE", partial.Error!.Code); Assert.Equal(1, partial.Error.ExitCode); Assert.Equal(2, partial.Error.Details!["results"]!.AsArray().Count);
        Assert.Equal(0, _steam.Stops); Assert.Equal(0, _network.Requests);
    }
    public void Dispose() { _http.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
