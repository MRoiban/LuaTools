using System.Globalization;
using System.IO;
using System.Text.Json;
using LuaToolsGui;
using Xunit;

namespace LuaToolsGui.Tests;

public class LanguageDetectionTests
{
    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-BE", "fr")]
    [InlineData("fr-CA", "fr")]
    [InlineData("fr-CH", "fr")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("es-MX", "en")]
    [InlineData("pt-BR", "en")]
    [InlineData("zh-Hant", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("nb-NO", "en")]
    [InlineData("ar-SA", "en")]
    [InlineData("he-IL", "en")]
    [InlineData("", "en")]
    public void OsLanguage_UsesFrenchOnlyForFrenchCultures(string os, string expected) =>
        Assert.Equal(expected, Program.MatchOsLanguage(new CultureInfo(os)));

    [Theory]
    [InlineData("de", "fr-BE", "fr")]
    [InlineData("zh-Hans", "fr-CA", "fr")]
    [InlineData("pt-BR", "de-DE", "en")]
    [InlineData("not-a-culture!", "fr-FR", "fr")]
    [InlineData(null, "fr-FR", "fr")]
    [InlineData("", "en-US", "en")]
    [InlineData("en", "fr-FR", "en")]
    [InlineData("fr", "en-US", "fr")]
    public void PersistedLanguage_UnsupportedValuesUseAutomaticDetection(string? saved, string os, string expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { Language = saved }));
            Assert.Equal(expected, Program.ResolveLanguageTag(new CultureInfo(os), path));
        }
        finally { File.Delete(path); }
    }
}
