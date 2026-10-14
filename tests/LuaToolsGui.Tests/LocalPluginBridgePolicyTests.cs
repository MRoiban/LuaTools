using System.IO;
using System.Net;
using System.Text;
using LuaToolsGui.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

public class LocalPluginBridgePolicyTests
{
    [Fact]
    public void EnabledLocalRequestNeedsExactProcessToken()
    {
        var uri = new Uri("http://127.0.0.1:6767/add/620");
        Assert.True(LocalPluginBridgePolicy.IsAuthorized(true, uri, "127.0.0.1:6767", IPAddress.Loopback, true, null, LocalPluginBridgePolicy.Token));
        Assert.False(LocalPluginBridgePolicy.IsAuthorized(true, uri, "127.0.0.1:6767", IPAddress.Loopback, true, null, null));
        Assert.False(LocalPluginBridgePolicy.IsAuthorized(true, uri, "127.0.0.1:6767", IPAddress.Loopback, true, null, new string('x', LocalPluginBridgePolicy.Token.Length)));
        Assert.False(LocalPluginBridgePolicy.IsAuthorized(false, uri, "127.0.0.1:6767", IPAddress.Loopback, true, null, LocalPluginBridgePolicy.Token));
    }

    [Theory]
    [InlineData("http://localhost:6767/add/620", "localhost:6767", "127.0.0.1", true, null)]
    [InlineData("http://127.0.0.1:6767/add/620", "evil.example:6767", "127.0.0.1", true, null)]
    [InlineData("http://127.0.0.1:8080/add/620", "127.0.0.1:8080", "127.0.0.1", true, null)]
    [InlineData("http://127.0.0.1:6767/add/620", "127.0.0.1:6767", "192.168.1.5", false, null)]
    [InlineData("http://127.0.0.1:6767/add/620", "127.0.0.1:6767", "2001::7f00:1", true, null)]
    [InlineData("http://127.0.0.1:6767/add/620", "127.0.0.1:6767", "127.0.0.1", true, "https://store.steampowered.com")]
    [InlineData("http://127.0.0.1:6767/add/620", "127.0.0.1:6767", "127.0.0.1", true, "null")]
    public void WebsiteOriginsHostConfusionAndNonlocalPeersAreDenied(string url, string host, string address, bool local, string? origin) =>
        Assert.False(LocalPluginBridgePolicy.IsAuthorized(true, new Uri(url), host, IPAddress.Parse(address), local, origin, LocalPluginBridgePolicy.Token));

    [Fact]
    public async Task SmallUtf8BodyPreservesOriginalJsonContract()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"name\":\"Épreuve\",\"source\":\"Ryuu\"}");
        using var stream = new MemoryStream(bytes);
        Assert.Equal(Encoding.UTF8.GetString(bytes), await LocalPluginBridgePolicy.ReadBodyAsync(stream, bytes.Length, default));
    }

    [Fact]
    public async Task BodyLimitRejectsActualOverflowWithoutContentLength()
    {
        using var stream = new MemoryStream(new byte[LocalPluginBridgePolicy.MaximumBodyBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalPluginBridgePolicy.ReadBodyAsync(stream, -1, default));
    }

    [Fact]
    public async Task InvalidUtf8CannotBecomeJsonRequest()
    {
        using var stream = new MemoryStream(new byte[] { 0xff });
        await Assert.ThrowsAsync<DecoderFallbackException>(() => LocalPluginBridgePolicy.ReadBodyAsync(stream, 1, default));
    }

    [Fact]
    public async Task HugeDeclaredBodyFailsBeforeReadingAndTruncationFails()
    {
        using var stream = new MemoryStream(new byte[1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalPluginBridgePolicy.ReadBodyAsync(stream, LocalPluginBridgePolicy.MaximumBodyBytes + 1, default));
        Assert.Equal(0, stream.Position);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalPluginBridgePolicy.ReadBodyAsync(stream, 2, default));
    }

    [Fact]
    public async Task BodyDeadlineDoesNotDependOnStreamHonoringCancellation()
    {
        using var stream = new NeverReadingStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalPluginBridgePolicy.ReadBodyAsync(stream, -1, default, TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task DefaultOffStartAndRepeatedStopDoNotCreateListener()
    {
        var settings = new SettingsService(new AppSettings());
        var server = new HttpServerService(null!, null!, null!, null!, NullLogger<HttpServerService>.Instance, settings);
        await server.StartAsync(default);
        await server.StartAsync(default);
        await server.StopAsync(default);
        await server.StopAsync(default);
    }

    private sealed class NeverReadingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(new TaskCompletionSource<int>().Task);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
