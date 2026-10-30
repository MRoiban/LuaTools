using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("download"), AgentResource("queue")]
    private async Task<Envelope> DownloadAsync(AgentRequest r, CancellationToken ct) => List(r, await DownloadSnapshotAsync(ct));
    private Task<JsonArray> DownloadSnapshotAsync(CancellationToken ct) => Snapshot(() => Protocol.Node(queue.Items.Select(i => new
    {
        id = i.Id, appid = i.AppId, title = LogRedactor.Redact(i.Title), state = JsonNamingPolicy.SnakeCaseLower.ConvertName(i.Status.ToString()),
        bytes = i.BytesRead, total_bytes = i.TotalBytes, rate = i.BytesPerSecond, eta_seconds = i.Eta?.TotalSeconds,
        enqueued_at = i.EnqueuedAt.ToUniversalTime(), available_actions = Actions("download"),
    }).ToArray())!.AsArray(), ct);
}
