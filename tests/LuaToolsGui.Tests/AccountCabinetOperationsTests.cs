using System.Text.Json;
using LuaTools.Agent;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Agent;
using LuaToolsGui.Services.Cabinet;
using Xunit;
namespace LuaToolsGui.Tests;
public sealed class AccountCabinetOperationsTests
{
    private sealed class FakeCabinet : ICabinetService, ICabinetCloud
    {
        public CabinetStatus Status { get; set; } = CabinetStatus.Off;
        CabinetCloudStatus ICabinetCloud.Status => Status.Cloud;
        public event EventHandler<CabinetStatus>? StatusChanged { add { } remove { } }
        public event EventHandler<CabinetNotification>? Notification { add { } remove { } }
        event EventHandler<CabinetCloudStatus>? ICabinetCloud.StatusChanged { add { } remove { } }
        public bool FriendLabels { get; set; }
        public int Syncs, Connects;
        public Task SyncNowAsync(CancellationToken cancellationToken = default) { Syncs++; return Task.CompletedTask; }
        public void SettingsChanged() { }
        public Task ConnectAsync(string input, string? server = null, CancellationToken cancellationToken = default) { Connects++; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    [Fact]
    public async Task SharedGuiSyncPreservesEngineGates()
    {
        var fake = new FakeCabinet();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await AccountCabinetOperations.SyncAsync(fake));
        fake.Status = fake.Status with { Enabled = true, StandaloneCabinetRunning = true };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await AccountCabinetOperations.SyncAsync(fake));
        fake.Status = fake.Status with { StandaloneCabinetRunning = false };
        await AccountCabinetOperations.SyncAsync(fake);
        Assert.Equal(1, fake.Syncs);
    }
    [Fact]
    public async Task SharedConnectDoesNotEnrolCloudSavesOrTriggerSync()
    {
        var fake = new FakeCabinet { Status = CabinetStatus.Off with { Enabled = true } };
        await AccountCabinetOperations.ConnectAsync(fake, fake, "fake-pairing-code", null);
        Assert.Equal(1, fake.Connects); Assert.Equal(0, fake.Syncs);
    }
    [Fact]
    public void HumanActionReceiptContainsWaitableIdOnly()
    {
        var response = AgentCommandService.HumanActionRequired("test-job");
        Assert.Equal("HUMAN_ACTION_REQUIRED", response.Error!.Code);
        Assert.Equal("test-job", response.Error.Details!["job_id"]!.GetValue<string>());
        Assert.Equal("luatools job wait test-job", response.Error.Hint);
        Assert.DoesNotContain("token", JsonSerializer.Serialize(response, Protocol.Json));
    }
}
