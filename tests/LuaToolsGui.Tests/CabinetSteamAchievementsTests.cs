using System.IO;
using CabinetEngine;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Lua games keep achievements in Steam's binary stats cache; Cabinet reads and adds to it.</summary>
public sealed class CabinetSteamAchievementsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cabinet-steam-stats-" + Guid.NewGuid().ToString("N"));
    public CabinetSteamAchievementsTests() => Directory.CreateDirectory(Path.Combine(_dir, "backup"));
    public void Dispose() => Directory.Delete(_dir, true);

    private static KV Sub(string key, params KV[] children) => new(0, key, children.ToList());
    private static KV Str(string key, string value) => new(1, key, value);

    // Steam's schema: stat "1" holds achievement bits 0 and 1, stat "2" is a plain int stat.
    private void WriteSchema(string appId) => File.WriteAllBytes(Path.Combine(_dir, $"UserGameStatsSchema_{appId}.bin"),
        Sub("", Sub(appId, Str("gamename", "Lua Game"), Sub("stats",
            Sub("1", Str("type", "4"), Sub("bits",
                Sub("0", Str("name", "ACH_A"), Str("bit", "0")),
                Sub("1", Str("name", "ACH_B"), Str("bit", "1")))),
            Sub("2", Str("name", "kills"), Str("type", "1"))))).Write());

    [Fact]
    public void ReadsAndAddsUnlocksWithoutClearingAny()
    {
        Assert.Null(Syncer.ReadSteamAchievements("777", "42", _dir));               // no schema yet
        Assert.Equal(-1, Syncer.MergeSteamAchievements("777", "42", new Dictionary<string, long> { ["ACH_A"] = 1 }, _dir, _dir));
        WriteSchema("777");
        Assert.Empty(Syncer.ReadSteamAchievements("777", "42", _dir)!);              // schema, never played

        var first = new Dictionary<string, long> { ["ACH_A"] = 1_600_000_000, ["NOT_IN_SCHEMA"] = 1 };
        Assert.Equal(1, Syncer.MergeSteamAchievements("777", "42", first, _dir, Path.Combine(_dir, "backup")));
        Assert.Equal(new Dictionary<string, long> { ["ACH_A"] = 1_600_000_000 }, Syncer.ReadSteamAchievements("777", "42", _dir));
        Assert.Equal(0, Syncer.MergeSteamAchievements("777", "42", first, _dir, Path.Combine(_dir, "backup")));

        // A later unlock keeps the first one and its time; Steam's original file is backed up once.
        Assert.Equal(1, Syncer.MergeSteamAchievements("777", "42", new Dictionary<string, long> { ["ACH_B"] = 1_700_000_000 }, _dir, Path.Combine(_dir, "backup")));
        Assert.Equal(new Dictionary<string, long> { ["ACH_A"] = 1_600_000_000, ["ACH_B"] = 1_700_000_000 }, Syncer.ReadSteamAchievements("777", "42", _dir));
        Assert.True(File.Exists(Path.Combine(_dir, "backup", "UserGameStats_42_777.bin")));
        Assert.Empty(Syncer.ReadSteamAchievements("777", "43", _dir)!);              // another Steam account
    }
}
