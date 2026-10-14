using System.Security.Cryptography;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class DependencyTrustPolicyTests
{
    [Theory]
    [InlineData("DepotDownloader")]
    [InlineData("Steamless")]
    [InlineData("SteamAutoCrack")]
    [InlineData("CloudRedirect")]
    [InlineData("CloudRedirectAddon")]
    [InlineData("OpenSteamTools")]
    [InlineData("BetterSteamTools")]
    [InlineData("LuaToolsPlugin")]
    [InlineData("LuaToolsPluginFrontend")]
    public void EmptyAllowlistRejectsEvenExplicitApproval(string component)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DependencyTrustPolicy.RequireInstallApproval(component, true));
        Assert.Contains("unavailable", ex.Message);
    }

    [Fact]
    public void ExistingFileDoesNotEstablishTrust()
    {
        string path = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(path, "local executable or JavaScript");
            Assert.False(DependencyTrustPolicy.IsTrustedFile("LuaToolsPlugin", path, "public/luatools.js"));
            Assert.Throws<InvalidOperationException>(() => DependencyTrustPolicy.RequireDirectory("DepotDownloader", System.IO.Path.GetDirectoryName(path)!));
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void HashVerificationRejectsMissingMalformedAndModifiedBytes()
    {
        byte[] bytes = [1, 2, 3];
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        DependencyTrustPolicy.VerifyHash(bytes, hash.ToLowerInvariant());
        Assert.Throws<InvalidOperationException>(() => DependencyTrustPolicy.VerifyHash(bytes, ""));
        Assert.Throws<InvalidOperationException>(() => DependencyTrustPolicy.VerifyHash(bytes, new string('z', 64)));
        Assert.Throws<InvalidOperationException>(() => DependencyTrustPolicy.VerifyHash([1, 2, 4], hash));
    }
    [Theory]
    [InlineData("ws://127.0.0.1:8080/devtools/page/ABC-123", true)]
    [InlineData("ws://localhost:8080/devtools/page/ABC", false)]
    [InlineData("wss://127.0.0.1:8080/devtools/page/ABC", false)]
    [InlineData("ws://example.com:8080/devtools/page/ABC", false)]
    [InlineData("ws://127.0.0.1:6767/devtools/page/ABC", false)]
    [InlineData("ws://user@127.0.0.1:8080/devtools/page/ABC", false)]
    [InlineData("ws://127.0.0.1:8080/devtools/page/ABC?remote=x", false)]
    [InlineData("ws://127.0.0.1:8080/devtools/page/", false)]
    public void CdpSocketsRemainOnExactLocalEndpoint(string url, bool expected) =>
        Assert.Equal(expected, CefInjectorService.IsAllowedDebuggerEndpoint(url));
}
