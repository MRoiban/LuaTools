using System.IO;
using System.IO.Compression;
using System.Text;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Whether a downloaded lua carries the keys Steam needs, against Steam's depot list.</summary>
public sealed class LuaKeyCoverageTests
{
    private const long App = 2868840;
    private static string Key(char c) => new(c, 64);

    // Slay the Spire 2's real depot shape: one Windows depot, plus depots LuaTools must not require.
    private static AppDepotInfo Steam(params ContentDepot[] extra) => new(App,
    [
        new ContentDepot(2868841, 2_910_000_000, null, false, "windows", null, "7641289876978921674"),
        new ContentDepot(2868842, 900_000_000, null, false, "macos", null, "1"),           // another OS
        new ContentDepot(2868843, 800_000_000, null, false, "linux", null, "2"),           // another OS
        new ContentDepot(2868850, 50_000_000, null, false, null, "french", "3"),          // a language pack
        new ContentDepot(2868860, 60_000_000, 2868900, false, null, null, "4"),           // a DLC's depot
        new ContentDepot(228989, 0, null, true, null, null) { FromAppId = 228980 },       // shared redistributable
        new ContentDepot(2868870, 70_000_000, null, false, null, null),                   // not on the public branch
        .. extra,
    ], [], []);

    [Fact]
    public void OnlyTheBaseGamesOwnPublicWindowsDepotsAreRequired()
    {
        var required = LuaKeyCoverage.RequiredDepots(Steam(new ContentDepot(2868844, 10, null, false, "windows,linux", "", "5")));
        Assert.Equal([2868841L, 2868844L], required.Select(d => d.Id));
    }

    [Fact]
    public void AScriptWithANeighbouringGamesKeysInsteadOfItsOwnIsMissingTheMainDepot()
    {
        // DexLua's actual shape for this app: keys for Card Survival's depots (app 2868860), none for 2868841.
        string dexLua = $"addappid({App}, 1)\naddappid(2868861, 0, \"{Key('a')}\")\naddappid(2868862, 0, \"{Key('b')}\")\n";
        var coverage = LuaKeyCoverage.Check(dexLua, Steam());
        Assert.Equal(KeyCoverageVerdict.MissingMain, coverage.Verdict);
        Assert.Equal(2868841, coverage.MainDepot);
        Assert.False(coverage.Usable);
        Assert.False(coverage.Shareable);
    }

    [Fact]
    public void EveryRequiredKeyIsCompleteAndAMissingSmallerOneIsPartial()
    {
        var steam = Steam(new ContentDepot(2868845, 5_000_000, null, false, null, null, "6"));
        string both = $"addappid({App})\naddappid(2868841, 1, \"{Key('a')}\")\naddappid(2868845,1,'{Key('c')}') -- extras\n";
        Assert.Equal(KeyCoverageVerdict.Complete, LuaKeyCoverage.Check(both, steam).Verdict);

        var partial = LuaKeyCoverage.Check($"addappid({App})\naddappid(2868841, 1, \"{Key('a')}\")\n", steam);
        Assert.Equal(KeyCoverageVerdict.Partial, partial.Verdict);
        Assert.Equal([2868845L], partial.Missing);
        Assert.True(partial.Usable);
        Assert.False(partial.Shareable);
    }

    [Theory]
    [InlineData("-- addappid(2868841, 1, \"{K}\")")]          // commented out: OpenSteamTool never runs it
    [InlineData("addappid(2868841, 1, \"Slay the Spire 2\")")] // a title where the key goes
    [InlineData("addappid(2868841, 1, \"abcdef\")")]           // too short: OpenSteamTool drops it
    [InlineData("addappid(2868841)")]                          // no key at all
    public void OnlyAnActive64HexKeyCounts(string line)
    {
        string lua = $"addappid({App})\n{line.Replace("{K}", Key('a'))}\n";
        Assert.Equal(KeyCoverageVerdict.MissingMain, LuaKeyCoverage.Check(lua, Steam()).Verdict);
    }

    [Fact]
    public void WithoutSizesOneKeyedDepotIsEnough()
    {
        var steam = new AppDepotInfo(App, [new ContentDepot(11, 0, null, false, null, null, "1"), new ContentDepot(12, 0, null, false, null, null, "2")], [], []);
        var one = LuaKeyCoverage.Check($"addappid({App})\naddappid(12, 0, \"{Key('a')}\")\n", steam);
        Assert.Equal(KeyCoverageVerdict.Partial, one.Verdict);
        Assert.Null(one.MainDepot);
        Assert.Equal(KeyCoverageVerdict.MissingMain, LuaKeyCoverage.Check($"addappid({App})\n", steam).Verdict);
    }

    [Fact]
    public void NoDepotListOrNothingShippedCannotFailALua()
    {
        Assert.Equal(KeyCoverageVerdict.Unverifiable, LuaKeyCoverage.Check($"addappid({App})\n", null).Verdict);
        Assert.True(KeyCoverage.Unverifiable.Usable);
        Assert.False(KeyCoverage.Unverifiable.Shareable);
        var unreleased = new AppDepotInfo(App, [new ContentDepot(2868841, 0, null, false, "windows", null)], [], []);
        Assert.Equal(KeyCoverageVerdict.Complete, LuaKeyCoverage.Check($"addappid({App})\n", unreleased).Verdict);
    }

    [Fact]
    public void TheLuaIsReadFromAPlainFileOrTheSingleLuaOfAPackage()
    {
        string lua = $"addappid({App})\naddappid(2868841, 1, \"{Key('a')}\")\n";
        Assert.Equal(lua, LuaKeyCoverage.ReadLua(Encoding.UTF8.GetBytes(lua)));
        Assert.Equal(lua, LuaKeyCoverage.ReadLua(Zip(($"{App}.lua", lua), ("2868841_1.manifest", "x"), ("README.txt", "ad"))));
        Assert.Null(LuaKeyCoverage.ReadLua(Zip(("a.lua", lua), ("b.lua", lua))));
        Assert.Null(LuaKeyCoverage.ReadLua(Zip(("2868841_1.manifest", "x"))));
        Assert.Null(LuaKeyCoverage.ReadLua([0xC3, 0x28])); // not UTF-8

        string root = Path.Combine(Path.GetTempPath(), "keycoverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, $"{App}.zip"), plain = Path.Combine(root, $"{App}.lua");
            File.WriteAllBytes(zip, Zip(($"{App}.lua", lua), ("2868841_1.manifest", new string('m', 4096))));
            File.WriteAllText(plain, lua);
            Assert.Equal(lua, LuaKeyCoverage.ReadLuaFile(zip));
            Assert.Equal(lua, LuaKeyCoverage.ReadLuaFile(plain));
            Assert.Null(LuaKeyCoverage.ReadLuaFile(Path.Combine(root, "missing.lua")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AnAddPickedByNameOnlySwitchesToUsableSourcesThatCostTheUserNothing()
    {
        PluginAddService.SourceRow Row(string name, bool needsKey = false, bool locked = false) =>
            new() { Name = name, Status = "available", NeedsKey = needsKey, Locked = locked };
        var rows = new[]
        {
            Row(AcquisitionCoordinator.CabinetSource), Row(AcquisitionCoordinator.HubcapSource, needsKey: true),
            Row(SteamMlService.SourceName), Row(DexLuaService.SourceName), Row("Ryuu", locked: true),
        };
        var alternatives = AcquisitionCoordinator.AlternativesFrom(rows, DexLuaService.SourceName);
        Assert.Equal([AcquisitionCoordinator.CabinetSource, SteamMlService.SourceName], alternatives.Select(a => a.Name));
        Assert.All(alternatives, a => Assert.False(a.NeedsKey));
    }

    private static byte[] Zip(params (string Name, string Data)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(data));
            }
        return output.ToArray();
    }
}
