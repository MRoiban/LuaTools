using System.Globalization;
using System.IO;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

internal sealed class FakeCabinetService : ICabinetService
{
    public CabinetStatus Status { get; private set; } = CabinetStatus.Off;
    public event EventHandler<CabinetStatus>? StatusChanged;
    public event EventHandler<CabinetNotification>? Notification;
    public int Changes { get; private set; }
    public int Syncs { get; private set; }
    public TaskCompletionSource? PendingSync { get; set; }
    public void Publish(CabinetStatus status) { Status = status; StatusChanged?.Invoke(this, status); }
    public void Notify(CabinetNotification notification) => Notification?.Invoke(this, notification);
    public Task SyncNowAsync(CancellationToken cancellationToken = default) { Syncs++; return PendingSync?.Task ?? Task.CompletedTask; }
    public void SettingsChanged() => Changes++;
}

public sealed class CabinetViewModelTests
{
    private static CabinetGame Game(string id, bool playing = false, DateTimeOffset? last = null) => new(id, "Game " + id, 3, 6, 2.5, last, playing, playing, 0, true);
    [Fact]
    public void MapsOffEmptySyncingAndPausedStates()
    {
        var service = new FakeCabinetService();
        using var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()));
        Assert.True(vm.IsOff); Assert.False(vm.ShowChips); Assert.False(vm.CanSync);
        service.Publish(CabinetStatus.Off with { Enabled = true });
        Assert.True(vm.IsEmpty); Assert.True(vm.CanSync); Assert.True(vm.ShowChips);
        service.Publish(service.Status with { IsSyncing = true });
        Assert.True(vm.IsSyncing); Assert.False(vm.SyncNowCommand.CanExecute(null));
        service.Publish(service.Status with { IsSyncing = false, StandaloneCabinetRunning = true });
        Assert.True(vm.IsPaused); Assert.False(vm.CanSync);
    }
    [Fact]
    public void SortsPlayingFirstThenMostRecentAndMapsBadges()
    {
        var service = new FakeCabinetService();
        var now = DateTimeOffset.Now;
        service.Publish(CabinetStatus.Off with { Enabled = true, Games = [Game("1", last: now), Game("2", true, now.AddDays(-1)), Game("3") with { HasAchievementList = false, AchievementsTotal = null, AchievementsUnlocked = null, PendingPlaytimeMinutes = 22 }] });
        using var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()));
        Assert.True(vm.HasGames); Assert.False(vm.IsEmpty);
        Assert.Equal(new[] { "2", "1", "3" }, vm.Games.Select(g => g.Game.AppId));
        Assert.True(vm.Games[0].HasPlayingBadge); Assert.Equal(50, vm.Games[0].Progress);
        Assert.True(vm.Games[2].HasPendingBadge); Assert.True(vm.Games[2].HasSchemaBadge);
        Assert.False(vm.Games[2].HasAchievements); Assert.Equal(0, vm.Games[2].Progress);
    }
    [Fact]
    public void TurnOnPersistsAndNotifiesEngine()
    {
        var service = new FakeCabinetService(); var settings = new SettingsService(new AppSettings());
        using var vm = new CabinetViewModel(service, settings);
        vm.TurnOnCommand.Execute(null);
        Assert.True(settings.CabinetEnabled); Assert.Equal(1, service.Changes);
    }
    [Fact]
    public async Task ManualSyncDisablesButtonUntilServiceCompletes()
    {
        var service = new FakeCabinetService { PendingSync = new TaskCompletionSource() };
        service.Publish(CabinetStatus.Off with { Enabled = true });
        using var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()));
        var task = vm.SyncNowCommand.ExecuteAsync(null);
        Assert.True(vm.IsSyncing); Assert.False(vm.CanSync); Assert.Equal(1, service.Syncs);
        service.PendingSync.SetResult(); await task;
        Assert.False(vm.IsSyncing); Assert.True(vm.CanSync);
    }
    [Fact]
    public async Task BackgroundStatusEventUpdatesStateWithoutAnApplicationDispatcher()
    {
        var service = new FakeCabinetService();
        using var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()));
        await Task.Run(() => service.Publish(CabinetStatus.Off with { Enabled = true, SteamRunning = true }));
        Assert.True(vm.IsEmpty); Assert.True(vm.Status.SteamRunning);
    }
    [Fact]
    public void DisposeUnsubscribesFromStatus()
    {
        var service = new FakeCabinetService(); var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()));
        vm.Dispose(); service.Publish(CabinetStatus.Off with { Enabled = true }); Assert.True(vm.IsOff);
    }
    [Fact]
    public async Task CloudSavesLineFollowsCloudRedirectAndOffersToTurnItOn()
    {
        var service = new FakeCabinetService();
        service.Publish(CabinetStatus.Off with { Enabled = true });
        using var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()));
        var state = CabinetCloudSaves.Off; int offers = 0;
        vm.CloudSavesProvider = () => state;
        vm.RequestEnableCloudSaves = () => { offers++; state = CabinetCloudSaves.Active; return Task.CompletedTask; };
        vm.RefreshCloudSaves();
        Assert.False(vm.ShowCloudSaves); // Cabinet Cloud isn't connected yet

        service.Publish(service.Status with { Cloud = new CabinetCloudStatus(true, "Alex", "cabinet.mc2.onl", null) });
        Assert.True(vm.ShowCloudSaves); Assert.True(vm.CanEnableCloudSaves); Assert.False(vm.CloudSavesActive);
        Assert.Equal(Resources.Strings.Cabinet_Saves_Off, vm.CloudSavesLabel);

        await vm.EnableCloudSavesCommand.ExecuteAsync(null);
        Assert.Equal(1, offers);
        Assert.True(vm.CloudSavesActive); Assert.False(vm.CanEnableCloudSaves);
        Assert.Equal(string.Format(Resources.Strings.Cabinet_Saves_Active, "cabinet.mc2.onl"), vm.CloudSavesLabel);

        state = CabinetCloudSaves.Unavailable; vm.RefreshCloudSaves();
        Assert.True(vm.ShowCloudSaves); Assert.False(vm.CanEnableCloudSaves);
        Assert.Equal(Resources.Strings.Cabinet_Saves_Unavailable, vm.CloudSavesLabel);

        service.Publish(service.Status with { Cloud = CabinetCloudStatus.Disconnected });
        Assert.False(vm.ShowCloudSaves);
    }
    [Fact]
    public void CloudSavesProviderFailureHidesTheLine()
    {
        var service = new FakeCabinetService();
        service.Publish(CabinetStatus.Off with { Enabled = true, Cloud = new CabinetCloudStatus(true, "Alex", "cabinet.mc2.onl", null) });
        using var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()));
        vm.CloudSavesProvider = () => throw new IOException("config locked");
        vm.RefreshCloudSaves();
        Assert.False(vm.ShowCloudSaves);
    }
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void RelativeTimesUseLocalizedResources(string language)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var now = DateTimeOffset.Now;
            Assert.Equal(Resources.Strings.Cabinet_Never, CabinetGameViewModel.FormatLastSync(null, now));
            Assert.Equal(Resources.Strings.Cabinet_JustNow, CabinetGameViewModel.FormatLastSync(now, now));
            Assert.Equal(string.Format(Resources.Strings.Cabinet_MinutesAgo, 5), CabinetGameViewModel.FormatLastSync(now.AddMinutes(-5), now));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}
