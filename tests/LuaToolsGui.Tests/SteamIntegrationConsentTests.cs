using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamIntegrationConsentTests
{
    [Fact]
    public async Task DefaultOffBlocksBackendPluginAndAddonBeforeAnyNetworkOrSteamWrite()
    {
        using var temp = new Fixture();
        var settings = new SettingsService(new AppSettings { SteamPathOverride = temp.Directory });
        var handler = new Stub();
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(1), handler);
        var github = new GithubProxy(http);
        var steam = new SteamService(settings);
        var cache = new CacheService(Path.Combine(temp.Directory, "cache.json"));
        var unlocker = new UnlockerService(steam, settings, cache, github);
        var injector = new CefInjectorService(steam, settings, NullLogger<CefInjectorService>.Instance);
        var plugin = new PluginInstallerService(steam, settings, github, injector);
        File.WriteAllText(Path.Combine(temp.Directory, "OpenSteamTool.dll"), "unreviewed cached dll");
        Assert.False((await unlocker.InstallAsync(UnlockerMode.Ost)).Success);
        Assert.False((await unlocker.InstallAsync(UnlockerMode.Bst)).Success);
        Assert.False((await unlocker.EnableCloudRedirectAsync()).Success);
        Assert.False((await plugin.InstallAsync(null)).ok);
        await injector.StartAsync(default);
        await injector.StopAsync(default);
        await injector.StopAsync(default);
        Assert.Equal(0, handler.Requests);
        Assert.Equal("unreviewed cached dll", File.ReadAllText(Path.Combine(temp.Directory, "OpenSteamTool.dll")));
    }

    [Fact]
    public void EnabledExplicitModeOperationCopiesOnlyVerifiedInertSnapshotsToIsolatedRoot()
    {
        using var temp = new Fixture();
        string stage = temp.Extract("OpenSteamTools", ("release/dwmapi.dll", "inert loader"),
            ("release/OpenSteamTool.dll", "inert payload"), ("release/xinput1_4.dll", "inert second loader"));
        string destination = Path.Combine(temp.Directory, "isolated-steam");
        System.IO.Directory.CreateDirectory(destination);
        string[] wanted = ["dwmapi.dll", "OpenSteamTool.dll", "xinput1_4.dll"];
        var files = UnlockerService.FindWanted(stage, wanted);
        string receipt = Path.Combine(temp.Directory, "consent", "OpenSteamTools.json");
        UnlockerService.InstallConsentedModeFiles("OpenSteamTools", files, destination, receipt, _ => true);
        Assert.Equal("inert payload", File.ReadAllText(Path.Combine(destination, "OpenSteamTool.dll")));
        UserAuthorizedDependencies.RequireFiles("OpenSteamTools", receipt,
            wanted.ToDictionary(name => name, name => Path.Combine(destination, name)), _ => true);
        Assert.False(DependencyTrustPolicy.IsTrustedFile("OpenSteamTools", Path.Combine(destination, "OpenSteamTool.dll")));
    }

    [Fact]
    public void ChangedStagedModeCannotWriteOrCreateInstallReceipt()
    {
        using var temp = new Fixture();
        string stage = temp.Extract("BetterSteamTools", ("dwmapi.dll", "inert"));
        var files = UnlockerService.FindWanted(stage, ["dwmapi.dll"]);
        File.WriteAllText(files["dwmapi.dll"], "substituted bytes");
        string destination = Path.Combine(temp.Directory, "target");
        System.IO.Directory.CreateDirectory(destination);
        string receipt = Path.Combine(temp.Directory, "receipt.json");
        Assert.Throws<InvalidDataException>(() => UnlockerService.InstallConsentedModeFiles("BetterSteamTools", files, destination, receipt, _ => true));
        Assert.False(File.Exists(Path.Combine(destination, "dwmapi.dll")));
        Assert.False(File.Exists(receipt));
    }

    [Fact]
    public void DisabledModeCannotUsePreviouslyConsentedStage()
    {
        using var temp = new Fixture();
        string stage = temp.Extract("OpenSteamTools", ("dwmapi.dll", "inert"));
        var files = UnlockerService.FindWanted(stage, ["dwmapi.dll"]);
        Assert.Throws<InvalidOperationException>(() => UnlockerService.InstallConsentedModeFiles("OpenSteamTools", files,
            temp.Directory, Path.Combine(temp.Directory, "receipt.json"), _ => false));
        Assert.False(File.Exists(Path.Combine(temp.Directory, "dwmapi.dll")));
    }

    [Fact]
    public void RepeatedBasenamesCannotChooseAnArbitraryNativePayload()
    {
        using var temp = new Fixture();
        string stage = temp.Extract("OpenSteamTools", ("one/dwmapi.dll", "first"), ("two/dwmapi.dll", "second"));
        Assert.Throws<InvalidDataException>(() => UnlockerService.FindWanted(stage, ["dwmapi.dll"]));
    }

    private sealed class Stub : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "LuaToolsIntegrationTests", Guid.NewGuid().ToString("N"));
        public Fixture() => System.IO.Directory.CreateDirectory(Directory);
        public string Extract(string component, params (string name, string value)[] files)
        {
            string archive = Path.Combine(Directory, Guid.NewGuid() + ".zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (var file in files)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(file.name).Open());
                    writer.Write(file.value);
                }
            string stage = Path.Combine(Directory, "stage-" + Guid.NewGuid());
            UserAuthorizedDependencies.ValidateArchiveAndExtract(component, archive, stage, _ => true, true);
            return stage;
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
