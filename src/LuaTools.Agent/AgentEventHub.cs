using System.Runtime.CompilerServices;
using System.Threading.Channels;
namespace LuaTools.Agent;

/// <summary>Bounded observational events; publication never blocks jobs, the dispatcher, or log writes.</summary>
public sealed class AgentEventHub(string instanceId)
{
    private readonly object _gate = new();
    private readonly Queue<(string Cursor, Envelope Frame)> _recent = new();
    private readonly HashSet<Channel<Envelope>> _readers = [];
    private long _sequence;
    public void Publish(string category, string kind, object? payload, string? jobId = null)
    {
        lock (_gate)
        {
            string cursor = instanceId + ":" + ++_sequence;
            var frame = Envelope.Success(new { @event = new { id = cursor, cursor, instance_id = instanceId, time = DateTimeOffset.UtcNow, category, kind, job_id = jobId, payload } });
            _recent.Enqueue((cursor, frame)); while (_recent.Count > 512) _recent.Dequeue();
            foreach (var reader in _readers)
            {
                if (reader.Writer.TryWrite(frame)) continue;
                while (reader.Reader.TryRead(out _)) { }
                reader.Writer.TryWrite(Envelope.Success(new { @event = new { kind = "GAP", resnapshot_required = true, cursor, instance_id = instanceId } }));
            }
        }
    }
    public async IAsyncEnumerable<Envelope> Watch(string? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false });
        Envelope[] backlog;
        lock (_gate)
        {
            if (_readers.Count >= 16) backlog = [Envelope.Fail("BUSY", 7, "Event reader limit reached.")];
            else
            {
                var retained = _recent.ToArray(); int index = cursor is null ? retained.Length - 1 : Array.FindIndex(retained, x => x.Cursor == cursor);
                backlog = cursor is not null && index < 0 ? [Envelope.Fail("RESET_REQUIRED", 3, "Refresh the resource snapshot before resuming events.")]
                    : retained.Skip(index + 1).Select(x => x.Frame).ToArray();
                if (backlog.Length == 0 || backlog.All(x => x.Ok)) _readers.Add(channel);
            }
        }
        try
        {
            foreach (var frame in backlog) { yield return frame; if (!frame.Ok) yield break; }
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                bool ready = false;
                try { ready = await channel.Reader.WaitToReadAsync(heartbeat.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                if (ready) { while (channel.Reader.TryRead(out var frame)) yield return frame; }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    string current; lock (_gate) current = instanceId + ":" + _sequence;
                    yield return Envelope.Success(new { @event = new { kind = "heartbeat", instance_id = instanceId, cursor = current } });
                }
            }
        }
        finally { lock (_gate) _readers.Remove(channel); channel.Writer.TryComplete(); }
    }
}
