using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamPluginBridgeTests
{
    private const string Store = "https://store.steampowered.com/app/620/Portal_2/";
    private static JsonElement Args(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void SurfaceContainsOnlyReviewedMethodsAndNumericCurrentApp()
    {
        Assert.Equal(("POST", "/add/620"), SteamPluginBridge.Map("StartLuaToolsAdd", Args("{\"appid\":620,\"name\":\"Portal 2\"}"), Store));
        Assert.Equal(("GET", "/loaded-apps"), SteamPluginBridge.Map("ReadLoadedApps", Args("{}"), "https://store.steampowered.com/"));
        Assert.Throws<InvalidOperationException>(() => SteamPluginBridge.Map("StartAddViaLuaToolsFromUrl", Args("{\"appid\":620}"), Store));
        Assert.Throws<InvalidOperationException>(() => SteamPluginBridge.Map("Logger", Args("{}"), Store));
    }

    [Theory]
    [InlineData("{\"appid\":\"620\"}")]
    [InlineData("{\"appid\":0}")]
    [InlineData("{\"appid\":-1}")]
    [InlineData("{\"appid\":620.5}")]
    [InlineData("{\"appid\":4294967296}")]
    [InlineData("{\"appid\":620,\"url\":\"https://evil.test\"}")]
    [InlineData("{\"appid\":620,\"appid\":620}")]
    public void InvalidOrUnexpectedArgumentsFailBeforeDispatch(string raw) =>
        Assert.Throws<InvalidDataException>(() => SteamPluginBridge.Map("DeleteLuaToolsForApp", Args(raw), Store));

    [Theory]
    [InlineData("https://store.steampowered.com/app/621/")]
    [InlineData("https://store.steampowered.com/")]
    [InlineData("https://store.steampowered.com.evil.test/app/620/")]
    [InlineData("https://store.steampowered.com:444/app/620/")]
    [InlineData("http://store.steampowered.com/app/620/")]
    [InlineData("https://user@store.steampowered.com/app/620/")]
    public void FreshPageScopeMustMatchAppAndExactStoreOrigin(string url) =>
        Assert.Throws<InvalidOperationException>(() => SteamPluginBridge.Map("OpenFix", Args("{\"appid\":620}"), url));

    [Theory]
    [InlineData("https://steamdb.info/app/620/", true)]
    [InlineData("https://discord.gg/luatools", true)]
    [InlineData("https://discord.com/channels/123", false)]
    [InlineData("https://steamdb.info.evil.test/app/620/", false)]
    [InlineData("https://steamdb.info/app/620/?redirect=evil", false)]
    [InlineData("https://steamdb.info/app/0/", false)]
    [InlineData("steam://install/620", false)]
    [InlineData("file:///C:/Windows/system32/cmd.exe", false)]
    public void ExternalLinksAreNarrowlyScoped(string url, bool allowed) =>
        Assert.Equal(allowed, SteamPluginBridge.IsAllowedExternalUrl(url));

    [Theory]
    [InlineData("ws://127.0.0.1:8080/devtools/page/123-ABC", true)]
    [InlineData("ws://127.1:8080/devtools/page/123", false)]
    [InlineData("ws://2130706433:8080/devtools/page/123", false)]
    [InlineData("ws://localhost:8080/devtools/page/123", false)]
    [InlineData("ws://127.0.0.1:8080/devtools/page/a/../123", false)]
    [InlineData("ws://127.0.0.1:8080/devtools/page/%31", false)]
    [InlineData("ws://127.0.0.1:8080/devtools/page/123?x=1", false)]
    [InlineData("ws://127.0.0.1:8080/devtools/page/123#x", false)]
    [InlineData("ws://127.0.0.1:8081/devtools/page/123", false)]
    [InlineData("ws://user@127.0.0.1:8080/devtools/page/123", false)]
    public void DebuggerEndpointRejectsUrlNormalizationAndAlternateHosts(string url, bool allowed) =>
        Assert.Equal(allowed, CefInjectorService.IsAllowedDebuggerEndpoint(url));

    [Fact]
    public async Task CdpCorrelatesIdsAndAlwaysSelectsIsolatedContext()
    {
        using var socket = new ScriptedSocket(
            "{\"method\":\"Page.frameNavigated\",\"params\":{}}",
            "{\"id\":999,\"result\":{}}",
            "{\"id\":1,\"result\":{\"result\":{\"type\":\"string\",\"value\":\"right response\"}}}");
        using var cdp = new SteamCdpConnection(socket);
        Assert.Equal("right response", await cdp.EvaluateAsync(47, "window.LuaToolsBridge.__drain()", default));
        using var sent = JsonDocument.Parse(Assert.Single(socket.Sent));
        Assert.Equal("Runtime.evaluate", sent.RootElement.GetProperty("method").GetString());
        Assert.Equal(47, sent.RootElement.GetProperty("params").GetProperty("contextId").GetInt32());
        Assert.True(sent.RootElement.GetProperty("params").GetProperty("returnByValue").GetBoolean());
    }

    [Fact]
    public async Task CdpReassemblesUtf8AcrossFragmentBoundaries()
    {
        using var socket = new ScriptedSocket("{\"id\":1,\"result\":{\"result\":{\"value\":\"é??\"}}}") { FragmentSize = 1 };
        using var cdp = new SteamCdpConnection(socket);
        Assert.Equal("é??", await cdp.EvaluateAsync(1, "'unused'", default));
    }

    [Fact]
    public async Task CdpErrorsAndEvaluationExceptionsCannotBecomeSuccess()
    {
        using var socket = new ScriptedSocket("{\"id\":1,\"error\":{\"message\":\"No context\"}}");
        using var cdp = new SteamCdpConnection(socket);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cdp.EvaluateAsync(1, "'unused'", default));
        using var failedSocket = new ScriptedSocket("{\"id\":1,\"result\":{\"exceptionDetails\":{}}}");
        using var failedCdp = new SteamCdpConnection(failedSocket);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failedCdp.EvaluateAsync(1, "'unused'", default));
    }

    [Fact]
    public async Task CdpBoundsActualMessageBytesAndEventFlood()
    {
        using var socket = new ScriptedSocket(new string('x', SteamCdpConnection.MaximumMessageBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => SteamCdpConnection.ReadMessageAsync(socket, default));
        using var flood = new ScriptedSocket(Enumerable.Repeat("{\"method\":\"unrelated\"}", 128).ToArray());
        using var cdp = new SteamCdpConnection(flood);
        await Assert.ThrowsAsync<InvalidDataException>(() => cdp.CommandAsync("Page.getFrameTree", new { }, default));
    }

    [Fact]
    public async Task CdpCancellationRejectsMissingResponse()
    {
        using var socket = new ScriptedSocket();
        using var cdp = new SteamCdpConnection(socket);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cdp.CommandAsync("Page.getFrameTree", new { }, cancellation.Token));
    }

    private sealed class ScriptedSocket(params string[] messages) : WebSocket
    {
        private readonly Queue<byte[]> _messages = new(messages.Select(Encoding.UTF8.GetBytes));
        private int _offset;
        public int FragmentSize { get; init; } = 8192;
        public List<string> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_messages.Count == 0) await Task.Delay(Timeout.Infinite, cancellationToken);
            var message = _messages.Peek();
            int count = Math.Min(Math.Min(FragmentSize, buffer.Count), message.Length - _offset);
            Array.Copy(message, _offset, buffer.Array!, buffer.Offset, count);
            _offset += count;
            bool done = _offset == message.Length;
            if (done) { _messages.Dequeue(); _offset = 0; }
            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, done);
        }
    }
}
