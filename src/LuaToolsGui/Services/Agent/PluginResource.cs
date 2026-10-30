using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("plugin")]
    private async Task<Envelope> PluginAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "plugin get")
        {
            var status = await (plugins ?? PluginCoordinator.For(plugin)).ReadAsync(r.Flags.ContainsKey("refresh"), ct);
            return await Snapshot(() => Envelope.Success(new { status, version = SystemVersion("plugin"), available_actions = AvailableActions.ForReasons("plugin", c => SystemReasons("plugin", c), r.Flags.ContainsKey("read-only")) }), ct);
        }
        return await MutateAsync(r, token => Snapshot(() =>
        {
            ValidateScalarInput(r); bool install = r.Command == "plugin update";
            if (install && !r.Flags.ContainsKey("install")) throw new MutationRejectedException("INVALID_USAGE", 2);
            if (install) RequireGate("LuaToolsPlugin");
            bool running = RequireSteamState(); string version = SystemVersion("plugin");
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), PlanContext, version, [SteamIdentity],
                [Resources.Strings.Agent_System_PluginImpact], true, running), commit => Snapshot(() =>
                {
                    RequireSystemContext(r, version, "plugin"); if (install) RequireGate("LuaToolsPlugin"); RequireProcessConsent(r);
                    return RegisterSystemJob("plugin.change", async lifetime =>
                    {
                        RequireSystemContext(r, version, "plugin"); if (install) RequireGate("LuaToolsPlugin"); RequireProcessConsent(r);
                        var result = await (plugins ?? PluginCoordinator.For(plugin)).ChangeAsync(install, ct: lifetime, revalidate: () => { RequireSystemContext(r, version, "plugin"); if (install) RequireGate("LuaToolsPlugin"); RequireProcessConsent(r); });
                        return SystemResult(result.ok);
                    });
                }, commit));
        }, token), ct);
    }
}
