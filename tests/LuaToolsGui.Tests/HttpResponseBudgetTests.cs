using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class HttpResponseBudgetTests
{
    private static readonly HttpResponseBudget Small = new(32, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task UnknownLengthDefaultBufferingIsBounded()
    {
        using var http = Client(new NonSeekableStream(new byte[33]));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://lua.tools/public"));
    }

    [Fact]
    public async Task DeclaredSmallLengthCannotHideStreamingOverflow()
    {
        var content = new StreamContent(new NonSeekableStream(new byte[33]));
        content.Headers.ContentLength = 1;
        using var http = Client(content);
        using var response = await http.GetAsync("https://lua.tools/public", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task HugeDeclaredBodyRejectedBeforeReading()
    {
        var source = new NonSeekableStream(new byte[1]);
        var content = new StreamContent(source);
        content.Headers.ContentLength = long.MaxValue;
        using var http = Client(content);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://lua.tools/public", HttpCompletionOption.ResponseHeadersRead));
        Assert.Equal(0, source.BytesRead);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task InBudgetContentAndHeadersPreserved()
    {
        var content = new StringContent("{\"ok\":true}");
        content.Headers.ContentDisposition = new("attachment") { FileName = "info.json" };
        using var http = Client(content);
        using var response = await http.GetAsync("https://lua.tools/public");
        Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync());
        Assert.Equal("info.json", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task RequestBudgetOverridesDefaultForKnownLargeResponse()
    {
        using var http = Client(new NonSeekableStream(new byte[33]));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://lua.tools/public");
        new HttpResponseBudget(64, TimeSpan.FromSeconds(1)).Apply(req);
        using var response = await http.SendAsync(req);
        Assert.Equal(33, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task StreamingDeadlineAppliesAfterHeadersAndCannotBeBypassedByIgnoringCancellation()
    {
        using var http = Client(new HangingStream(), new(32, TimeSpan.FromMilliseconds(30)));
        using var response = await http.GetAsync("https://lua.tools/public", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[1]).AsTask());
    }

    [Fact]
    public async Task StreamingCallerCancellationIsPreserved()
    {
        using var http = Client(new HangingStream());
        using var response = await http.GetAsync("https://lua.tools/public", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[1], cancellation.Token).AsTask());
    }

    [Fact]
    public async Task DisposingResponseDisposesOriginalStream()
    {
        var source = new NonSeekableStream([1]);
        using var http = Client(source);
        var response = await http.GetAsync("https://lua.tools/public", HttpCompletionOption.ResponseHeadersRead);
        response.Dispose();
        response.Dispose();
        Assert.True(source.Disposed);
    }

    private static HttpClient Client(Stream source, HttpResponseBudget? budget = null) => Client(new StreamContent(source), budget);
    private static HttpClient Client(HttpContent content, HttpResponseBudget? budget = null) =>
        AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(content), budget ?? Small);

    private sealed class Stub(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public long BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { int read = _inner.Read(buffer, offset, count); BytesRead += read; return read; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { int read = _inner.Read(buffer.Span); BytesRead += read; return ValueTask.FromResult(read); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; _inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class HangingStream() : NonSeekableStream([])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            new(new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
    }
}
