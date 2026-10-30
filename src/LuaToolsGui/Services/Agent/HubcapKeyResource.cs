using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private Models.HubcapStats? _agentHubcapStats;
    [AgentResource("hubcap-key")]
    private async Task<Envelope> HubcapKeyAsync(AgentRequest r, CancellationToken ct)
    {
        if (hubcap is null) return Envelope.Fail("SERVICE_UNAVAILABLE", 5, Resources.Strings.Agent_Account_Unavailable);
        if (r.Command == "hubcap-key get")
        {
            string? key = await Snapshot(() => settings.HubcapApiKey, ct);
            if (r.Flags.ContainsKey("refresh") && key is not null) _agentHubcapStats = await hubcap.GetStatsAsync(key, ct);
            return Envelope.Success(new { configured = key is not null, stats = _agentHubcapStats, available_actions = AvailableActions.For("hubcap-key") });
        }
        return await _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            bool delete = r.Command == "hubcap-key delete";
            if (!delete && !HubcapService.IsValidKeyFormat(r.Secret?.Trim())) throw new MutationRejectedException("INVALID_SECRET", 2);
            string context = PlanContext;
            // Version is a presence marker, never a digest of saved credentials.
            string version = settings.HubcapApiKey is null ? "unconfigured" : "configured";
            if (!delete && version == "configured") throw new MutationRejectedException("CLEAR_KEY_FIRST", 6);
            var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, ["hubcap-key"], [delete ? "Forget saved Hubcap key." : "Validate and save Hubcap key in owner-protected storage."], !delete);
            return new PreparedMutation(plan, commit => Snapshot(() =>
            {
                if (context != PlanContext || version != (settings.HubcapApiKey is null ? "unconfigured" : "configured")) throw new MutationRejectedException("PLAN_STALE", 6);
                if (delete) { settings.HubcapApiKey = null; _agentHubcapStats = null; return Envelope.Success(new { configured = false }); }
                string id = Jobs.Register("hubcap-key update", async lifetime =>
                {
                    var stats = await AccountCabinetOperations.ValidateHubcapAsync(hubcap, r.Secret!, lifetime);
                    if (stats is null) return Envelope.Fail("VALIDATION_FAILED", 1, Resources.Strings.Agent_Account_Unavailable);
                    return await Snapshot(() =>
                    {
                        if (settings.HubcapApiKey is not null) return Envelope.Fail("VERSION_CONFLICT", 6, Resources.Strings.Agent_Account_Unavailable);
                        settings.HubcapApiKey = r.Secret!.Trim(); _agentHubcapStats = stats;
                        return Envelope.Success(new { configured = true });
                    }, lifetime);
                }, _hostLifetime);
                return Envelope.Success(new { job_id = id });
            }, commit));
        }, token), ct);
    }
}
