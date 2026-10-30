using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("application")]
    private Task<Envelope> SystemAsync(AgentRequest r, CancellationToken ct) => LifecycleAsync(r, ct);
    private async Task<Envelope> LifecycleAsync(AgentRequest r, CancellationToken ct)
    {
        // Waiting happens before the serialized admission stage. It holds no mutation lock or UI thread.
        if (r.Flags.ContainsKey("drain") && !r.Flags.ContainsKey("dry-run") && r.Flags.ContainsKey("yes"))
            while (await Snapshot(() => queue.ActiveCount + Jobs.ActiveOperations > 0 || Cabinet.CabinetService.HasActiveHelpers, ct))
                await Task.Delay(100, ct);
        return await _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            ValidateScalarInput(r);
            if (r.Command == "application start") return new PreparedMutation(
                new(Guid.NewGuid().ToString("N"), PlanContext, InstanceId, [InstanceId], [], false), _ => Task.FromResult(Ping()));
            void Idle()
            {
                if (!(r.Flags.ContainsKey("dry-run") && r.Flags.ContainsKey("drain")) && (queue.ActiveCount + Jobs.ActiveOperations > 0 || Cabinet.CabinetService.HasActiveHelpers)) throw new MutationRejectedException("JOBS_ACTIVE", 6);
                if (r.Command == "update apply" && !updates.HasStagedUpdate) throw new MutationRejectedException("NO_STAGED_UPDATE", 6);
            }
            Idle(); string version = SystemVersion("lifecycle");
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), PlanContext, version, [InstanceId, SteamIdentity],
                [r.Command == "application exit" ? Resources.Strings.Agent_ExitImpact : Resources.Strings.Agent_System_RestartImpact], true),
                commit => Snapshot(() =>
                {
                    RequireSystemContext(r, version, "lifecycle"); Idle();
                    _exiting = true;
                    _shutdownReplies.TryAdd(r.RequestId, () =>
                    {
                        if (r.Command == "application restart") ApplicationCoordinator.Restart();
                        else if (r.Command == "update apply")
                        {
                            try { (lifecycle ?? new(updates)).Apply(); }
                            catch { _exiting = false; }
                        }
                        else System.Windows.Application.Current?.Shutdown();
                    });
                    return Envelope.Success(new { state = r.Command == "application exit" ? "exiting" : "restarting", instance_id = InstanceId, request_id = r.RequestId });
                }, commit));
        }, token), ct);
    }
}
