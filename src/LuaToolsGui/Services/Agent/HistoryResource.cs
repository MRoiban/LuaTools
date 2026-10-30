using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("history")]
    private Task<Envelope> HistoryAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "history list") return Snapshot(() => r.Flags.GetValueOrDefault("kind") == "pending-added"
            ? List(r, Protocol.Node((libraryCache?.GetLoadedAppIds() ?? []).Select(id => new { id, appid = id }))!.AsArray())
            : List(r, HistoryRows()), ct);
        return _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            if (r.Flags.ContainsKey("data")) throw new MutationRejectedException("INVALID_DATA", 2);
            bool pending = r.Arguments.FirstOrDefault() == "pending-added";
            string[] ids = pending ? ["pending-added"] : r.Arguments.FirstOrDefault() == "all" ? queue.History.Select(h => h.Id).ToArray()
                : LibrarySelect(r, HistoryRows(), "id", ["id", "appid", "state"]);
            string Version() => MutationCoordinator.Hash(pending ? string.Join(':', libraryCache?.GetLoadedAppIds() ?? []) : HistoryRows().ToJsonString());
            string version = Version(), context = PlanContext;
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), context, version, ids, [Resources.Strings.Agent_Library_HistoryImpact], true), commit => Snapshot(() =>
            {
                if (version != Version() || context != PlanContext) throw new MutationRejectedException("PLAN_STALE", 6);
                if (pending) { if (libraryCache is null) throw new MutationRejectedException("ACTION_UNAVAILABLE", 6); libraryCache.ClearLoadedAppIds(); }
                else
                {
                    var results = ids.Select(id =>
                    {
                        var entry = queue.History.FirstOrDefault(e => e.Id == id);
                        if (entry is not null) DownloadHistoryCoordinator.Remove(queue, entry);
                        return new { id, ok = entry is not null };
                    }).ToArray();
                    if (results.Any(i => !i.ok)) return Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Library_Unavailable) with { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Library_Unavailable, Details: Protocol.Node(new { results })) };
                }
                return Envelope.Success(new { deleted = ids });
            }, commit));
        }, token), ct);
    }
    private System.Text.Json.Nodes.JsonArray HistoryRows() => Protocol.Node(queue.History.Select(h => new { id = h.Id, appid = h.AppId, title = LogRedactor.Redact(h.Title), subtitle = LogRedactor.Redact(h.SubTitle), state = h.Status.ToString().ToLowerInvariant(), bytes = h.Record.Bytes, completed_at = DateTimeOffset.FromUnixTimeMilliseconds(h.Record.CompletedAtMs), available_actions = AvailableActions.For("history") }))!.AsArray();
}
