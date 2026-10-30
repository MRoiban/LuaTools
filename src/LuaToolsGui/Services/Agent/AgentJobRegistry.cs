using System.Collections.Specialized;
using System.Text.Json;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;

/// <summary>Observes the existing queue; owns no scheduler. All queue-facing methods run on the dispatcher.</summary>
public sealed class AgentJobRegistry
{
    private readonly DownloadQueue _queue;
    private readonly Dictionary<string, (DateTimeOffset Seen, DownloadItem Item)> _items = [];
    private readonly Dictionary<string, Operation> _operations = [];
    private sealed class Operation(string id, string kind, Func<CancellationToken, Task<Envelope>> run, CancellationToken lifetime)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public string State { get; set; } = "running";
        public Envelope? Result { get; set; }
        public CancellationToken Lifetime { get; } = lifetime;
        public Func<CancellationToken, Task<Envelope>> Run { get; } = run;
    }
    public AgentJobRegistry(DownloadQueue queue)
    {
        _queue = queue;
        queue.Items.CollectionChanged += (_, _) => Capture();
        Capture();
    }
    private void Capture()
    {
        foreach (var item in _queue.Items) _items[item.Id] = (DateTimeOffset.UtcNow, item);
        foreach (string id in _items.Where(p => !p.Value.Item.IsActive && p.Value.Seen < DateTimeOffset.UtcNow.AddHours(-24)).Select(p => p.Key).ToArray()) _items.Remove(id);
        while (_items.Count > 1000)
        {
            var candidate = _items.Where(p => !p.Value.Item.IsActive).OrderBy(p => p.Value.Seen).FirstOrDefault();
            if (candidate.Key is null) break;
            _items.Remove(candidate.Key);
        }
    }
    public DownloadItem? Find(string id) { Capture(); return _items.GetValueOrDefault(id).Item; }
    public object[] List(bool active = false)
    {
        Capture();
        return _items.Values.Where(x => !active || x.Item.IsActive).Select(x => Describe(x.Item))
            .Concat(_operations.Values.Where(x => !active || x.State == "running").Select(x => (object)new { id = x.Id, state = x.State, kind = x.Kind, version = x.State, available_actions = Array.Empty<object>() })).ToArray();
    }
    public object? Get(string id) => Find(id) is { } item ? Describe(item) : _operations.TryGetValue(id, out var op)
        ? new { id = op.Id, state = op.State, kind = op.Kind, result = op.Result, version = op.State, available_actions = Array.Empty<object>() } : null;
    public static string Version(DownloadItem item) => MutationCoordinator.Hash(item.Id + ":" + item.Status);
    public static object Describe(DownloadItem item) => new
    {
        id = item.Id, state = JsonNamingPolicy.SnakeCaseLower.ConvertName(item.Status.ToString()), kind = item.Job.Kind.ToString(),
        version = Version(item), bytes = item.BytesRead, total_bytes = item.TotalBytes, rate = item.BytesPerSecond,
        eta_seconds = item.Eta?.TotalSeconds, cancel_requested = item.Cts.IsCancellationRequested && !item.PauseRequested,
        enqueued_at = item.EnqueuedAt, available_actions = AvailableActions.For("job", c => c.Verb switch
        {
            "pause" when !item.CanPause => "PAUSE_UNSUPPORTED", "resume" when !item.CanResume => "NOT_PAUSED",
            "retry" when !item.CanRetry => "RETRY_UNSUPPORTED", "cancel" when !item.CanCancel => "CANCEL_UNSUPPORTED",
            "confirm" when !ConsentCoordinator.HasPending(item.Id) => "NO_PENDING_DECISION", _ => null,
        }),
    };
    public string Register(string kind, Func<CancellationToken, Task<Envelope>> run, CancellationToken hostLifetime)
    {
        foreach (string id in _operations.Where(p => p.Value.State != "running" && p.Value.Created < DateTimeOffset.UtcNow.AddHours(-24)).Select(p => p.Key).ToArray()) _operations.Remove(id);
        if (_operations.Count >= 1000) throw new MutationRejectedException("BUSY", 7);
        var op = new Operation(Guid.NewGuid().ToString("N"), kind, run, hostLifetime); _operations.Add(op.Id, op);
        _ = RunAsync(op); return op.Id;
    }
    private static async Task RunAsync(Operation op)
    {
        try { op.Result = await op.Run(op.Lifetime); op.State = op.Result.Ok ? "completed" : "failed"; }
        catch (OperationCanceledException) { op.State = "cancelled"; }
        catch { op.State = "failed"; }
    }
}

/// <summary>GUI and CLI decision adapter; queue factories can use WaitAsync as ConfirmAsync.</summary>
public static class ConsentCoordinator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> Pending = new();
    public static bool HasPending(string id) => Pending.ContainsKey(id);
    public static async Task<bool> WaitAsync(string id, CancellationToken ct)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Pending.TryAdd(id, decision)) throw new InvalidOperationException();
        try { return await decision.Task.WaitAsync(ct); }
        finally { Pending.TryRemove(id, out _); }
    }
    public static bool Decide(string id, bool replace) => Pending.TryGetValue(id, out var decision) && decision.TrySetResult(replace);
}
