using System.Text.Json;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamPluginStringsTests
{
    [Theory]
    [InlineData("fr", "Cancel", "Annuler")]
    [InlineData("nl", "Download", "Downloaden")]
    [InlineData("en", "Restart Steam…", "Restart Steam…")]
    [InlineData("fr", "Settings…", "Paramètres…")]
    public void SharedLabelsComeFromAppResources(string language, string key, string expected)
    {
        using var json = JsonDocument.Parse(SteamPluginStrings.BuildJson());
        Assert.Equal(expected, json.RootElement.GetProperty(language).GetProperty(key).GetString());
    }

    [Fact]
    public void AllAppLanguagesAndSteamAliasesExistWithEnglishFallbackForNewCopy()
    {
        using var json = JsonDocument.Parse(SteamPluginStrings.BuildJson());
        var root = json.RootElement;
        Assert.Equal(36, root.EnumerateObject().Count());
        Assert.Equal(root.GetProperty("zh-hans").GetRawText(), root.GetProperty("zh-cn").GetRawText());
        Assert.Equal(root.GetProperty("nb").GetRawText(), root.GetProperty("no").GetRawText());
        Assert.False(root.GetProperty("fr").TryGetProperty("Add {game} via LuaTools", out _));
        Assert.Equal("Cancel", root.GetProperty("en").GetProperty("Cancel").GetString());
    }
}
