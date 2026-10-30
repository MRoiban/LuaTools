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
                    _ => Task.FromResult(Envelope.Success(new { state = "exiting" })));
            }, ct);
        return Ping();
    }
}
