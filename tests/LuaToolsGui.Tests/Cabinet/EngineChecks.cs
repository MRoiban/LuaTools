using System.IO;
using LuaToolsGui.Services.Cabinet;
using Xunit;

namespace LuaToolsGui.Tests.Cabinet;

public sealed class EngineChecks
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "luatools-cabinet-test-" + Guid.NewGuid().ToString("N"));
        public CabinetPaths Paths { get; }
        public string? Account { get; set; } = "123";
        public Fixture()
        {
            Paths = new CabinetPaths
            {
                SteamRoot = Path.Combine(Root, "steam"), DataRoot = Path.Combine(Root, "data"),
                OnlineFixRoot = Path.Combine(Root, "onlinefix"), StandaloneDataRoot = Path.Combine(Root, "standalone"),
                SteamRunning = () => false, StandaloneRunning = () => false, AccountId = () => Account,
                SteamCounting = _ => false, RunningApps = () => new HashSet<string>()
            };
            Directory.CreateDirectory(Root);
        }
        public string Put(string relative, string text)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        public string Stats => Path.Combine(Paths.SteamRoot!, "appcache", "stats");
        public void Schema()
        {
            Directory.CreateDirectory(Stats);
            var game = Sub("100", new KV(1, "gamename", "Fixture Game"), Sub("stats",
                Sub("1", Sub("bits", Sub("0", new KV(1, "name", "WIN"), new KV(2, "bit", 0)))),
                Sub("2", new KV(1, "name", "SCORE"), new KV(1, "type", "INT")),
                Sub("3", new KV(1, "name", "RATE"), new KV(1, "type", "FLOAT"))));
            File.WriteAllBytes(Path.Combine(Stats, "UserGameStatsSchema_100.bin"), Sub("", game).Write());
            Put("onlinefix/100/Stats/Achievements.ini", "[WIN]\nachieved=true\ntimestamp=1700000000\n[UNKNOWN]\nachieved=true");
            Put("onlinefix/100/Stats/Stats.ini", "[Stats]\nSCORE=42\nRATE=1.5\nUNKNOWN=666");
        }
        public string Game(string id = "100")
        {
            var dir = Path.Combine(Root, "steam", "steamapps", "common", "Fixture Game");
            Put("steam/steamapps/common/Fixture Game/OnlineFix.ini", "[Main]\nRealAppId=" + id + "\nFakeAppId=480\n[Interfaces]\nUGC=true");
            Put("steam/steamapps/common/Fixture Game/OnlineFix64.dll", "inert fixture");
            Directory.CreateDirectory(Path.Combine(Root, "steam", "steamapps", "workshop", "content", "100", "999"));
            return dir;
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(full), ignoreCase: true);
            Assert.StartsWith("luatools-cabinet-test-", Path.GetFileName(full));
            DeleteOwnedTree(full);
        }
        private static void DeleteOwnedTree(string root)
        {
            foreach (var entry in Directory.GetFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry);
                    else DeleteOwnedTree(entry);
                }
                else File.Delete(entry);
            }
            Directory.Delete(root);
        }
    }
    private sealed class Settings : ICabinetSettings
    {
        public bool CabinetEnabled { get; set; }
        public bool CabinetLivePlaytime { get; set; }
        public bool CabinetFetchSchemas { get; set; }
        public bool CabinetWorkshopLinks { get; set; }
    }
    private static KV Sub(string key, params KV[] children) => new(0, key, children.ToList());

    [Fact]
    public void BinaryKeyValuesRoundTripsAllSupportedTypes()
    {
        var root = Sub("", Sub("game", new KV(1, "name", "é\\\"fixture"), new KV(2, "int", -42),
            new KV(3, "float", 1.5f), new KV(7, "unsigned", ulong.MaxValue), new KV(10, "signed", long.MinValue),
            new KV(4, "pointer", 9), new KV(6, "color", 123)));
        var bytes = root.Write();
        Assert.Equal(bytes, KV.Read(bytes).Write());
    }
    [Theory]
    [InlineData(new byte[] { 0, 65 })]
    [InlineData(new byte[] { 2, 65, 0, 1 })]
    [InlineData(new byte[] { 0, 65, 0 })]
    public void TruncatedBinaryKeyValuesAreRefused(byte[] bytes) => Assert.Throws<InvalidDataException>(() => KV.Read(bytes));

    [Fact]
    public void SyncPreservesUnknownNodesAndOriginalBeforeFirstChange()
    {
        using var fixture = new Fixture();
        fixture.Schema();
        using var context = fixture.Paths.Enter();
        var file = Path.Combine(fixture.Stats, "UserGameStats_123_100.bin");
        var original = Sub("", Sub("cache", new KV(1, "unknown", "preserve"))).Write();
        File.WriteAllBytes(file, original);
        var result = Syncer.Sync("100", "123");
        Assert.Equal(1, result.NewAchievements);
        Assert.Equal(2, result.StatsChanged);
        var cache = KV.Read(File.ReadAllBytes(file)).Children[0];
        Assert.Equal("preserve", cache.Get("unknown").Str());
        Assert.Equal(1, cache.Get("1").Get("data").Value);
        Assert.Equal(42, cache.Get("2").Get("data").Value);
        Assert.Equal(1.5f, cache.Get("3").Get("data").Value);
        var backup = Path.Combine(fixture.Paths.DataRoot, "backup", Path.GetFileName(file));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(0, Syncer.Sync("100", "123").NewAchievements);
        Assert.Equal(original, File.ReadAllBytes(backup));
    }
    [Theory]
    [InlineData("../100")]
    [InlineData("100/../../elsewhere")]
    [InlineData("100:stream")]
    [InlineData("000100")]
    [InlineData("4294967296")]
    public void MaliciousIdsCannotEscapeOnlineFixOrSteam(string id)
    {
        using var fixture = new Fixture();
        using var context = fixture.Paths.Enter();
        Assert.Throws<InvalidDataException>(() => Syncer.Sync(id, "123"));
        Assert.False(Directory.Exists(fixture.Paths.DataRoot));
    }
    [Fact]
    public void NonfiniteAndOverflowIniStatsAreIgnored()
    {
        using var fixture = new Fixture(); fixture.Schema();
        fixture.Put("onlinefix/100/Stats/Stats.ini", "[Stats]\nRATE=NaN\nSCORE=9223372036854775807");
        using var context = fixture.Paths.Enter();
        var result = Syncer.Sync("100", "123");
        Assert.Equal(0, result.StatsChanged);
    }
    [Fact]
    public void JunctionInStatsPathIsRefusedBeforeReadOrWrite()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(target);
        var app = Path.Combine(fixture.Paths.OnlineFixRoot, "100"); Directory.CreateDirectory(app);
        WorkshopJunction.Create(Path.Combine(app, "Stats"), target);
        using var context = fixture.Paths.Enter();
        Assert.Throws<InvalidDataException>(() => Syncer.Sync("100", "123"));
        Assert.Empty(Directory.GetFiles(target));
    }
    [Theory]
    [InlineData("../mods")]
    [InlineData("C:/mods")]
    [InlineData("mods/CON")]
    [InlineData("mods /inner")]
    [InlineData("mods:stream")]
    public void WorkshopProfileTraversalAndWindowsAliasesAreRefused(string path)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["100"] = new { name = "Fixture", modFolder = path } });
        Assert.Throws<InvalidDataException>(() => WorkshopEngine.ParseProfiles(json));
    }
    [Fact]
    public void WorkshopNeverAdoptsOrRemovesUnownedJunction()
    {
        using var fixture = new Fixture(); var game = fixture.Game();
        var content = Path.Combine(fixture.Paths.SteamRoot!, "steamapps", "workshop", "content", "100");
        var destination = Path.Combine(game, "Workshop");
        WorkshopJunction.Create(destination, content);
        var engine = new WorkshopEngine(fixture.Paths.DataRoot, _ => { });
        engine.Scan(new[] { fixture.Paths.SteamRoot! }, new HashSet<string>(), false);
        Assert.False(File.Exists(Path.Combine(fixture.Paths.DataRoot, "workshop-links.json")));
        Directory.Delete(Path.Combine(content, "999")); Directory.Delete(content);
        engine.Scan(new[] { fixture.Paths.SteamRoot! }, new HashSet<string>(), false);
        Assert.Equal(content, WorkshopJunction.Target(destination), ignoreCase: true);
    }
    [Fact]
    public void WorkshopOnlyRemovesConfirmedOwnedMissingTargets()
    {
        using var fixture = new Fixture(); var game = fixture.Game();
        var engine = new WorkshopEngine(fixture.Paths.DataRoot, _ => { });
        engine.Scan(new[] { fixture.Paths.SteamRoot! }, new HashSet<string>(), false);
        var destination = Path.Combine(game, "Workshop");
        var content = WorkshopJunction.Target(destination);
        Assert.NotNull(content);
        var journal = File.ReadAllText(Path.Combine(fixture.Paths.DataRoot, "workshop-links.json"));
        Assert.Contains("\"Created\":true", journal);
        Directory.Delete(Path.Combine(content!, "999")); Directory.Delete(content!);
        engine.Scan(new[] { fixture.Paths.SteamRoot! }, new HashSet<string>(), false);
        Assert.False(WorkshopEngine.Exists(destination));
    }
    [Fact]
    public void CorruptOwnershipJournalDisablesWorkshopWrites()
    {
        using var fixture = new Fixture(); var game = fixture.Game();
        fixture.Put("data/workshop-links.json", "malformed");
        var engine = new WorkshopEngine(fixture.Paths.DataRoot, _ => { });
        engine.Scan(new[] { fixture.Paths.SteamRoot! }, new HashSet<string>(), false);
        Assert.False(Directory.Exists(Path.Combine(game, "Workshop")));
    }
    [Fact]
    public void WorkshopDefersRealAndFakeRunningGames()
    {
        using var fixture = new Fixture(); var game = fixture.Game();
        var engine = new WorkshopEngine(fixture.Paths.DataRoot, _ => { });
        engine.Scan(new[] { fixture.Paths.SteamRoot! }, new HashSet<string> { "480" }, false);
        Assert.False(Directory.Exists(Path.Combine(game, "Workshop")));
    }
    [Fact]
    public void StateImportCopiesOnlyNonSpendableStateAndBackups()
    {
        using var fixture = new Fixture();
        var ledger = fixture.Put("standalone/playtime.txt", "100|120|0|0|0|0|0|0|0|0\n");
        fixture.Put("standalone/state.txt", "100=123|fingerprint");
        fixture.Put("standalone/sync-times.txt", "100|1234");
        fixture.Put("standalone/backup/original.bin", "backup");
        fixture.Put("standalone/workshop-links.json", "[]");
        fixture.Put("standalone/Cabinet.exe", "inert");
        fixture.Put("standalone/cloud.json", "secret fixture");
        fixture.Paths.ImportStandalone();
        Assert.False(File.Exists(Path.Combine(fixture.Paths.DataRoot, "playtime.txt")));
        Assert.Equal("100=123|fingerprint", File.ReadAllText(Path.Combine(fixture.Paths.DataRoot, "state.txt")));
        Assert.True(File.Exists(ledger));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.DataRoot, "backup", "original.bin")));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.DataRoot, "Cabinet.exe")));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.DataRoot, "cloud.json")));
        fixture.Paths.ImportStandalone();
    }
    [Fact]
    public void ConflictingImportDoesNotOverwriteOrMarkCompleted()
    {
        using var fixture = new Fixture();
        fixture.Put("standalone/state.txt", "standalone"); fixture.Put("data/state.txt", "fork");
        Assert.Throws<InvalidDataException>(() => fixture.Paths.ImportStandalone());
        Assert.Equal("fork", File.ReadAllText(Path.Combine(fixture.Paths.DataRoot, "state.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.DataRoot, "standalone-imported.txt")));
    }
    [Fact]
    public async Task DisabledMeansNoFilesProbesOrNotifications()
    {
        using var fixture = new Fixture();
        var paths = new CabinetPaths
        {
            DataRoot = fixture.Paths.DataRoot,
            SteamRunning = () => throw new Exception("disabled probe"),
            StandaloneRunning = () => throw new Exception("disabled probe")
        };
        using var service = new CabinetService(new Settings(), paths, _ => { });
        int notifications = 0; service.Notification += (_, _) => notifications++;
        await service.StartAsync(default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.SyncNowAsync()));
        await service.StopAsync(default);
        Assert.Equal(CabinetStatus.Off, service.Status);
        Assert.Equal(0, notifications);
        Assert.False(Directory.Exists(paths.DataRoot));
    }
    [Fact]
    public async Task ConcurrentSyncCreditsOnceAndReportsFixtureGames()
    {
        using var fixture = new Fixture(); fixture.Schema();
        using var service = new CabinetService(new Settings { CabinetEnabled = true }, fixture.Paths, _ => { });
        int unlockEvents = 0; service.Notification += (_, n) => { if (n.Kind == CabinetNotificationKind.AchievementsSynced) unlockEvents++; };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.SyncNowAsync()));
        Assert.Equal(1, unlockEvents);
        var game = Assert.Single(service.Status.Games);
        Assert.Equal("Fixture Game", game.Name); Assert.Equal(1, game.AchievementsUnlocked);
        Assert.False(service.Status.IsSyncing); Assert.NotNull(game.LastSynced);
    }
    [Fact]
    public async Task StandaloneCabinetPausesBeforeImportOrSteamProbe()
    {
        using var fixture = new Fixture();
        var paths = new CabinetPaths { DataRoot = fixture.Paths.DataRoot, StandaloneRunning = () => true,
            SteamRunning = () => throw new Exception("paused probe") };
        using var service = new CabinetService(new Settings { CabinetEnabled = true }, paths, _ => { });
        await service.SyncNowAsync();
        Assert.True(service.Status.StandaloneCabinetRunning);
        Assert.False(Directory.Exists(paths.DataRoot));
    }
    [Fact]
    public void UnsignedDllIsRejectedWithoutLoadingIt()
    {
        using var fixture = new Fixture();
        var dll = fixture.Put("unsigned-steamclient64.dll", "inert unsigned DLL fixture");
        using var locked = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read);
        var canonical = ValveSignature.FinalPath(locked.SafeFileHandle);
        Assert.False(ValveSignature.Verify(canonical, locked.SafeFileHandle));
        Assert.Throws<InvalidOperationException>(() => new SteamSession("100", _ => { }));
    }
    [Fact]
    public void StandalonePendingMinutesRemainExclusivelyOwnedByStandalone()
    {
        using var fixture = new Fixture();
        fixture.Put("standalone/playtime.txt", "100|120|0|0|0|0|0|0|0|0\n");
        var config = fixture.Put("steam/userdata/123/config/localconfig.vdf",
            "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"100\" { \"Playtime\" \"10\" \"Playtime2wks\" \"10\" \"LastPlayed\" \"0\" } } } } }\n}\n");
        fixture.Paths.ImportStandalone();
        using var context = fixture.Paths.Enter();
        var tracker = new Playtime(Path.Combine(fixture.Paths.DataRoot, "playtime.txt"), _ => { });
        tracker.Tick("123"); tracker.Tick("123");
        Assert.Equal("10", new VdfText(File.ReadAllText(config)).GetAppValue("100", "Playtime"));
        // Simulate standalone applying its own two minutes after LuaTools: 10 -> 12, never 14.
        var standalone = new Playtime(Path.Combine(fixture.Paths.StandaloneDataRoot, "playtime.txt"), _ => { });
        standalone.Tick("123");
        Assert.Equal("12", new VdfText(File.ReadAllText(config)).GetAppValue("100", "Playtime"));
        tracker.Tick("123"); standalone.Tick("123");
        Assert.Equal("12", new VdfText(File.ReadAllText(config)).GetAppValue("100", "Playtime"));
        Assert.Equal(0, tracker.PendingMinutes());
        Assert.Equal(0, standalone.PendingMinutes());
    }

    [Fact]
    public async Task RapidSettingsChangesRetireLoopsAndBecomeIdle()
    {
        using var fixture = new Fixture(); fixture.Schema();
        var settings = new Settings { CabinetEnabled = true };
        using var service = new CabinetService(settings, fixture.Paths, _ => { });
        await service.StartAsync(default);
        for (var i = 0; i < 10; i++) service.SettingsChanged();
        settings.CabinetEnabled = false;
        service.SettingsChanged();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StopAsync(timeout.Token);
        Assert.Equal(CabinetStatus.Off, service.Status);
    }

    [Fact]
    public void ImportedJunctionsCannotRedirectBackupCopies()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateDirectory(fixture.Paths.StandaloneDataRoot);
        WorkshopJunction.Create(Path.Combine(fixture.Paths.StandaloneDataRoot, "backup"), outside);
        Assert.Throws<InvalidDataException>(() => fixture.Paths.ImportStandalone());
        Assert.False(File.Exists(Path.Combine(fixture.Paths.DataRoot, "standalone-imported.txt")));
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public void NumericPrefixOfMaliciousOnlineFixPathIsNotAGameId()
    {
        using var fixture = new Fixture(); fixture.Game("100/../../other");
        using var context = fixture.Paths.Enter();
        var tracker = new Playtime(Path.Combine(fixture.Paths.DataRoot, "playtime.txt"), _ => { });
        tracker.RefreshGames();
        Assert.Empty(tracker.GameDirs());
    }

    [Fact]
    public void ParentLifetimeChecksRejectPidReuseByStartTime()
    {
        using var parent = System.Diagnostics.Process.GetCurrentProcess();
        var start = parent.StartTime.ToUniversalTime().Ticks;
        Assert.True(CabinetHelper.ParentAlive(parent, start));
        Assert.False(CabinetHelper.ParentAlive(parent, start + 1));
    }

    [Fact]
    public async Task CompletedHelperInvalidatesUnchangedIniFingerprintAndRepairsRevertedCache()
    {
        using var fixture = new Fixture(); fixture.Schema();
        using var service = new CabinetService(new Settings { CabinetEnabled = true }, fixture.Paths, _ => { });
        await service.TickAsync();
        var file = Path.Combine(fixture.Stats, "UserGameStats_123_100.bin");
        var fingerprint = File.ReadAllText(Path.Combine(fixture.Paths.DataRoot, "state.txt"));
        var reverted = Sub("", Sub("cache", new KV(1, "unknown", "preserve"))).Write();
        File.WriteAllBytes(file, reverted);
        await service.TickAsync();
        Assert.Equal(reverted, File.ReadAllBytes(file)); // unchanged INIs are normally skipped
        fixture.Account = null; // completed helper must persist invalidation even without an account
        service.HelperTickOverride = (_, _) => new[] { "100" };
        await service.TickAsync();
        Assert.Empty(File.ReadAllText(Path.Combine(fixture.Paths.DataRoot, "state.txt")));
        fixture.Account = "123";
        using var restarted = new CabinetService(new Settings { CabinetEnabled = true }, fixture.Paths, _ => { });
        await restarted.TickAsync();
        Assert.Equal(1, KV.Read(File.ReadAllBytes(file)).Children[0].Get("1").Get("data").Value);
        Assert.Equal(fingerprint, File.ReadAllText(Path.Combine(fixture.Paths.DataRoot, "state.txt")));
    }

    [Fact]
    public async Task OptionalFailureDoesNotAbortCoreSync()
    {
        using var fixture = new Fixture(); fixture.Schema(); var logs = new List<string>();
        using var service = new CabinetService(new Settings { CabinetEnabled = true }, fixture.Paths, logs.Add);
        service.OptionalTickOverride = () => throw new IOException("optional fixture failure");
        await service.SyncNowAsync();
        Assert.Equal(1, Assert.Single(service.Status.Games).AchievementsUnlocked);
        Assert.Contains(logs, line => line.Contains("Workshop deferred: optional fixture failure"));
    }

    [Fact]
    public void AtomicOutcomeWriterRefusesJunctionParents()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateDirectory(fixture.Paths.DataRoot);
        WorkshopJunction.Create(Path.Combine(fixture.Paths.DataRoot, "schemas"), outside);
        Assert.Throws<InvalidDataException>(() => WorkshopEngine.AtomicWrite(Path.Combine(fixture.Paths.DataRoot, "schemas", "100.txt"), "attempting"));
        Assert.Empty(Directory.GetFiles(outside));
    }
}
