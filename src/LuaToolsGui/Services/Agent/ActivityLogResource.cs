using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("activity-log")]
    private async Task<Envelope> ActivityAsync(AgentRequest r, CancellationToken ct) => List(r, await Task.Run(() => AppEventLog.Shared.ReadAgentEvents(r.Flags), ct));
}
