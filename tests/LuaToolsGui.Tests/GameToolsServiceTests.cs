using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using LuaToolsGui.Services.GameTools;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The Tools page's save paths against a fake Steam folder in a temp directory. Steam process control and the
/// live helper are injected stubs: a test must never stop the machine's Steam or touch its files.
/// </summary>
public sealed class GameToolsServiceTests : IDisposable
{
    private const long App = 777;
    private const uint Account = 42;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gametools-svc-" + Guid.NewGuid().ToString("N"));
    private string Steam => Path.Combine(_root, "Steam");
    private string Backup => Path.Combine(_root, "backup");
    private string StatsFile => Path.Combine(Steam, "appcache", "stats", $"UserGameStats_{Account}_{App}.bin");
    private string CrFile => Path.Combine(Steam, "cloud_redirect", "stats", Account.ToString(), $"{App}.json");
    private string PendingFile => Path.Combine(Steam, "cloud_redirect", "pending_edits.json");
    private string LocalConfig => Path.Combine(Steam, "userdata", Account.ToString(), "config", "localconfig.vdf");

    private bool _running;
    private int _stops, _starts, _liveCalls;
    private Func<bool>? _runningOverride;
    private StatsSaveResult _live = new(StatsSaveOutcome.SavedLive);
    private Action? _onLive;
    private readonly GameToolsService _service;

    public GameToolsServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(Steam, "appcache", "stats"));
        File.WriteAllBytes(Path.Combine(Steam, "appcache", "stats", $"UserGameStatsSchema_{App}.bin"), GameToolsFilesTests.Schema());
        File.WriteAllBytes(StatsFile, GameToolsFilesTests.UserStats());
        _service = new GameToolsService(
            () => Steam,
            () => _runningOverride?.Invoke() ?? _running,
            () => { _stops++; _running = false; },
            () => { _starts++; _running = true; return true; },
            _ => Account,
            (_, _, _, _) => { _liveCalls++; _onLive?.Invoke(); return Task.FromResult(_live); },
            (_, _) => Task.CompletedTask,
            (_, _) => true,
            () => "english",
            () => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            Backup);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static GameStatsEdit Edit(Dictionary<string, bool>? a = null, Dictionary<string, double>? s = null) => new(a ?? new(), s ?? new());
    private bool[] Unlocked() => _service.Load(App).Achievements.Select(a => a.Unlocked).ToArray();

    private void ManagedByCloudRedirect()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CrFile)!);
        File.WriteAllText(CrFile, """{"stats":[{"id":1,"value":5}],"achievements":[{"stat_id":1,"bits":5,"unlock_times":[1,0,2]}]}""");
    }

    [Fact]
    public async Task SteamClosed_WritesTheCacheAfterOneBackup()
    {
        var original = File.ReadAllBytes(StatsFile);
        var snapshot = _service.Load(App);
        Assert.True(snapshot.HasSchema);
        Assert.Equal(Account, snapshot.AccountId);
        Assert.False(snapshot.CloudRedirectManaged);
        Assert.True(snapshot.CabinetSyncs);

        var result = await _service.SaveAsync(App, Edit(new() { ["ACH_A"] = false }, new() { ["kills"] = 3 }));
        Assert.Equal(StatsSaveOutcome.SavedToFile, result.Outcome);
        Assert.Equal(new[] { false, false, true, false }, Unlocked());
        Assert.Equal(3, _service.Load(App).Stats[0].Value);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Backup, Path.GetFileName(StatsFile))));

        // The backup keeps the ORIGINAL: a second edit doesn't refresh it.
        await _service.SaveAsync(App, Edit(new() { ["ACH_B"] = true }));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Backup, Path.GetFileName(StatsFile))));
        Assert.Equal(0, _liveCalls + _stops + _starts);
        Assert.False(File.Exists(PendingFile));                       // not a CloudRedirect game
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StatsFile)!, "*.tmp"));
    }

    [Fact]
    public async Task SteamRunning_GoesThroughTheHelperAndNeverTouchesTheFile()
    {
        _running = true;
        var before = File.ReadAllBytes(StatsFile);
        var result = await _service.SaveAsync(App, Edit(new() { ["ACH_A"] = false }));
        Assert.Equal(StatsSaveOutcome.SavedLive, result.Outcome);
        Assert.Equal(1, _liveCalls);
        Assert.Equal(before, File.ReadAllBytes(StatsFile));
        Assert.Equal(0, _stops);
    }

    [Fact]
    public async Task SteamRunning_ARejectedWriteIsConfirmedFromTheCacheOrReported()
    {
        _running = true;
        _live = new(StatsSaveOutcome.LiveRejected, "store-failed:8");
        Assert.Equal(new StatsSaveResult(StatsSaveOutcome.LiveRejected, "store-failed:8"), await _service.SaveAsync(App, Edit(new() { ["ACH_B"] = true })));

        // Steam recorded it after all (its cache shows the change): that counts as saved.
        _onLive = () => File.WriteAllBytes(StatsFile, GameStatsFiles.ApplyEdit(File.ReadAllBytes(StatsFile),
            GameToolsFilesTests.ParsedSchema(), Edit(new() { ["ACH_B"] = true }), DateTimeOffset.UtcNow)!);
        Assert.Equal(StatsSaveOutcome.SavedLive, (await _service.SaveAsync(App, Edit(new() { ["ACH_B"] = true }))).Outcome);
        Assert.Equal(0, _stops);
    }

    [Fact]
    public async Task CloudRedirectLock_WithSteamRunning_AsksInsteadOfStoppingSteam()
    {
        ManagedByCloudRedirect();
        _running = true;
        Assert.True(_service.Load(App).CloudRedirectManaged);
        var before = File.ReadAllBytes(StatsFile);

        var result = await _service.SaveAsync(App, Edit(new() { ["ACH_A"] = false }));
        Assert.Equal(StatsSaveOutcome.NeedsSteamClosed, result.Outcome);
        Assert.Equal(0, _liveCalls + _stops + _starts);
        Assert.Equal(before, File.ReadAllBytes(StatsFile));
        Assert.False(File.Exists(PendingFile));

        // Unlocks still go live: CloudRedirect sees those through Steam itself.
        Assert.Equal(StatsSaveOutcome.SavedLive, (await _service.SaveAsync(App, Edit(new() { ["ACH_B"] = true }))).Outcome);
    }

    [Fact]
    public async Task CloudRedirectLock_AfterConsent_StopsSteamWritesAllThreeFilesAndStartsSteam()
    {
        ManagedByCloudRedirect();
        Directory.CreateDirectory(Path.GetDirectoryName(PendingFile)!);
        File.WriteAllText(PendingFile, """{"99":{"777":[{"name":"ACH_A","stat":1,"bit":0}]}}""");
        _running = true;

        var run = await _service.SaveWithSteamClosedAsync(App, Edit(new() { ["ACH_A"] = false }));
        Assert.Equal(StatsSaveOutcome.SavedToFile, run.Result.Outcome);
        Assert.False(run.SteamRestartFailed);
        Assert.Equal((1, 1), (_stops, _starts));
        Assert.Equal(new[] { false, false, true, false }, Unlocked());

        var cr = JsonNode.Parse(File.ReadAllText(CrFile))!;
        Assert.Equal(4, (long)cr["achievements"]![0]!["bits"]!);
        Assert.Equal(4, (long)cr["stats"]![0]!["value"]!);
        var pending = JsonNode.Parse(File.ReadAllText(PendingFile))!;
        Assert.Equal("""[{"name":"ACH_A","stat":1,"bit":0}]""", pending["42"]!["777"]!.ToJsonString());
        Assert.NotNull(pending["99"]);                                   // another account's note is kept
        Assert.True(File.Exists(Path.Combine(Backup, $"cloud_redirect_{Account}_{App}.json")));
    }

    [Fact]
    public async Task AMalformedCloudRedirectFileAbortsBeforeAnyWrite()
    {
        ManagedByCloudRedirect();
        File.WriteAllText(CrFile, "{ not json");
        var before = File.ReadAllBytes(StatsFile);
        var result = await _service.SaveAsync(App, Edit(new() { ["ACH_A"] = false }));
        Assert.Equal(new StatsSaveResult(StatsSaveOutcome.Failed, GameToolsService.ErrCloudRedirectFile), result);
        Assert.Equal(before, File.ReadAllBytes(StatsFile));
        Assert.False(File.Exists(PendingFile));
    }

    [Fact]
    public void SteamStartingMidWriteCommitsNothing()
    {
        var before = File.ReadAllBytes(StatsFile);
        int checks = 0;
        _runningOverride = () => ++checks > 2;           // closed at the start, running by the commit
        var result = _service.SaveOffline(App, Edit(new() { ["ACH_A"] = false }));
        Assert.Equal(new StatsSaveResult(StatsSaveOutcome.Failed, GameToolsService.ErrSteamStarted), result);
        Assert.Equal(before, File.ReadAllBytes(StatsFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StatsFile)!, "*.tmp"));

        _runningOverride = () => true;
        Assert.Equal(GameToolsService.ErrSteamRunning, _service.SaveOffline(App, Edit(new() { ["ACH_A"] = false })).Error);
    }

    [Fact]
    public async Task NoSchemaOrAccountIsReportedNotGuessed()
    {
        File.Delete(Path.Combine(Steam, "appcache", "stats", $"UserGameStatsSchema_{App}.bin"));
        Assert.False(_service.Load(App).HasSchema);
        Assert.Equal(GameToolsService.ErrNoSchema, (await _service.SaveAsync(App, Edit(new() { ["ACH_A"] = true }))).Error);

        var noAccount = new GameToolsService(() => Steam, () => false, () => { }, () => true, _ => null,
            (_, _, _, _) => Task.FromResult(new StatsSaveResult(StatsSaveOutcome.SavedLive)), (_, _) => Task.CompletedTask,
            (_, _) => false, () => "english", () => DateTimeOffset.UtcNow, Backup);
        Assert.Null(noAccount.Load(App).AccountId);
        Assert.Equal(GameToolsService.ErrNoAccount, noAccount.SaveOffline(App, Edit(new() { ["ACH_A"] = true })).Error);
        Assert.Null(noAccount.GetPlaytimeMinutes(App));
    }

    private void WriteLocalConfig(bool bom)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalConfig)!);
        string text = "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
                      "\t\t\t\t\t\"777\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"Playtime\"\t\t\"120\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";
        File.WriteAllBytes(LocalConfig, PlaytimeConfig.Encode(text, bom));
    }

    [Fact]
    public async Task PlaytimeIsWrittenOnlyWithSteamClosed()
    {
        WriteLocalConfig(bom: true);
        var original = File.ReadAllBytes(LocalConfig);
        Assert.Equal(120, _service.GetPlaytimeMinutes(App));

        _running = true;
        Assert.Equal(GameToolsService.ErrSteamRunning, await _service.SetPlaytimeAsync(App, 600));
        Assert.Equal(original, File.ReadAllBytes(LocalConfig));
        Assert.Equal(0, _stops);

        // After the user agreed: Steam closes, the value is written, Steam starts again.
        var run = await _service.SetPlaytimeWithSteamClosedAsync(App, 600);
        Assert.Null(run.Result);
        Assert.Equal((1, 1), (_stops, _starts));
        Assert.Equal(600, _service.GetPlaytimeMinutes(App));
        Assert.Equal((byte)0xEF, File.ReadAllBytes(LocalConfig)[0]);   // Steam's BOM kept
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Backup, $"localconfig_{Account}.vdf")));

        _running = false;
        Assert.Null(await _service.SetPlaytimeAsync(App, 30));
        Assert.Equal(30, _service.GetPlaytimeMinutes(App));
        Assert.Equal((1, 1), (_stops, _starts));                       // closed Steam: no stop/start
    }

    [Fact]
    public async Task ASteamThatWillNotCloseLeavesTheFilesAlone()
    {
        WriteLocalConfig(bom: false);
        var original = File.ReadAllBytes(LocalConfig);
        var stubborn = new GameToolsService(() => Steam, () => true, () => throw new InvalidOperationException("no"), () => true,
            _ => Account, (_, _, _, _) => Task.FromResult(new StatsSaveResult(StatsSaveOutcome.SavedLive)), (_, _) => Task.CompletedTask,
            (_, _) => false, () => "english", () => DateTimeOffset.UtcNow, Backup);
        var run = await stubborn.SetPlaytimeWithSteamClosedAsync(App, 5);
        Assert.Equal(GameToolsService.ErrSteamStop, run.Result);
        Assert.Equal(original, File.ReadAllBytes(LocalConfig));
    }
}
