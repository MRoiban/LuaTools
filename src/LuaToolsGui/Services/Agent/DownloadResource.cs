using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("download"), AgentResource("queue")]
    private async Task<Envelope> DownloadAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "download watch")
        {
            string cursor = r.Flags.GetValueOrDefault("cursor") ?? Events.CurrentCursor;
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            observation.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await foreach (var frame in Events.Watch(cursor, observation.Token))
                {
                    if (!frame.Ok) return frame;
                    cursor = frame.Data?["event"]?["cursor"]?.ToString() ?? cursor;
                    if (frame.Data?["event"]?["category"]?.ToString() == "download" || frame.Data?["event"]?["kind"]?.ToString() == "reset") return frame;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            return Envelope.Success(new { @event = new { kind = "heartbeat", cursor } });
        }
        if (r.Command is "download list" or "queue list") return List(r, await DownloadSnapshotAsync(ct));
        return await _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            System.Text.Json.Nodes.JsonObject data;
            string? move, dataId;
            try
            {
                data = BulkSelection.Data(r, r.Command == "download update" ? ["id", "move"] : []);
                move = r.Flags.GetValueOrDefault("move") ?? data["move"]?.GetValue<string>();
                dataId = data["id"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new MutationRejectedException("INVALID_DATA", 2); }
            if (r.Command == "download update" && move is not ("up" or "down")) throw new MutationRejectedException("INVALID_DATA", 2);
            var selection = dataId is not null && r.Arguments.Length == 0 && !r.Flags.ContainsKey("ids") && !r.Flags.ContainsKey("where") ? r with { Arguments = [dataId] } : r;
            string[] ids = LibrarySelect(selection, Protocol.Node(queue.Items.Select(i => new { id = i.Id }))!.AsArray(), "id", ["id"]);
            var items = ids.Select(id => queue.Items.FirstOrDefault(i => i.Id == id)).ToArray();
            string Version() => MutationCoordinator.Hash(string.Join(':', queue.Items.Select(i => i.Id + AgentJobRegistry.Version(i))));
            string version = Version(), context = PlanContext;
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), context, version, ids, [r.Command], false), commit => Snapshot(() =>
            {
                if (version != Version() || context != PlanContext) throw new MutationRejectedException("PLAN_STALE", 6);
                var results = items.Select((item, index) =>
                {
                    bool ok = item is not null && (r.Command == "download delete" ? !item.IsActive : item.Status == DownloadStatus.Queued);
                    if (ok) { if (r.Command == "download delete") queue.Remove(item!); else queue.Move(item!, move == "up" ? -1 : 1); }
                    return new { id = ids[index], ok };
                }).ToArray();
                return results.All(i => i.ok) ? Envelope.Success(new { results }) : Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Library_Unavailable) with { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Library_Unavailable, Details: Protocol.Node(new { results })) };
            }, commit));
        }, token), ct);
    }
    private Task<JsonArray> DownloadSnapshotAsync(CancellationToken ct) => Snapshot(() => Protocol.Node(queue.Items.Select(i => new
    {
        id = i.Id, appid = i.AppId, title = LogRedactor.Redact(i.Title), state = JsonNamingPolicy.SnakeCaseLower.ConvertName(i.Status.ToString()),
        bytes = i.BytesRead, total_bytes = i.TotalBytes, rate = i.BytesPerSecond, eta_seconds = i.Eta?.TotalSeconds,
        enqueued_at = i.EnqueuedAt.ToUniversalTime(), available_actions = AvailableActions.For("download", c => c.Verb == "update" && i.Status != DownloadStatus.Queued ? "NOT_QUEUED" : c.Verb == "delete" && i.IsActive ? "JOB_ACTIVE" : null),
    }).ToArray())!.AsArray(), ct);
}
