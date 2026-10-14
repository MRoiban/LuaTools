using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SafeImageLoaderTests
{
    [Theory]
    [InlineData("https://avatars.steamstatic.com/avatar.jpg")]
    [InlineData("http://127.0.0.1/image.jpg")]
    [InlineData(@"\\server\share\image.jpg")]
    [InlineData("//server/share/image.jpg")]
    [InlineData("file://server/share/image.jpg")]
    [InlineData("file:///C:/image.jpg")]
    [InlineData(@"C:image.jpg")]
    [InlineData("image.jpg")]
    [InlineData(@"\\?\UNC\server\share\image.jpg")]
    public void LocalDecoderRejectsNetworkAndNonAbsolutePaths(string path)
    {
        Assert.False(SafeImageLoader.IsLocalPath(path));
        Assert.Null(SafeImageLoader.LoadLocal(path));
    }

    [Theory]
    [InlineData("https://192.168.1.1/image.jpg")]
    [InlineData("http://avatars.steamstatic.com/image.jpg")]
    [InlineData("https://127.0.0.1/image.jpg")]
    public async Task RemoteLoaderRejectsUnapprovedDestinationsWithoutTransport(string url)
    {
        Assert.Null(await SafeImageLoader.LoadAsync(url));
    }

    [Fact]
    public async Task BodyLimitAppliesWithoutContentLength()
    {
        using var stream = new MemoryStream(new byte[SafeImageLoader.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SafeImageLoader.ReadBoundedAsync(stream));
    }

    [Fact]
    public async Task BoundedBodyPreservesImageBytes()
    {
        byte[] bytes = [1, 2, 3, 4];
        using var stream = new MemoryStream(bytes);
        Assert.Equal(bytes, await SafeImageLoader.ReadBoundedAsync(stream));
    }

    [Fact]
    public async Task PublicImageOriginDecodesWithoutAccountHeaders()
    {
        var transport = new FakeImageTransport(HttpStatusCode.OK, CreateBitmap());
        const string source = "https://dynamic-cdn.example/avatar.bmp";
        var image = await SafeImageLoader.LoadAsync(source, clientFactory: uri =>
            AppHttp.CreateExternalDataForTesting(TimeSpan.FromSeconds(1), uri, transport, HttpResponseBudget.Image));
        Assert.NotNull(image);
        Assert.True(image.IsFrozen);
        Assert.Equal(1, transport.Calls);
        Assert.Empty(transport.Headers);
        Assert.Equal(source, transport.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.OK)]
    public async Task RedirectOrMalformedDataStaysBlank(HttpStatusCode status)
    {
        var transport = new FakeImageTransport(status, [1, 2, 3]);
        var image = await SafeImageLoader.LoadAsync("https://dynamic-cdn.example/avatar.bmp", clientFactory: uri =>
            AppHttp.CreateExternalDataForTesting(TimeSpan.FromSeconds(1), uri, transport, HttpResponseBudget.Image));
        Assert.Null(image);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData("https://user:password@dynamic-cdn.example/image.bmp")]
    [InlineData("https://127.0.0.1/image.bmp")]
    [InlineData("https://192.168.1.1/image.bmp")]
    [InlineData("http://dynamic-cdn.example/image.bmp")]
    public async Task HostileDestinationsNeverReachImageTransport(string source)
    {
        var transport = new FakeImageTransport(HttpStatusCode.OK, CreateBitmap());
        var image = await SafeImageLoader.LoadAsync(source, clientFactory: uri =>
            AppHttp.CreateExternalDataForTesting(TimeSpan.FromSeconds(1), uri, transport, HttpResponseBudget.Image));
        Assert.Null(image);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public void LocalImageDecodesFrozenAndReleasesFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "lt-image-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            File.WriteAllBytes(path, CreateBitmap());
            var image = SafeImageLoader.LoadLocal(path);
            Assert.NotNull(image);
            Assert.True(image.IsFrozen);
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    private static byte[] CreateBitmap()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        // A 1x1, 24-bit BMP (four-byte padded pixel row).
        writer.Write((ushort)0x4d42);
        writer.Write(58);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(1);
        writer.Write(1);
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(new byte[24]);
        writer.Write(new byte[] { 0, 0, 255, 0 });
        return bytes.ToArray();
    }

    private sealed class FakeImageTransport(HttpStatusCode status, byte[] data) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? RequestUri { get; private set; }
        public List<string> Headers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            RequestUri = request.RequestUri;
            Headers.AddRange(request.Headers.Select(h => h.Key));
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(data) };
            if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://127.0.0.1/image.bmp");
            return Task.FromResult(response);
        }
    }
}
