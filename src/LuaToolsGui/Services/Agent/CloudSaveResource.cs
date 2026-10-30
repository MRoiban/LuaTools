using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("cloud-save")]
    private Task<Envelope> CloudSaveAsync(AgentRequest r, CancellationToken ct) => _mutations.ExecuteAsync(r, token => Snapshot(() =>
    {
        ValidateScalarInput(r, "enabled");
        var data = BulkSelection.Data(r, "enabled");
        bool enabled = r.Flags.GetValueOrDefault("enabled") is { } value ? bool.Parse(value) : data["enabled"]?.GetValue<bool>() ?? throw new MutationRejectedException("INVALID_USAGE", 2);
        if (!enabled) throw new MutationRejectedException("UNSUPPORTED", 5);
        void Gate()
        {
            if (modes is null || !modes.CloudUnlocked) throw new MutationRejectedException("MODE_REQUIRED", 6);
            if (CabinetGate() is { } reason) throw new MutationRejectedException(reason, 6);
            if (cabinet?.Status.Cloud.Connected != true) throw new MutationRejectedException("NOT_CONNECTED", 6);
            if (!steam.IsValid) throw new MutationRejectedException("STEAM_NOT_FOUND", 6);
        }
        Gate(); string context = PlanContext, version = SystemVersion("cloudredirect") + CabinetVersion;
        return new PreparedMutation(new(Guid.NewGuid().ToString("N"), context, version, [SteamIdentity + ":cloud-save"],
            [Resources.Strings.Agent_Finish_CloudImpact], true), commit => Snapshot(() =>
        {
            Gate();
            if (context != PlanContext || version != SystemVersion("cloudredirect") + CabinetVersion) throw new MutationRejectedException("PLAN_STALE", 6);
            if (!settings.CloudRedirectEnabled) ExecutableConsentCoordinator.Set(settings, "CloudRedirectEnabled", true, "agent", r.Flags.GetValueOrDefault("actor-note"), toast);
            string admitted = PlanContext, cabinetVersion = CabinetVersion;
            return RegisterSystemJob("cloud-save.enable", async lifetime =>
            {
                void Revalidate() { Gate(); if (PlanContext != admitted || CabinetVersion != cabinetVersion) throw new MutationRejectedException("PLAN_STALE", 6); RequireGate("CloudRedirect"); }
                Revalidate();
                var result = await modes!.EnrollCloudSavesAsync(lifetime, Revalidate);
                return result.Success ? Envelope.Success(new { enabled = true, restart_steam_required = true }) : SystemResult(false);
            });
        }, commit));
    }, token), ct);
}
