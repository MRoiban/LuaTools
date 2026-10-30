using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("application")]
    private async Task<Envelope> SystemAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "application exit")
            return await _mutations.ExecuteAsync(r, async token =>
            {
                if (await Snapshot(() => queue.ActiveCount + Jobs.ActiveOperations, token) > 0 || Cabinet.CabinetService.HasActiveHelpers)
                    throw new MutationRejectedException("JOBS_ACTIVE", 6);
                return new(new(Guid.NewGuid().ToString("N"), PlanContext, "idle", [], [Resources.Strings.Agent_ExitImpact], true),
                    commitToken => Snapshot(() =>
                    {
                        if (queue.ActiveCount + Jobs.ActiveOperations > 0 || Cabinet.CabinetService.HasActiveHelpers) throw new MutationRejectedException("JOBS_ACTIVE", 6);
                        _exiting = true; _shutdownReplies.TryAdd(r.RequestId, 0);
                        return Envelope.Success(new { state = "exiting", instance_id = InstanceId, request_id = r.RequestId });
                    }, commitToken));
            }, ct);
        return await _mutations.ExecuteAsync(r, token => Snapshot(() => new PreparedMutation(
            new(Guid.NewGuid().ToString("N"), PlanContext, InstanceId, [InstanceId], [], false),
            _ => Task.FromResult(Ping())), token), ct);
    }
}
