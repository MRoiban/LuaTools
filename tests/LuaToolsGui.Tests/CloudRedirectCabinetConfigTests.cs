using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class CloudRedirectCabinetConfigTests : IDisposable
{
    private const string Server = "https://cabinet.mc2.onl";
    private const string Token = "cab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CloudRedirectConfigTests", Guid.NewGuid().ToString("N"));
    private string TokenPath => Path.Combine(_directory, "tokens_cabinet.json");
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private CloudRedirectCabinetConfig Create() => new(_directory, Transform, Transform);
    private static byte[] Transform(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0xA5)).ToArray();

    [Fact]
    public void ApplyWritesTheDllContractAndRawProtectedCredentials()
    {
        var service = Create();
        service.Apply(Server + "/", Token);
        var config = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        Assert.Equal(new[] { "auto_update_dll", "provider", "token_path", "token_paths" }, config.Select(p => p.Key).Order().ToArray());
        Assert.Equal("cabinet", config["provider"]!.GetValue<string>());
        Assert.Equal(TokenPath, config["token_path"]!.GetValue<string>());
        Assert.Single(config["token_paths"]!.AsObject());
        Assert.Equal(TokenPath, config["token_paths"]!["cabinet"]!.GetValue<string>());
        Assert.False(config["auto_update_dll"]!.GetValue<bool>());
        var raw = File.ReadAllBytes(TokenPath);
        byte[] expected = Encoding.UTF8.GetBytes("{\"server\":\"" + Server + "\",\"token\":\"" + Token + "\"}");
        Assert.Equal(Transform(expected), raw);
        Assert.DoesNotContain(Token, Encoding.UTF8.GetString(raw));
        Assert.False(File.ReadAllBytes(ConfigPath).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Contains('\n', File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Equal(new CloudRedirectCabinetStatus(true, Server), service.Status());
        Assert.DoesNotContain(Token, service.Status().ToString());
    }

    [Fact]
    public void ReapplyingMergesProviderPathsAndPreservesOtherSettings()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, "{\"provider\":\"gdrive\",\"token_path\":\"old\",\"token_paths\":{\"gdrive\":\"keep\",\"cabinet\":\"stale\"},\"auto_update_dll\":true,\"games\":{\"123\":{\"save\":\"slot\"}},\"custom\":[1,true]}");
        var service = Create();
        service.Apply(Server, Token);
        service.Apply("https://example.com:8443", "cab_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var config = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        Assert.Equal("keep", config["token_paths"]!["gdrive"]!.GetValue<string>());
        Assert.Equal(TokenPath, config["token_paths"]!["cabinet"]!.GetValue<string>());
        Assert.Equal("slot", config["games"]!["123"]!["save"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[1,true]"), config["custom"]));
        Assert.False(config["auto_update_dll"]!.GetValue<bool>());
        Assert.Equal(new CloudRedirectCabinetStatus(true, "https://example.com:8443"), service.Status());
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("broken {")]
    [InlineData("[]")]
    [InlineData("null")]
    public void UnparsableOrNonObjectConfigIsBackedUpBeforeReplacement(string old)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, old);
        Create().Apply(Server, Token);
        Assert.Equal(old, File.ReadAllText(ConfigPath + ".bak"));
        Assert.Equal(4, JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject().Count);
    }

    [Fact]
    public void RemoveDropsOnlyCabinetCredentialsAndSelection()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, "{\"games\":[123],\"token_paths\":{\"gdrive\":\"other.json\"},\"custom\":true}");
        var service = Create();
        service.Apply(Server, Token);
        service.Remove();
        service.Remove();
        Assert.False(File.Exists(TokenPath));
        var config = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        Assert.False(config.ContainsKey("provider"));
        Assert.False(config.ContainsKey("token_path"));
        Assert.False(config["token_paths"]!.AsObject().ContainsKey("cabinet"));
        Assert.Equal("other.json", config["token_paths"]!["gdrive"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[123]"), config["games"]));
        Assert.True(config["custom"]!.GetValue<bool>());
        Assert.False(config["auto_update_dll"]!.GetValue<bool>());
        Assert.Equal(new CloudRedirectCabinetStatus(false, null), service.Status());
    }

    [Fact]
    public void RemoveRetainsAnotherProvidersSelection()
    {
        Create().Apply(Server, Token);
        File.WriteAllText(ConfigPath, "{\"provider\":\"gdrive\",\"token_path\":\"drive.json\",\"token_paths\":{\"cabinet\":\"old\",\"gdrive\":\"drive.json\"},\"auto_update_dll\":false}");
        Create().Remove();
        Assert.False(File.Exists(TokenPath));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"provider\":\"gdrive\",\"token_path\":\"drive.json\",\"token_paths\":{\"gdrive\":\"drive.json\"},\"auto_update_dll\":false}"), JsonNode.Parse(File.ReadAllText(ConfigPath))));
    }

    [Fact]
    public void RemoveDoesNotCreateOrOverwriteMissingOrCorruptConfig()
    {
        Create().Remove();
        Assert.False(Directory.Exists(_directory));
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, "broken");
        File.WriteAllText(TokenPath, "stale");
        Create().Remove();
        Assert.False(File.Exists(TokenPath));
        Assert.Equal("broken", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void ProtectionFailureDoesNotWritePlaintextOrChangeConfig()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, "{\"custom\":true}");
        var service = new CloudRedirectCabinetConfig(_directory, _ => throw new CryptographicException());
        Assert.Throws<CryptographicException>(() => service.Apply(Server, Token));
        Assert.Equal("{\"custom\":true}", File.ReadAllText(ConfigPath));
        Assert.False(File.Exists(TokenPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void RealCurrentUserDpapiRoundTripOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = new CloudRedirectCabinetConfig(_directory);
        service.Apply(Server, Token);
        byte[] raw = File.ReadAllBytes(TokenPath);
        Assert.NotEqual((byte)'{', raw[0]);
        byte[] clear = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
        try
        {
            var json = JsonNode.Parse(clear)!;
            Assert.Equal(Server, json["server"]!.GetValue<string>());
            Assert.Equal(Token, json["token"]!.GetValue<string>());
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Assert.Equal(new CloudRedirectCabinetStatus(true, Server), service.Status());
        service.Remove();
        Assert.False(service.Status().Configured);
    }

    [Fact]
    public void ApplyMatchesThePublishedWindowsPathFixture()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string directory = @"C:\Users\Alex\AppData\Roaming\CloudRedirect";
        // The exact DLL fixture uses a synthetic profile. Never overwrite an existing profile's data.
        Assert.False(Directory.Exists(directory));
        try
        {
            new CloudRedirectCabinetConfig(directory, Transform, Transform).Apply(Server, Token);
            string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cloudredirect-config.json"));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture),
                JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "config.json")))));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
