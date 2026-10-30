using LuaToolsGui.Services;
using Xunit;
namespace LuaToolsGui.Tests;
public sealed class DepotProjectionTests
{
    [Fact]
    public void SharedGuiProjectionKeepsDisabledOmittedDeclarationsAndSearchExcludesPublicOnlyPin()
    {
        var lua = LuaFileParser.ParseText("addappid(570)\n--addappid(571,0,\"never-output\") -- French\n--setManifestid(571,\"111\")\n", 570);
        var info = new AppDepotInfo(570, [new ContentDepot(572, 100, null, false, "windows", null, "987654321")], [], []);
        var rows = DepotProjection.BuildRows(info, lua, _ => null);
        var declared = Assert.Single(rows.InLua.Where(r => r.Id == 571));
        Assert.False(declared.IsEnabled); Assert.Equal("french", declared.Language);
        Assert.True(declared.CanLock); Assert.True(declared.Matches("111"));
        var missing = Assert.Single(rows.Missing);
        Assert.False(missing.Matches("987654321"));
    }
}
