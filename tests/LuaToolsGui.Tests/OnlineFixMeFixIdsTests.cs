using LuaToolsGui.Services;
using LuaToolsGui.Services.OnlineFixMe;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class OnlineFixMeFixIdsTests
{
    [Theory]
    [InlineData("Game_Fix_Repair-v2.rar", "onlinefixme:Game_Fix_Repair-v2_rar")]
    [InlineData("A b:é/修.zip", "onlinefixme:A_b_____zip")]
    [InlineData("", "onlinefixme:")]
    public void SanitizesEveryNonAsciiIdentifierCharacter(string archive, string expected)
    {
        string id = OnlineFixMeFixIds.ForArchive(archive);
        Assert.Equal(expected, id);
        Assert.True(ApiContract.ValidFixId(id));
        Assert.True(OnlineFixMeFixIds.IsOnlineFixMe(id));
    }

    [Fact]
    public void LongNamesAreBoundedAndDeterministic()
    {
        string archive = new string('a', 200) + "_Fix_Repair.rar";
        string id = OnlineFixMeFixIds.ForArchive(archive);
        Assert.Equal(128, id.Length);
        Assert.Equal(id, OnlineFixMeFixIds.ForArchive(archive));
        Assert.True(ApiContract.ValidFixId(id));
    }

    [Theory]
    [InlineData("online-fix:release")]
    [InlineData("provider:online-fix.me")]
    [InlineData("Onlinefixme:archive")]
    [InlineData("")]
    public void RecognizesOnlyTheProviderPrefix(string id) => Assert.False(OnlineFixMeFixIds.IsOnlineFixMe(id));
}
