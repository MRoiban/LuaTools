using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("cloudredirect")]
    private async Task<Envelope> CloudredirectAsync(AgentRequest r, CancellationToken ct)
    {
        if (modes is null) return Envelope.Fail("NOT_IMPLEMENTED", 5, Resources.Strings.Agent_NotImplemented);
        if (r.Command == "cloudredirect get")
        {
            var status = await modes.ReadCloudAsync(r.Flags.ContainsKey("refresh"), ct);
            // Status returns only readiness/host, never Cabinet credentials.
            object cabinetState;
            try { var s = new CloudRedirectCabinetConfig().Status(); cabinetState = new { configured = s.Configured, server_host = s.Server is { } server ? new Uri(server).Host : null }; }
            catch { cabinetState = new { configured = false, error = "CONFIG_ERROR" }; }
            return await Snapshot(() => Envelope.Success(new { status, cabinet = cabinetState, cabinet_connected = cabinet?.Status.Cloud.Connected == true, connected_server_host = cabinet?.Status.Cloud.ServerHost, unlocked = modes.CloudUnlocked, version = SystemVersion("cloudredirect"), available_actions = AvailableActions.ForReasons("cloudredirect", c => SystemReasons("cloudredirect", c), r.Flags.ContainsKey("read-only")) }), ct);
        }
        return await MutateAsync(r, token => Snapshot(() =>
        {
            ValidateScalarInput(r, "enabled", "refresh"); var data = BulkSelection.Data(r, "enabled", "refresh");
            bool? enabled = r.Flags.GetValueOrDefault("enabled") is { } value ? bool.Parse(value) : data["enabled"]?.GetValue<bool>();
            bool refresh = r.Flags.ContainsKey("refresh") || data["refresh"]?.GetValue<bool>() == true;
            bool manage = r.Command == "cloudredirect manage";
            if (manage && data.Count != 0) throw new MutationRejectedException("INVALID_USAGE", 2);
            if (!manage && (enabled is null && !refresh || enabled is not null && refresh)) throw new MutationRejectedException("INVALID_USAGE", 2);
            void Gate()
            {
                if (!modes.CloudUnlocked) throw new MutationRejectedException("MODE_REQUIRED", 6);
                if (!steam.IsValid) throw new MutationRejectedException("STEAM_NOT_FOUND", 6);
                if (enabled != false) RequireGate("CloudRedirect");
                if (manage && !unlocker.IsCloudRedirectAddonEnabled) throw new MutationRejectedException("ADDON_DISABLED", 6);
            }
            Gate(); string version = SystemVersion("cloudredirect");
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), PlanContext, version, [SteamIdentity],
                [Resources.Strings.Agent_System_CloudImpact], true), commit => Snapshot(() =>
                {
                    RequireSystemContext(r, version, "cloudredirect"); Gate();
                    return RegisterSystemJob("cloudredirect.change", async lifetime =>
                    {
                        RequireSystemContext(r, version, "cloudredirect"); Gate();
                        if (manage) return SystemResult(await modes.ManageCloudAsync(ct: lifetime));
                        return SystemResult((await modes.UpdateCloudAsync(enabled, refresh, ct: lifetime, revalidate: () => { RequireSystemContext(r, version, "cloudredirect"); Gate(); })).Success);
                    });
                }, commit));
        }, token), ct);
    }
}
