using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using LuaToolsGui.Services;
using LuaToolsGui.Services.GameTools;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The Tools tabs' staging and the rule that Steam is only closed after the user confirms: declining the
/// overlay writes nothing. Runs on a real dispatcher thread (the achievement list is a WPF collection view).
/// </summary>
public sealed class GameToolsViewModelTests : IDisposable
{
    private const long App = 777;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gametools-vm-" + Guid.NewGuid().ToString("N"));
    private string Steam => Path.Combine(_root, "Steam");
    private string StatsFile => Path.Combine(Steam, "appcache", "stats", $"UserGameStats_42_{App}.bin");
    private string LocalConfig => Path.Combine(Steam, "userdata", "42", "config", "localconfig.vdf");
    private bool _running, _cabinet;
    private int _stops, _starts, _live;

    public GameToolsViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(Steam, "appcache", "stats"));
        File.WriteAllBytes(Path.Combine(Steam, "appcache", "stats", $"UserGameStatsSchema_{App}.bin"), GameToolsFilesTests.Schema());
        File.WriteAllBytes(StatsFile, GameToolsFilesTests.UserStats());
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private GameToolsViewModel NewViewModel()
    {
        var service = new GameToolsService(() => Steam, () => _running, () => { _stops++; _running = false; }, () => { _starts++; _running = true; return true; },
            _ => 42, (_, _, _, _) => { _live++; return Task.FromResult(new StatsSaveResult(StatsSaveOutcome.SavedLive)); },
            (_, _) => Task.CompletedTask, (_, _) => _cabinet, () => "english", () => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            Path.Combine(_root, "backup"));
        var icons = new AchievementIconCache(new HttpClient(new NotFound()), Path.Combine(_root, "icons"));
        var vm = new GameToolsViewModel(service, icons, new ToastService());
        vm.SetGame(App);
        return vm;
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static async Task OnUi(Func<Task> body)
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var ui = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(ui));
            ready.SetResult(ui);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var dispatcher = await ready.Task;
        try { await dispatcher.InvokeAsync(body).Task.Unwrap(); }
        finally { dispatcher.InvokeShutdown(); }
    }

    [Fact]
    public Task StagesEditsAndShowsTheCabinetNoteOnlyForPendingLocks() => OnUi(async () =>
    {
        _cabinet = true;
        var vm = NewViewModel();
        await vm.Reload();
        Assert.Equal(4, vm.Achievements.Count);
        Assert.Equal(4, vm.Stats.Count);
        Assert.False(vm.HasChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.Achievements[3].CanToggle);                    // server-only achievement

        vm.Achievements[0].Unlocked = false;
        Assert.True(vm.HasChanges);
        Assert.True(vm.ShowCabinetNote);
        Assert.False(vm.ShowCloudRedirectNote);
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.Achievements[0].Unlocked = true;                            // back to Steam's state
        Assert.False(vm.ShowCabinetNote);
        Assert.False(vm.HasChanges);

        vm.AchievementSearchText = "second";
        vm.UnlockAllCommand.Execute(null);                             // only the row the search shows
        Assert.Equal(new[] { true, true, true, false }, vm.Achievements.Select(a => a.Unlocked));
        vm.LockAllCommand.Execute(null);
        Assert.Equal(new[] { true, false, true, false }, vm.Achievements.Select(a => a.Unlocked));

        vm.Stats[0].Text = "1.5";                                      // an int stat: not valid
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.Stats[0].Text = "12";
        Assert.True(vm.SaveCommand.CanExecute(null));

        await vm.SaveCommand.ExecuteAsync(null);                       // Steam closed: written to the file
        Assert.Equal(0, _live + _stops);
        var reloaded = NewViewModel();
        await reloaded.Reload();
        Assert.Equal(12, reloaded.Stats[0].Info.Value);
        Assert.Equal(new[] { true, false, true, false }, reloaded.Achievements.Select(a => a.Unlocked));
    });

    [Fact]
    public Task CloudRedirectLockAsksFirstAndWritesNothingWhenDeclined() => OnUi(async () =>
    {
        string cr = Path.Combine(Steam, "cloud_redirect", "stats", "42", $"{App}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cr)!);
        File.WriteAllText(cr, """{"stats":[],"achievements":[{"stat_id":1,"bits":5,"unlock_times":[1,0,2]}]}""");
        _running = true;
        var vm = NewViewModel();
        await vm.Reload();
        var before = File.ReadAllBytes(StatsFile);

        vm.Achievements[0].Unlocked = false;
        Assert.True(vm.ShowCloudRedirectNote);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirming);
        Assert.Equal(0, _stops + _live);

        vm.CancelConfirmCommand.Execute(null);                         // declined: nothing happens
        Assert.False(vm.IsConfirming);
        Assert.Equal(before, File.ReadAllBytes(StatsFile));
        Assert.False(File.Exists(Path.Combine(Steam, "cloud_redirect", "pending_edits.json")));
        Assert.Equal(0, _stops);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirming);
        await vm.AcceptConfirmCommand.ExecuteAsync(null);              // accepted: Steam closed, written, restarted
        Assert.Equal((1, 1), (_stops, _starts));
        Assert.True(File.Exists(Path.Combine(Steam, "cloud_redirect", "pending_edits.json")));
        Assert.False(vm.Achievements[0].Unlocked);
        Assert.False(vm.HasChanges);
    });

    [Fact]
    public Task PlaytimeWithSteamRunningAsksBeforeClosingIt() => OnUi(async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalConfig)!);
        File.WriteAllText(LocalConfig, "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n");
        _running = true;
        var vm = NewViewModel();
        await vm.Reload();
        Assert.False(vm.SavePlaytimeCommand.CanExecute(null));         // nothing changed yet
        vm.PlaytimeHours = "abc";
        Assert.True(vm.PlaytimeInvalid);
        vm.PlaytimeHours = "2.5";
        Assert.True(vm.SavePlaytimeCommand.CanExecute(null));

        var before = File.ReadAllBytes(LocalConfig);
        await vm.SavePlaytimeCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirming);
        vm.CancelConfirmCommand.Execute(null);
        Assert.Equal(before, File.ReadAllBytes(LocalConfig));
        Assert.Equal(0, _stops);

        await vm.SavePlaytimeCommand.ExecuteAsync(null);
        await vm.AcceptConfirmCommand.ExecuteAsync(null);
        Assert.Equal((1, 1), (_stops, _starts));
        Assert.Equal(150, PlaytimeConfig.GetMinutes(File.ReadAllText(LocalConfig), App));
    });

    [Fact]
    public void ParsesHoursAndLocalizesErrorCodes()
    {
        Assert.Equal(90, GameToolsViewModel.ParseHours("1.5"));
        Assert.Equal(0, GameToolsViewModel.ParseHours("0"));
        Assert.Null(GameToolsViewModel.ParseHours("-1"));
        Assert.Null(GameToolsViewModel.ParseHours("lots"));
        Assert.Equal(LuaToolsGui.Resources.Strings.Tools_Err_SteamStarted, GameToolsViewModel.Describe(GameToolsService.ErrSteamStarted));
        Assert.Equal(string.Format(LuaToolsGui.Resources.Strings.Tools_Err_Refused, "ACH_A, kills"), GameToolsViewModel.Describe("refused:ACH_A, kills"));
        Assert.Equal(LuaToolsGui.Resources.Strings.Tools_Err_Store + " (8)", GameToolsViewModel.Describe("store-failed:8"));
        Assert.Equal("Access to the path 'C:\\x' is denied.", GameToolsViewModel.Describe("Access to the path 'C:\\x' is denied."));
        Assert.Equal("boom", GameToolsViewModel.Describe("exception: boom"));
    }
}
