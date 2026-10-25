using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamDebugEndpointDiscoveryTests
{
    private static readonly SteamDebugEndpointDiscovery.Listener[] Listeners =
    [
        new("127.0.0.1", @"C:\Steam\bin\cef\steamwebhelper.exe"),
        new("[::1]", @"C:\Steam-Secondary\bin\cef\steamwebhelper.exe")
    ];

    [Fact]
    public void SelectedInstallationDeterminesListenerWhenBothClientsRun()
    {
        Assert.Equal("http://127.0.0.1:8080/json", SteamDebugEndpointDiscovery.Select(@"C:\Steam", Listeners));
        Assert.Equal("http://[::1]:8080/json", SteamDebugEndpointDiscovery.Select(@"C:\Steam-Secondary", Listeners));
        Assert.Null(SteamDebugEndpointDiscovery.Select(@"C:\OtherSteam", Listeners));
        Assert.Null(SteamDebugEndpointDiscovery.Select(null, Listeners));
    }

    [Fact]
    public void AnotherInstallationOrProcessCannotSupplySelectedListener()
    {
        Assert.Null(SteamDebugEndpointDiscovery.Select(@"C:\Steam", [Listeners[1]]));
        Assert.Null(SteamDebugEndpointDiscovery.Select(@"C:\Steam", [new("127.0.0.1", @"C:\Steam\other.exe")]));
        Assert.Null(SteamDebugEndpointDiscovery.Select(@"C:\Steam", [new("192.168.1.2", Listeners[0].ExecutablePath)]));
        Assert.Null(SteamDebugEndpointDiscovery.Select(@"C:\Steam", [new("127.0.0.1", @"C:\Steam\..\Other\steamwebhelper.exe")]));
    }

    [Theory]
    [InlineData("ws://[::1]:8080/devtools/page/123-ABC", true)]
    [InlineData("ws://[0:0:0:0:0:0:0:1]:8080/devtools/page/123-ABC", true)]
    [InlineData("ws://[0000:0000:0000:0000:0000:0000:0000:0001]:8080/devtools/page/123-ABC", true)]
    [InlineData("ws://127.0.0.1:8080/devtools/page/123", false)]
    [InlineData("ws://[::ffff:127.0.0.1]:8080/devtools/page/123", false)]
    [InlineData("ws://[::1]:8081/devtools/page/123", false)]
    [InlineData("ws://user@[::1]:8080/devtools/page/123", false)]
    [InlineData("ws://[::1]:8080/devtools/page/a/../123", false)]
    [InlineData("ws://[::1]:8080/devtools/page/123?x=1", false)]
    public void Ipv6SelectionOnlyAcceptsMatchingLiteralDebugger(string endpoint, bool allowed) =>
        Assert.Equal(allowed, CefInjectorService.IsAllowedDebuggerEndpoint(endpoint, "http://[::1]:8080/json"));
}
