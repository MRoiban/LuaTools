using System.IO;
using LuaToolsGui.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamSessionRoutingTests
{
    [Fact]
    public void OrdinarySteamNeedsNoFalconFilesOrEnvironment()
    {
        const string steam = @"C:\Program Files (x86)\Steam\steam.exe";
        var environment = SteamProcessControl.ResolveEnvironment(steam, _ => null, file => file == steam);
        var start = SteamProcessControl.CreateStartInfo(steam, environment, file => file == steam);
        Assert.Equal(steam, start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Empty(start.ArgumentList);
        Assert.False(SteamProcessControl.HasIsolationContext(environment));
        Assert.Equal("http://127.0.0.1:8080/json", SteamDebugEndpointDiscovery.Select(
            @"C:\Program Files (x86)\Steam",
            [new("127.0.0.1", @"C:\Program Files (x86)\Steam\bin\cef\steamwebhelper.exe", 1)], 1));
    }

    [Fact]
    public void SameInstallationInAnotherSessionCannotReceiveCommands()
    {
        SteamDebugEndpointDiscovery.Listener[] listeners =
        [new("127.0.0.1", @"C:\Steam\bin\steamwebhelper.exe", 1, 8080),
         new("127.0.0.1", @"C:\Steam\bin\steamwebhelper.exe", 4, 43123)];
        Assert.Equal("http://127.0.0.1:43123/json", SteamDebugEndpointDiscovery.Select(@"C:\Steam", listeners, 4));
        Assert.Null(SteamDebugEndpointDiscovery.Select(@"C:\Steam", listeners, 2));
        Assert.Null(SteamDebugEndpointDiscovery.Select(@"C:\Steam-Secondary", listeners, 4));
        Assert.True(CefInjectorService.IsAllowedDebuggerEndpoint("ws://127.0.0.1:43123/devtools/page/ABC", "http://127.0.0.1:43123/json"));
        Assert.False(CefInjectorService.IsAllowedDebuggerEndpoint("ws://127.0.0.1:8080/devtools/page/ABC", "http://127.0.0.1:43123/json"));
    }

    [Fact]
    public void CabinetFriendsBrowserUsesTheSameSelectedEndpoint()
    {
        const string json = "[{\"title\":\"SharedJSContext\",\"webSocketDebuggerUrl\":\"ws://127.0.0.1:43123/devtools/page/ABC\"}]";
        Assert.NotNull(CabinetEngine.FriendLabels.SelectTarget(json, "http://127.0.0.1:43123/json"));
        Assert.Null(CabinetEngine.FriendLabels.SelectTarget(json, "http://127.0.0.1:8080/json"));
        Assert.Null(CabinetEngine.FriendLabels.SelectTarget(json, "http://[::1]:43123/json"));
    }

    [Fact]
    public async Task TwoBackendsRemainIndependentAndRejectStaleSelection()
    {
        var one = new SettingsService(new AppSettings { SteamPluginEnabled = true, SteamPathOverride = @"C:\One" });
        var two = new SettingsService(new AppSettings { SteamPluginEnabled = true, SteamPathOverride = @"C:\Two" });
        var a = Backend(one); var b = Backend(two);
        await a.StartAsync(default); await b.StartAsync(default);
        Assert.Equal(404, (await a.DispatchAsync("POST", "/unknown", "{}", @"C:\One", default)).Status);
        Assert.Equal(403, (await a.DispatchAsync("POST", "/open/fix/620", "{}", @"C:\Two", default)).Status);
        Assert.Equal(403, (await b.DispatchAsync("POST", "/open/fix/620", "{}", @"C:\One", default)).Status);
        await a.StopAsync(default);
        Assert.Equal(403, (await a.DispatchAsync("POST", "/unknown", "{}", @"C:\One", default)).Status);
        Assert.Equal(404, (await b.DispatchAsync("POST", "/unknown", "{}", @"C:\Two", default)).Status);
        two.SteamPathOverride = @"C:\Three";
        Assert.Equal(403, (await b.DispatchAsync("POST", "/open/fix/620", "{}", @"C:\Two", default)).Status);
        await b.StopAsync(default);
    }

    private static HttpServerService Backend(SettingsService settings) =>
        new(null!, new SteamService(settings), null!, null!, NullLogger<HttpServerService>.Instance, settings);

    [Fact]
    public void DesktopLaunchRecognizesFalconWithoutInheritedEnvironment()
    {
        const string steam = @"C:\Steam-Secondary\steam.exe";
        var environment = SteamProcessControl.ResolveEnvironment(steam, _ => null, _ => true);
        var start = SteamProcessControl.CreateStartInfo(steam, environment, _ => true);
        Assert.Equal(@"C:\Steam-Secondary\PortableSteam.exe", start.FileName);
        Assert.Equal(new[] { "--steam", steam, "--state", @"C:\Steam-Secondary\portable-state" }, start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.ResolveEnvironment(steam, _ => null,
            file => Path.GetFileName(file) == "SteamFL.exe"));
    }

    [Fact]
    public void ExplicitInheritedRouteIsNeverReplacedByGuessedFalconRoute()
    {
        string? Environment(string key) => key == SteamProcessControl.LauncherVariable ? @"C:\Missing\PortableSteam.exe" : null;
        var resolved = SteamProcessControl.ResolveEnvironment(@"C:\Steam\steam.exe", Environment, _ => true);
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.CreateStartInfo(@"C:\Steam\steam.exe", resolved, _ => true));
    }
}
