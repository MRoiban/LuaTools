using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("update")]
    private async Task<Envelope> UpdateAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "update apply") return await LifecycleAsync(r, ct);
        if (r.Command == "update get") return await Snapshot(() => Envelope.Success(new
        {
            current_version = UpdateService.CurrentVersion.ToString(), staged_version = updates.StagedVersion?.ToString(), staged = updates.HasStagedUpdate,
            version = SystemVersion("update"), available_actions = AvailableActions.ForReasons("update", c => SystemReasons("update", c), r.Flags.ContainsKey("read-only")),
        }), ct);
        return await _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            ValidateScalarInput(r); string version = SystemVersion("update");
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), PlanContext, version, [InstanceId], [Resources.Strings.Agent_System_UpdateImpact], false),
                commit => Snapshot(() =>
                {
                    RequireSystemContext(r, version, "update");
                    return RegisterSystemJob("update.check", async _ =>
                    {
                        var result = await (lifecycle ?? new(updates)).CheckAsync();
                        return result == UpdateCheckResult.Unavailable ? SystemResult(false) : Envelope.Success(new { state = result.ToString().ToLowerInvariant(), staged_version = updates.StagedVersion?.ToString() });
                    });
                }, commit));
        }, token), ct);
    }
}
