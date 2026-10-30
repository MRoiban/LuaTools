using System.Collections.Specialized;
using System.Text.Json;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;

/// <summary>Observes the existing queue; owns no scheduler. All queue-facing methods run on the dispatcher.</summary>
public sealed class AgentJobRegistry
{
    private readonly DownloadQueue _queue;
    internal LocalInstallCoordinator? Local { get; set; }
    public AgentEventHub Events { get; internal set; } = new(Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, System.ComponentModel.PropertyChangedEventHandler> _observed = [];
    private readonly Dictionary<string, DateTimeOffset> _progressAt = [];
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
    public int ActiveOperations => _operations.Values.Count(x => x.State == "running") + (Local?.Operations.Count(x => x.State == "running") ?? 0);
    private void Capture()
    {
        foreach (var item in _queue.Items)
        {
            _items[item.Id] = (DateTimeOffset.UtcNow, item);
            if (_observed.ContainsKey(item.Id)) continue;
            Events.Publish("download", "queued", Describe(item), item.Id);
            System.ComponentModel.PropertyChangedEventHandler observer = (_, change) =>
            {
                bool terminal = change.PropertyName == nameof(DownloadItem.Status);
                if (!terminal && _progressAt.GetValueOrDefault(item.Id) > DateTimeOffset.UtcNow.AddMilliseconds(-100)) return;
                _progressAt[item.Id] = DateTimeOffset.UtcNow;
                Events.Publish("download", terminal ? "state" : "progress", Describe(item), item.Id);
            };
            item.PropertyChanged += observer; _observed[item.Id] = observer;
        }
        foreach (string id in _items.Where(p => !p.Value.Item.IsActive && p.Value.Seen < DateTimeOffset.UtcNow.AddHours(-24)).Select(p => p.Key).ToArray()) Forget(id);
        while (_items.Count > 1000)
        {
            var candidate = _items.Where(p => !p.Value.Item.IsActive).OrderBy(p => p.Value.Seen).FirstOrDefault();
            if (candidate.Key is null) break;
            Forget(candidate.Key);
        }
    }
    private void Forget(string id)
    {
        if (_items.Remove(id, out var retained) && _observed.Remove(id, out var observer)) retained.Item.PropertyChanged -= observer;
        _progressAt.Remove(id);
    }
    public DownloadItem? Find(string id) { Capture(); return _items.GetValueOrDefault(id).Item; }
    public object[] List(bool active = false)
    {
        Capture();
        return _items.Values.Where(x => !active || x.Item.IsActive).Select(x => Describe(x.Item))
            .Concat((Local?.Operations ?? []).Where(b => !active || b.State == "running").Select(LocalRow))
            .Concat(_operations.Values.Where(x => !active || x.State == "running").Select(x => (object)new { id = x.Id, state = x.State, kind = x.Kind, version = x.State, available_actions = OperationActions() })).ToArray();
    }
    public object? Get(string id) => Find(id) is { } item ? Describe(item) : _queue.History.FirstOrDefault(x => x.Id == id) is { } history
        ? new { id = history.Id, state = JsonNamingPolicy.SnakeCaseLower.ConvertName(history.Record.Status.ToString()), kind = history.Record.Kind, message = LogRedactor.Redact(history.Record.Message ?? ""), paths = new { output = history.Record.RevealPath }, result = new { ok = history.Record.Status == "Completed", message = LogRedactor.Redact(history.Record.Message ?? "") }, version = "history", available_actions = OperationActions() }
        : Local?.Operations.FirstOrDefault(b => b.Id == id) is { } batch ? LocalRow(batch)
        : _operations.TryGetValue(id, out var op)
        ? new { id = op.Id, state = op.State, kind = op.Kind, result = op.Result, version = op.State, available_actions = OperationActions() } : null;
    private static object LocalRow(LocalInstallCoordinator.Batch batch) => new { id = batch.Id, state = ConsentCoordinator.HasPending(batch.Id) ? "awaiting_confirmation" : batch.State, kind = "local-install", result = new { results = batch.Results.Select(r => new { r.Name, r.Ok, r.Skipped, r.LuaInstalled, r.ManifestCount, r.FailedCount, code = r.Ok ? null : "INSTALL_FAILED", message = r.Ok ? null : Resources.Strings.Agent_Add_InstallFailed }) }, pending_decision = ConsentCoordinator.Details(batch.Id), version = LocalVersion(batch), available_actions = AvailableActions.For("job", c => c.ReadOnly || c.Verb == "confirm" && ConsentCoordinator.HasPending(batch.Id) ? null : "ACTION_UNSUPPORTED") };
    internal static string LocalVersion(LocalInstallCoordinator.Batch batch) => MutationCoordinator.Hash(batch.Id + batch.State + batch.DecisionVersion + JsonSerializer.Serialize(ConsentCoordinator.Details(batch.Id), Protocol.Json));
    public static string Version(DownloadItem item) => MutationCoordinator.Hash(item.Id + ":" + item.Status);
    public static object Describe(DownloadItem item) => new
    {
        id = item.Id, state = JsonNamingPolicy.SnakeCaseLower.ConvertName(item.Status.ToString()), kind = item.Job.Kind.ToString(),
        version = Version(item), bytes = item.BytesRead, total_bytes = item.TotalBytes, rate = item.BytesPerSecond,
        eta_seconds = item.Eta?.TotalSeconds, cancel_requested = item.Cts.IsCancellationRequested && !item.PauseRequested,
        message = LogRedactor.Redact(item.Message ?? ""), paths = new { output = item.RevealPath ?? item.Job.OutputPath }, pending_decision = ConsentCoordinator.Details(item.Id),
        result = item.Completion.IsCompletedSuccessfully && item.Completion.Result is { } result ? new { ok = result.Ok, message = LogRedactor.Redact(result.Message ?? "") } : item.Status is DownloadStatus.Failed or DownloadStatus.Cancelled ? new { ok = false, message = LogRedactor.Redact(item.Message ?? "") } : null,
        enqueued_at = item.EnqueuedAt, available_actions = AvailableActions.For("job", c => c.Verb switch
        {
            "pause" when !item.CanPause => "PAUSE_UNSUPPORTED", "resume" when !item.CanResume || item.RunningAttempt is { IsCompleted: false } => "NOT_PAUSED",
            "retry" when !item.CanRetry => "RETRY_UNSUPPORTED", "cancel" when !item.CanCancel => "CANCEL_UNSUPPORTED",
            "confirm" when !ConsentCoordinator.HasPending(item.Id) => "NO_PENDING_DECISION", _ => null,
        }),
    };
    private static object[] OperationActions() => AvailableActions.For("job", c => c.ReadOnly ? null : "ACTION_UNSUPPORTED");
    public string Register(string kind, Func<CancellationToken, Task<Envelope>> run, CancellationToken hostLifetime)
    {
        foreach (string id in _operations.Where(p => p.Value.State != "running" && p.Value.Created < DateTimeOffset.UtcNow.AddHours(-24)).Select(p => p.Key).ToArray()) _operations.Remove(id);
        if (_operations.Count >= 1000) throw new MutationRejectedException("BUSY", 7);
        var op = new Operation(Guid.NewGuid().ToString("N"), kind, run, hostLifetime); _operations.Add(op.Id, op);
        Events.Publish("job", "running", new { id = op.Id, kind = op.Kind }, op.Id);
        _ = RunAsync(op); return op.Id;
    }
    private async Task RunAsync(Operation op)
    {
        try { op.Result = await op.Run(op.Lifetime); op.State = op.Result.Ok ? "completed" : "failed"; }
        catch (OperationCanceledException) { op.State = "cancelled"; }
        catch (MutationRejectedException ex) { op.Result = Envelope.Fail(ex.Code, ex.ExitCode, Resources.Strings.Agent_MutationRejected); op.State = "failed"; }
        catch { op.Result = Envelope.Fail("OPERATION_FAILED", 1, Resources.Strings.Agent_MutationRejected); op.State = "failed"; }
        Events.Publish("job", "state", new { id = op.Id, state = op.State, kind = op.Kind }, op.Id);
    }
}

/// <summary>GUI and CLI decision adapter; queue factories can use WaitAsync as ConfirmAsync.</summary>
public static class ConsentCoordinator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> Pending = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Descriptions = new();
    public static object? Details(string id) => Descriptions.GetValueOrDefault(id);
    public static async Task<bool> WaitAsync(string id, object details, CancellationToken ct)
    {
        Descriptions[id] = details;
        try { return await WaitAsync(id, ct); }
        finally { Descriptions.TryRemove(id, out _); }
    }
    public static bool HasPending(string id) => Pending.ContainsKey(id);
    public static Task<bool> WaitAsync(string id, CancellationToken ct) => WaitWithGuiAsync(id, null, ct);
    public static async Task<bool> WaitWithGuiAsync(string id, Task<bool>? guiDecision, CancellationToken ct, object? details = null)
    {
        if (details is not null) Descriptions[id] = details;
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Pending.TryAdd(id, decision)) throw new InvalidOperationException();
        try
        {
            if (guiDecision is null) return await decision.Task.WaitAsync(ct);
            var winner = await Task.WhenAny(decision.Task, guiDecision).WaitAsync(ct);
            return await winner;
        }
        finally { Pending.TryRemove(id, out _); Descriptions.TryRemove(id, out _); }
    }
    public static bool Decide(string id, bool replace) => Pending.TryGetValue(id, out var decision) && decision.TrySetResult(replace);
}
