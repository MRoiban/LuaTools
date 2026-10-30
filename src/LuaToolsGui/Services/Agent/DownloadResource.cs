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
        if (r.Command is "download list" or "queue list") return List(r, await DownloadSnapshotAsync(ct));
        return await _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            if (r.Flags.ContainsKey("data")) throw new MutationRejectedException("INVALID_DATA", 2);
            string[] ids = LibrarySelect(r, Protocol.Node(queue.Items.Select(i => new { id = i.Id }))!.AsArray(), "id", ["id"]);
            var items = ids.Select(id => queue.Items.FirstOrDefault(i => i.Id == id)).ToArray();
            string Version() => MutationCoordinator.Hash(string.Join(':', queue.Items.Select(i => i.Id + AgentJobRegistry.Version(i))));
            string version = Version(), context = PlanContext;
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), context, version, ids, [r.Command], false), commit => Snapshot(() =>
            {
                if (version != Version() || context != PlanContext) throw new MutationRejectedException("PLAN_STALE", 6);
                var results = items.Select((item, index) =>
                {
                    bool ok = item is not null && (r.Command == "download delete" ? !item.IsActive : item.Status == DownloadStatus.Queued);
                    if (ok) { if (r.Command == "download delete") queue.Remove(item!); else queue.Move(item!, r.Flags.GetValueOrDefault("move") == "up" ? -1 : 1); }
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
        enqueued_at = i.EnqueuedAt.ToUniversalTime(), available_actions = Actions("download"),
    }).ToArray())!.AsArray(), ct);
}
