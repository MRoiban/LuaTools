using System.Collections.Concurrent;
using System.IO;
using CabinetEngine;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Cabinet state files damaged by a crash or power loss (field case: a zero-filled
/// cloud-state.json beside File.Replace's ~RF backup). Temporary directories and the engine's own
/// localhost fake server only; each scenario throws "FAIL: ..." from CloudSelfTest.Check.</summary>
public sealed class CabinetStateRecoveryTests
{
    [Fact] public void ZeroFilledJournalIsRecoveredFromTheReplaceBackup() => CloudSelfTest.JournalRecoveredFromReplaceBackup();
    [Fact] public void JournalIsRecoveredFromTheNewestAtomicTempFile() => CloudSelfTest.JournalRecoveredFromNewestTempFile();
    [Fact] public void TruncatedJournalIsQuarantinedAndRebaselined() => CloudSelfTest.TruncatedJournalQuarantined();
    [Fact] public void UnrecoverableJournalRebaselinesWithoutDoubleCounting() => CloudSelfTest.DamagedJournalRebaselinesWithoutDoubleCounting();
    [Fact] public void ZeroFilledSettingsStartDisconnectedThenConnectWorks() => CloudSelfTest.DamagedSettingsStartDisconnectedThenConnect();
    [Fact] public void PlaytimeLedgerIsRecoveredOrResetWithAMarker() => CloudSelfTest.LedgerRecoveredOrReset();
    [Fact] public void LedgerResetNeverPushesImportedMinutesAsOwn() => CloudSelfTest.LedgerResetNeverPushesImportedMinutes();
    [Fact] public void DurableWriterLeavesNoTemporaryFiles() => CloudSelfTest.DurableWritesLeaveNoTemporaryFiles();

    [Fact]
    public void MissingFileWithoutLeftoversStartsFreshQuietly()
    {
        InTemp(dir =>
        {
            var logs = new List<string>();
            Assert.Null(StateFile.Load(Path.Combine(dir, "cloud-state.json"), t => t, logs.Add, out var result));
            Assert.Equal(StateLoad.Missing, result);
            Assert.Empty(logs);
            Assert.Empty(Directory.GetFiles(dir));
        });
    }

    [Fact]
    public void InterruptedReplaceWithoutTheFileIsRecoveredFromItsLeftover()
    {
        InTemp(dir =>
        {
            // ReplaceFile moved the old file aside, then the power went: only the ~RF backup is left.
            string path = Path.Combine(dir, "cloud-state.json");
            File.WriteAllText(path + "~RF5c1.TMP", "{\"apps\":{}}");
            var logs = new List<string>();
            Assert.Equal("{\"apps\":{}}", StateFile.Load(path, t => t.StartsWith("{") ? t : null, logs.Add, out var result));
            Assert.Equal(StateLoad.Recovered, result);
            Assert.Equal("{\"apps\":{}}", File.ReadAllText(path));
            Assert.Contains(logs, l => l.Contains("missing after an interrupted write"));
        });
    }

    [Fact]
    public void LockedFileIsNotTreatedAsDamaged()
    {
        InTemp(dir =>
        {
            string path = Path.Combine(dir, "cloud-state.json");
            File.WriteAllText(path, "{\"apps\":{}}");
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.ThrowsAny<IOException>(() => StateFile.Load(path, t => t, null, out _));
            Assert.Equal(new[] { "cloud-state.json" }, Directory.GetFiles(dir).Select(Path.GetFileName));
        });
    }

    [Fact]
    public void QuarantineReachesTheHostWarningWithoutContents()
    {
        var warnings = new ConcurrentQueue<string>();
        var previous = StateFile.Warning;
        StateFile.Warning = warnings.Enqueue;
        try
        {
            InTemp(dir =>
            {
                string path = Path.Combine(dir, "cloud.json");
                File.WriteAllText(path, "{\"token\":\"secret-token-value\",\"deviceId\":");
                Assert.Null(StateFile.Load(path, t => (string?)null, null, out var result));
                Assert.Equal(StateLoad.Damaged, result);
                Assert.Contains(warnings, w => w.Contains("cloud.json was damaged") && w.Contains("cloud.json.corrupt-"));
                Assert.DoesNotContain(warnings, w => w.Contains("secret-token-value"));
                Assert.Single(Directory.GetFiles(dir, "cloud.json.corrupt-*"));
            });
        }
        finally { StateFile.Warning = previous; }
    }

    [Fact]
    public void SettingsExplainsADamagedConnectionUntilReconnected()
    {
        var cloud = new StatusCloud { Status = CabinetCloudStatus.Disconnected with { ConnectionDamaged = true } };
        var settings = new SettingsService(new AppSettings { CabinetEnabled = true });
        var vm = new SettingsViewModel(settings, new AuthService(), new SteamService(settings),
            new HubcapService(), new FakeCabinetService(), cloud);
        Assert.Equal(Resources.Strings.CabinetCloud_ConnectionDamaged, vm.CloudNotice);
        cloud.Status = new CabinetCloudStatus(true, "Alex", "cabinet.mc2.onl", null);
        Assert.Null(vm.CloudNotice);
        cloud.Status = CabinetCloudStatus.Disconnected;
        Assert.Null(vm.CloudNotice);
    }

    private static void InTemp(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), "LuaTools-state-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { test(dir); }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class StatusCloud : ICabinetCloud
    {
        public CabinetCloudStatus Status { get; set; } = CabinetCloudStatus.Disconnected;
        public event EventHandler<CabinetCloudStatus>? StatusChanged { add { } remove { } }
        public bool FriendLabels { get; set; }
        public Task ConnectAsync(string input, string? server = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SyncNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
