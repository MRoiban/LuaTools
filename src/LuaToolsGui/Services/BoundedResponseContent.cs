using System.IO;
using System.Net;
using System.Net.Http;

namespace LuaToolsGui.Services;

/// <summary>Wraps transport content before HttpClient can buffer it; owns the original content.</summary>
internal sealed class BoundedResponseContent : HttpContent
{
    private readonly HttpContent _original;
    private readonly HttpResponseBudget _budget;
    private readonly CancellationTokenSource _deadline;
    private Task<Stream>? _stream;
    private bool _disposed;

    internal BoundedResponseContent(HttpContent original, HttpResponseBudget budget, CancellationToken requestCancellation)
    {
        budget.Validate();
        // A declared length is only an early reject, never proof that the actual body is bounded.
        if (original.Headers.ContentLength > budget.MaxBytes)
            throw new HttpRequestException("Response exceeds the permitted size.");
        _original = original;
        _budget = budget;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
        _deadline.CancelAfter(budget.BodyTimeout);
        foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }

    private Task<Stream> GetStreamAsync() => _stream ??= OpenStreamAsync();
    private async Task<Stream> OpenStreamAsync()
    {
        var stream = await _original.ReadAsStreamAsync(_deadline.Token).WaitAsync(_deadline.Token);
        return new BoundedReadStream(stream, _budget.MaxBytes, _deadline.Token);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() => GetStreamAsync();
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        GetStreamAsync().WaitAsync(cancellationToken);
    protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
        GetStreamAsync().WaitAsync(cancellationToken).GetAwaiter().GetResult();

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token, cancellationToken);
        var source = await GetStreamAsync().WaitAsync(linked.Token);
        await source.CopyToAsync(stream, 81920, linked.Token).WaitAsync(linked.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _deadline.Cancel();
            _original.Dispose();
            _deadline.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class BoundedReadStream(Stream inner, long maxBytes, CancellationToken deadline) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override int Read(Span<byte> buffer)
        {
            var temporary = new byte[Math.Min(buffer.Length, 81920)];
            int count = Read(temporary, 0, temporary.Length);
            temporary.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            // Read at most one byte beyond the budget to detect overflow without a large transient allocation.
            int wanted = (int)Math.Min(buffer.Length, Math.Min(int.MaxValue, maxBytes - _read + 1));
            int count = await inner.ReadAsync(buffer[..wanted], linked.Token).AsTask().WaitAsync(linked.Token);
            _read += count;
            if (_read > maxBytes) throw new HttpRequestException("Response exceeds the permitted size.");
            return count;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
