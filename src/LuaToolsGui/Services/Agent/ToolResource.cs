using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("tool")]
    private Task<Envelope> ToolAsync(AgentRequest r, CancellationToken ct) => _mutations.ExecuteAsync(r, token => Snapshot(() =>
    {
        ValidateScalarInput(r);
        if (r.Arguments is not ["steamautocrack"]) throw new MutationRejectedException("INVALID_USAGE", 2);
        RequireGate("SteamAutoCrack");
        if (tool is null || factory is null) throw new MutationRejectedException("NOT_IMPLEMENTED", 5);
        string version = SystemVersion("tool");
        return new PreparedMutation(new(Guid.NewGuid().ToString("N"), PlanContext, version, ["steamautocrack", SteamIdentity],
            [Resources.Strings.Agent_System_ToolImpact], true), commit => Snapshot(() =>
            {
                RequireSystemContext(r, version, "tool"); RequireGate("SteamAutoCrack");
                return RegisterSystemJob("tool.launch", async lifetime =>
                {
                    RequireSystemContext(r, version, "tool"); RequireGate("SteamAutoCrack");
                    if (await tool.TryLaunchIfReadyAsync()) return Envelope.Success(new { state = "launched" });
                    return await Snapshot(() =>
                    {
                        RequireGate("SteamAutoCrack");
                        var item = queue.Enqueue(factory.CreateSteamAutoCrackJob());
                        return Envelope.Success(new { job_id = item.Id, state = "queued" });
                    }, lifetime);
                });
            }, commit));
    }, token), ct);
}
