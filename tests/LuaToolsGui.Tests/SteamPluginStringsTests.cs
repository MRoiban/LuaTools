using System.Text.Json;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamPluginStringsTests
{
    [Theory]
    [InlineData("fr", "Cancel", "Annuler")]
    [InlineData("fr", "Download", "Télécharger")]
    [InlineData("en", "Restart Steam…", "Restart Steam…")]
    [InlineData("fr", "Settings…", "Paramètres…")]
    public void SharedLabelsComeFromAppResources(string language, string key, string expected)
    {
        using var json = JsonDocument.Parse(SteamPluginStrings.BuildJson());
        Assert.Equal(expected, json.RootElement.GetProperty(language).GetProperty(key).GetString());
    }

    [Fact]
    public void OnlyEnglishAndFrenchExistWithEnglishFallbackForNewCopy()
    {
        using var json = JsonDocument.Parse(SteamPluginStrings.BuildJson());
        var root = json.RootElement;
        Assert.Equal(new[] { "en", "fr" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.False(root.GetProperty("fr").TryGetProperty("Add {game} via LuaTools", out _));
        Assert.Equal("Cancel", root.GetProperty("en").GetProperty("Cancel").GetString());
    }
}
