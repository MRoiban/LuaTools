using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using CabinetEngine;
using LuaToolsGui.Services.GameTools;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Pure edits behind the Tools page: Steam's binary stats files, CloudRedirect's JSON, localconfig.vdf.</summary>
public sealed class GameToolsFilesTests
{
    internal static KV Sub(string key, params KV[] children) => new(0, key, children.ToList());
    internal static KV Str(string key, string value) => new(1, key, value);
    internal static KV Int(string key, int value) => new(2, key, value);
    internal static KV Flt(string key, float value) => new(3, key, value);

    /// <summary>Stat 1 holds four achievements (bit 3 protected), stat 2 is an int, 3 a float, 4 an AVGRATE,
    /// 5 a server-only int.</summary>
    internal static byte[] Schema(long appId = 777) => Sub("", Sub(appId.ToString(),
        Str("gamename", "Test Game"),
        Sub("stats",
            Sub("1", Str("type", "4"), Sub("bits",
                Sub("0", Str("name", "ACH_A"), Sub("display",
                    Sub("name", Str("english", "First"), Str("french", "Premier")),
                    Sub("desc", Str("english", "Do the first thing")),
                    Int("hidden", 0), Str("icon", "a.jpg"), Str("icon_gray", "a_gray.jpg")),
                    Sub("progress", Int("min_val", 0), Int("max_val", 10),
                        Sub("value", Str("operation", "statvalue"), Str("operand1", "kills")))),
                Sub("1", Str("name", "ACH_B"), Sub("display", Sub("name", Str("english", "Second")), Int("hidden", 1),
                    Str("icon", "../evil.jpg"))),
                Sub("2", Str("name", "ACH_C")),
                Sub("3", Str("name", "ACH_SERVER"), Int("permission", 2)))),
            Sub("2", Str("type", "INT"), Str("name", "kills"), Sub("display", Str("name", "Kills")), Int("incrementonly", 1)),
            Sub("3", Str("type", "FLOAT"), Str("name", "distance"), Flt("default", 1.5f)),
            Sub("4", Str("type", "AVGRATE"), Str("name", "rate")),
            Sub("5", Str("type_int", "1"), Str("type", "garbage"), Str("name", "server_only"), Int("permission", 3))))).Write();

    internal static GameStatsSchema ParsedSchema(string language = "english") => GameStatsFiles.ReadSchema(Schema(), 777, language)!;

    /// <summary>A cache with ACH_A (bit 0) and ACH_C (bit 2) unlocked, pending upload, plus stat values.</summary>
    internal static byte[] UserStats() => Sub("", Sub("cache", Int("crc", 1234), Int("PendingChanges", 1),
        Sub("1", Int("data", 0b101), Int("state", 2), Int("pendingbits", 0b101),
            Sub("AchievementTimes", Int("0", 1_600_000_000), Int("2", 1_650_000_000))),
        Sub("2", Int("data", 7), Int("state", 2)),
        Sub("3", Flt("data", 2.25f)))).Write();

    private static GameStatsEdit Edit(Dictionary<string, bool>? a = null, Dictionary<string, double>? s = null) =>
        new(a ?? new(), s ?? new());

    [Fact]
    public void ReadsTheSchemaInSteamsLanguageWithEnglishFallback()
    {
        var schema = ParsedSchema("french");
        Assert.Equal(new[] { "ACH_A", "ACH_B", "ACH_C", "ACH_SERVER" }, schema.Achievements.Select(a => a.ApiName));
        var a = schema.Achievements[0];
        Assert.Equal(("Premier", "Do the first thing", 1, 0), (a.Name, a.Description, a.StatId, a.Bit));
        Assert.Equal(GameStatsFiles.IconBase + "777/a.jpg", a.IconUrl);
        Assert.Equal(GameStatsFiles.IconBase + "777/a_gray.jpg", a.IconGrayUrl);
        Assert.Equal(new AchievementProgress("kills", 0, 10, 0), a.Progress);
        Assert.Equal("Second", schema.Achievements[1].Name);              // no French text: English
        Assert.True(schema.Achievements[1].Hidden);
        Assert.Null(schema.Achievements[1].IconUrl);                      // a path, not a file name: refused
        Assert.Equal("ACH_C", schema.Achievements[2].Name);               // no display block: API name
        Assert.True(schema.Achievements[3].Protected);
        Assert.False(a.Protected);

        Assert.Equal(new[] { "kills", "distance", "rate", "server_only" }, schema.Stats.Select(s => s.ApiName));
        Assert.Equal(("Kills", false, true, false), (schema.Stats[0].Name, schema.Stats[0].IsFloat, schema.Stats[0].IncrementOnly, schema.Stats[0].Protected));
        Assert.Equal((true, 1.5), (schema.Stats[1].IsFloat, schema.Stats[1].Value));
        Assert.True(schema.Stats[2].Protected);                           // AVGRATE: Steam updates it itself
        Assert.True(schema.Stats[3].Protected);                           // type_int wins over "type"
        Assert.Null(GameStatsFiles.ReadSchema(Sub("", Sub("777", Str("gamename", "x"))).Write(), 777, "english"));
    }

    [Fact]
    public void MergesTheUsersCacheIntoTheSchema()
    {
        var schema = ParsedSchema();
        var (achievements, stats) = GameStatsFiles.ReadState(schema, UserStats());
        Assert.Equal(new[] { true, false, true, false }, achievements.Select(a => a.Unlocked));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000), achievements[0].UnlockedAt);
        Assert.Equal(7, achievements[0].Progress!.Value);                 // progress follows the "kills" stat
        Assert.Equal(new[] { 7, 2.25 }, stats.Take(2).Select(s => s.Value));
        Assert.Equal(0, stats[2].Value);                                  // not in the cache: schema default

        var (fresh, _) = GameStatsFiles.ReadState(schema, null);          // never played
        Assert.All(fresh, a => Assert.False(a.Unlocked));
    }

    [Fact]
    public void LockClearsTheBitItsTimeAndPendingBitAndKeepsEverythingElse()
    {
        var schema = ParsedSchema();
        var bytes = GameStatsFiles.ApplyEdit(UserStats(), schema, Edit(new() { ["ACH_A"] = false }), DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))!;
        var cache = KV.Read(bytes).Get("cache");
        var stat = cache.Get("1");
        Assert.Equal(0b100, (int)stat.Get("data").Value);
        Assert.Equal(0b100, (int)stat.Get("pendingbits").Value);
        Assert.Equal(new[] { "2" }, stat.Get("AchievementTimes").Children.Select(c => c.Key));
        Assert.Equal(2, (int)stat.Get("state").Value);
        Assert.Equal(1234, (int)cache.Get("crc").Value);
        Assert.Equal(1, (int)cache.Get("PendingChanges").Value);
        Assert.Equal(2.25f, (float)cache.Get("3").Get("data").Value);

        var (achievements, _) = GameStatsFiles.ReadState(schema, bytes);
        Assert.Equal(new[] { false, false, true, false }, achievements.Select(a => a.Unlocked));
    }

    [Fact]
    public void UnlockSetsTheBitAndKeepsAnExistingTime()
    {
        var schema = ParsedSchema();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var bytes = GameStatsFiles.ApplyEdit(UserStats(), schema, Edit(new() { ["ACH_A"] = true, ["ACH_B"] = true, ["NOPE"] = true }), now)!;
        var (achievements, _) = GameStatsFiles.ReadState(schema, bytes);
        Assert.Equal(new[] { true, true, true, false }, achievements.Select(a => a.Unlocked));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000), achievements[0].UnlockedAt);
        Assert.Equal(now, achievements[1].UnlockedAt);

        // Nothing to change: no write.
        Assert.Null(GameStatsFiles.ApplyEdit(bytes, schema, Edit(new() { ["ACH_A"] = true, ["NOPE"] = false }), now));
    }

    [Fact]
    public void WritesStatsWithTheirSchemaTypeAndCreatesAFreshCache()
    {
        var schema = ParsedSchema();
        var bytes = GameStatsFiles.ApplyEdit(null, schema,
            Edit(new() { ["ACH_C"] = true }, new() { ["kills"] = 41.6, ["distance"] = 3.5, ["unknown"] = 1 }), DateTimeOffset.FromUnixTimeSeconds(5))!;
        var cache = KV.Read(bytes).Get("cache");
        Assert.NotNull(cache.Get("crc"));
        Assert.Equal((byte)2, cache.Get("2").Get("data").Type);
        Assert.Equal(42, (int)cache.Get("2").Get("data").Value);
        Assert.Equal((byte)3, cache.Get("3").Get("data").Type);
        Assert.Equal(3.5f, (float)cache.Get("3").Get("data").Value);
        var snapshot = new GameStatsSnapshot(777, 42, true, false, false, GameStatsFiles.ReadState(schema, bytes).Achievements, GameStatsFiles.ReadState(schema, bytes).Stats);
        Assert.True(GameStatsFiles.IsApplied(snapshot, Edit(new() { ["ACH_C"] = true }, new() { ["kills"] = 42, ["distance"] = 3.5 })));
        Assert.False(GameStatsFiles.IsApplied(snapshot, Edit(new() { ["ACH_A"] = true })));
        Assert.False(GameStatsFiles.IsApplied(snapshot, Edit(s: new() { ["kills"] = 43 })));
        Assert.False(GameStatsFiles.IsApplied(snapshot, Edit(s: new() { ["unknown"] = 1 })));
    }

    [Fact]
    public void LocksAnAchievementInARealSteamCacheSoCabinetReadsItAsLocked()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "steam-stats");
        var schema = GameStatsFiles.ReadSchema(File.ReadAllBytes(Path.Combine(fixtures, "UserGameStatsSchema_3624140.bin")), 3624140, "english")!;
        Assert.Equal(26, schema.Achievements.Count);
        Assert.Equal("1000 units consumed", schema.Achievements.Single(a => a.ApiName == "UNITS_TIER1").Name);
        var original = File.ReadAllBytes(Path.Combine(fixtures, "UserGameStats_42_3624140.bin"));
        var bytes = GameStatsFiles.ApplyEdit(original, schema, Edit(new() { ["FIRSTWIN"] = false }), DateTimeOffset.UtcNow)!;

        string dir = Path.Combine(Path.GetTempPath(), "gametools-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(fixtures, "UserGameStatsSchema_3624140.bin"), Path.Combine(dir, "UserGameStatsSchema_3624140.bin"));
            File.WriteAllBytes(Path.Combine(dir, "UserGameStats_42_3624140.bin"), bytes);
            Assert.Equal(new[] { "DESERT", "DISTANCE_TIER1", "VEHICLES_TIER1" },
                Syncer.ReadSteamAchievements("3624140", "42", dir)!.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CloudRedirectCopyLosesALockedBitInBothTheBlockAndTheStatValue()
    {
        var schema = ParsedSchema();
        string json = """
            {"crc_stats":1,"stats":[{"id":1,"value":5},{"id":2,"value":7}],
             "achievements":[{"stat_id":1,"bits":5,"unlock_times":[100,0,200,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"names":["a","b","c"]}],
             "playtime":{"minutes_forever":12}}
            """;
        var now = DateTimeOffset.FromUnixTimeSeconds(999);
        var root = JsonNode.Parse(CloudRedirectStatsFiles.ApplyEdit(json, schema,
            Edit(new() { ["ACH_A"] = false, ["ACH_B"] = true }, new() { ["kills"] = 9, ["distance"] = 2.5 }), now))!.AsObject();
        var block = root["achievements"]![0]!;
        Assert.Equal(0b110, (long)block["bits"]!);
        Assert.Equal(0, (long)block["unlock_times"]![0]!);
        Assert.Equal(999, (long)block["unlock_times"]![1]!);
        Assert.Equal(200, (long)block["unlock_times"]![2]!);
        Assert.Equal("a", (string)block["names"]![0]!);
        Assert.Equal(0b110, (long)root["stats"]![0]!["value"]!);
        Assert.Equal(9, (long)root["stats"]![1]!["value"]!);
        Assert.Equal(BitConverter.SingleToUInt32Bits(2.5f), (long)root["stats"]![2]!["value"]!);
        Assert.Equal(12, (long)root["playtime"]!["minutes_forever"]!);

        // An unlock for a stat CloudRedirect never saw creates its block; a lock there has nothing to clear.
        var fresh = JsonNode.Parse(CloudRedirectStatsFiles.ApplyEdit("{}", schema, Edit(new() { ["ACH_C"] = true }), now))!;
        Assert.Equal(4, (long)fresh["achievements"]![0]!["bits"]!);
        Assert.Equal(32, fresh["achievements"]![0]!["unlock_times"]!.AsArray().Count);
        Assert.Throws<InvalidDataException>(() => CloudRedirectStatsFiles.ApplyEdit("[1]", schema, Edit(), now));
    }

    [Fact]
    public void PendingEditsMergeOnlyTheCurrentAccountsEntry()
    {
        var schema = ParsedSchema();
        string existing = """{"99":{"777":[{"name":"ACH_A","stat":1,"bit":0}]},"42":{"555":[{"name":"X","stat":1,"bit":3}],"777":[{"name":"ACH_C","stat":1,"bit":2}]}}""";
        var root = JsonNode.Parse(CloudRedirectStatsFiles.MergePendingEdits(existing, 42, 777, schema,
            Edit(new() { ["ACH_A"] = false, ["ACH_C"] = true, ["NOPE"] = false })))!;
        Assert.Equal("""[{"name":"ACH_A","stat":1,"bit":0}]""", root["99"]!["777"]!.ToJsonString());  // other account untouched
        Assert.Equal("""[{"name":"X","stat":1,"bit":3}]""", root["42"]!["555"]!.ToJsonString());      // other app untouched
        Assert.Equal("""[{"name":"ACH_A","stat":1,"bit":0}]""", root["42"]!["777"]!.ToJsonString());  // re-unlock drops the lock

        // Locking twice lists it once; unlock-only edits still list the app so CloudRedirect uploads it.
        var twice = CloudRedirectStatsFiles.MergePendingEdits(root.ToJsonString(), 42, 777, schema, Edit(new() { ["ACH_A"] = false }));
        Assert.Single(JsonNode.Parse(twice)!["42"]!["777"]!.AsArray());
        Assert.Equal("""{"7":{"777":[]}}""", CloudRedirectStatsFiles.MergePendingEdits(null, 7, 777, schema, Edit(new() { ["ACH_B"] = true })));
        Assert.Equal("""{"7":{"777":[]}}""", CloudRedirectStatsFiles.MergePendingEdits("not json", 7, 777, schema, Edit(new() { ["ACH_B"] = true })));
    }

    private const string LocalConfig = "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        "\t\t\t\t\t\"10\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1700000000\"\n\t\t\t\t\t\t\"Playtime2wks\"\t\t\"90\"\n\t\t\t\t\t\t\"Playtime\"\t\t\"600\"\n\t\t\t\t\t}\n" +
        "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";

    [Fact]
    public void PlaytimeEditsOnlyTheGamesValues()
    {
        Assert.Equal(600, PlaytimeConfig.GetMinutes(LocalConfig, 10));
        Assert.Null(PlaytimeConfig.GetMinutes(LocalConfig, 20));

        string raised = PlaytimeConfig.SetMinutes(LocalConfig, 10, 1500)!;
        Assert.Equal(LocalConfig.Replace("\"600\"", "\"1500\""), raised);           // nothing else moves
        string lowered = PlaytimeConfig.SetMinutes(LocalConfig, 10, 30)!;
        Assert.Equal(30, PlaytimeConfig.GetMinutes(lowered, 10));
        Assert.Contains("\"Playtime2wks\"\t\t\"30\"", lowered);                     // recent time capped at the total
        Assert.Contains("\"LastPlayed\"\t\t\"1700000000\"", lowered);

        string added = PlaytimeConfig.SetMinutes(LocalConfig, 20, 61)!;           // game Steam never recorded
        Assert.Equal(61, PlaytimeConfig.GetMinutes(added, 20));
        Assert.Equal(600, PlaytimeConfig.GetMinutes(added, 10));
        Assert.Null(PlaytimeConfig.SetMinutes("\"UserLocalConfigStore\"\n{\n}\n", 10, 5));

        var bom = PlaytimeConfig.Encode("abc", true);
        Assert.Equal((byte)0xEF, bom[0]);
        Assert.Equal(("abc", true), PlaytimeConfig.Decode(bom));
        Assert.Equal(("abc", false), PlaytimeConfig.Decode(Encoding.UTF8.GetBytes("abc")));
    }

    [Fact]
    public void FindsTheMostRecentSteamLogin()
    {
        string users = "\"users\"\n{\n\t\"76561197960265770\"\n\t{\n\t\t\"AccountName\"\t\t\"a\"\n\t\t\"MostRecent\"\t\t\"0\"\n\t\t\"Timestamp\"\t\t\"200\"\n\t}\n" +
                       "\t\"76561197960265800\"\n\t{\n\t\t\"AccountName\"\t\t\"b\"\n\t\t\"MostRecent\"\t\t\"0\"\n\t\t\"Timestamp\"\t\t\"100\"\n\t}\n}\n";
        Assert.Equal(42u, SteamAccounts.FromLoginUsers(users));
        Assert.Equal(72u, SteamAccounts.FromLoginUsers(users.Replace("\"MostRecent\"\t\t\"0\"\n\t\t\"Timestamp\"\t\t\"100\"", "\"MostRecent\"\t\t\"1\"\n\t\t\"Timestamp\"\t\t\"100\"")));
        Assert.Null(SteamAccounts.FromLoginUsers("\"users\"\n{\n}\n"));
    }

    [Theory]
    [InlineData(false, true, false, true, true, false)]   // engine off: nothing syncs
    [InlineData(true, false, false, true, false, true)]   // OnlineFix game: local copy into Steam
    [InlineData(true, false, false, false, true, false)]  // Lua game, no cloud: nothing pulls unlocks back
    [InlineData(true, true, false, false, true, true)]    // Lua game with Cabinet Cloud
    [InlineData(true, true, false, false, false, false)]  // neither kind of game
    [InlineData(false, false, true, false, true, true)]   // standalone Cabinet: assume it syncs
    public void KnowsWhichGamesCabinetSyncs(bool engine, bool cloud, bool standalone, bool onlineFix, bool lua, bool expected) =>
        Assert.Equal(expected, CabinetSyncScope.Syncs(engine, cloud, standalone, onlineFix, lua));

    [Fact]
    public void HelperRequestsAndAnswersRoundTrip()
    {
        string request = GameStatsHelperClient.BuildRequest(Edit(new() { ["ACH_A"] = false }, new() { ["distance"] = 2.5, ["unknown"] = 1 }), ParsedSchema());
        var parsed = System.Text.Json.JsonSerializer.Deserialize<GameStatsHelper.Request>(request, GameStatsHelper.Json)!;
        Assert.False(parsed.Achievements["ACH_A"]);
        Assert.True(parsed.Stats["distance"].Float);
        Assert.Equal(2.5, parsed.Stats["distance"].Value);
        Assert.False(parsed.Stats.ContainsKey("unknown"));

        Assert.Equal(StatsSaveOutcome.SavedLive, GameStatsHelperClient.ParseResponse("""{"Ok":true,"SteamResult":1}""").Outcome);
        Assert.Equal(new StatsSaveResult(StatsSaveOutcome.LiveRejected, "refused:ACH_A, kills"),
            GameStatsHelperClient.ParseResponse("""{"Ok":false,"Error":"refused","Refused":["ACH_A","kills"]}"""));
        Assert.Equal(new StatsSaveResult(StatsSaveOutcome.LiveRejected, "store-failed:8"),
            GameStatsHelperClient.ParseResponse("""{"Ok":false,"Error":"store-failed","SteamResult":8}"""));
        Assert.Equal(new StatsSaveResult(StatsSaveOutcome.LiveRejected, "helper-failed"), GameStatsHelperClient.ParseResponse(null));
        Assert.Equal(new StatsSaveResult(StatsSaveOutcome.LiveRejected, "helper-failed"), GameStatsHelperClient.ParseResponse("garbage"));
    }

    [Fact]
    public async Task HelperReplyIsFoundAmongSteamclientsOwnOutput()
    {
        string reply = """{"Ok":true,"SteamResult":1}""";
        var noisy = new StringReader("Setting breakpad minidump AppID = 777\nSteam_SetMinidumpSteamID:  Caching Steam ID:  76561197960265770 [API loaded no]\n"
            + "partial line without newline" + GameStatsHelper.ReplyPrefix + reply + "\nlater noise\n");
        Assert.Equal(reply, await GameStatsHelperClient.ReadReplyAsync(noisy, CancellationToken.None));
        Assert.Null(await GameStatsHelperClient.ReadReplyAsync(new StringReader("only noise\n{\"Ok\":true}\n"), CancellationToken.None));
        Assert.Equal(new StatsSaveResult(StatsSaveOutcome.LiveRejected, "helper-failed"), GameStatsHelperClient.ParseResponse(""));
    }

    [Fact]
    public async Task ACancelledIconWaitDoesNotCancelTheSharedDownload()
    {
        var gate = new TaskCompletionSource();
        var handler = new GatedNotFound(gate.Task);
        var cache = new AchievementIconCache(new System.Net.Http.HttpClient(handler), Path.Combine(Path.GetTempPath(), "icons-" + Guid.NewGuid().ToString("N")));
        string url = GameStatsFiles.IconBase + "777/a.jpg";
        using var first = new CancellationTokenSource();
        var cancelled = cache.GetAsync(url, first.Token);
        first.Cancel();                                                   // e.g. Discard or switching games
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var next = cache.GetAsync(url);                                  // the reload joins the same download
        gate.SetResult();
        Assert.Null(await next);                                         // completes (404), not cancelled
        Assert.Equal(1, handler.Requests);
    }

    private sealed class GatedNotFound(Task gate) : System.Net.Http.HttpMessageHandler
    {
        public int Requests;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            await gate.WaitAsync(ct);
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public void IconCacheOnlyAcceptsFilesUnderTheAppsIconFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "icons-" + Guid.NewGuid().ToString("N"));
        var cache = new AchievementIconCache(new System.Net.Http.HttpClient(), root);
        Assert.Equal(Path.Combine(root, "777", "a.jpg"), cache.LocalPath(GameStatsFiles.IconBase + "777/a.jpg"));
        Assert.Null(cache.LocalPath(GameStatsFiles.IconBase + "777/../a.jpg"));
        Assert.Null(cache.LocalPath(GameStatsFiles.IconBase + "777/sub/a.jpg"));
        Assert.Null(cache.LocalPath(GameStatsFiles.IconBase + "x/a.jpg"));
        Assert.Null(cache.LocalPath("https://example.com/777/a.jpg"));
        Assert.True(AchievementIconCache.IsImage([0xFF, 0xD8, 0xFF, 0]));
        Assert.True(AchievementIconCache.IsImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.False(AchievementIconCache.IsImage(Encoding.ASCII.GetBytes("<html>")));
    }
}
