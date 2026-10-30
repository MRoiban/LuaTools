using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("ping"), AgentResource("application"), AgentResource("status"), AgentResource("whoami")]
    private async Task<Envelope> SystemAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "status") return await StatusAsync(ct);
        if (r.Command == "whoami") return Whoami();
        if (r.Command == "application exit")
            return await _mutations.ExecuteAsync(r, async token =>
            {
                if (await Snapshot(() => queue.ActiveCount, token) > 0 || Cabinet.CabinetService.HasActiveHelpers)
                    throw new MutationRejectedException("JOBS_ACTIVE", 6);
                return new(new(Guid.NewGuid().ToString("N"), InstanceId + SteamIdentity, "idle", [], [Resources.Strings.Agent_ExitImpact], true),
                    _ => Task.FromResult(Envelope.Success(new { state = "exiting" })));
            }, ct);
        return Ping();
    }
    public Envelope Ping() => Envelope.Success(new { protocol = Protocol.Version, instance_id = InstanceId, ready = true, steam_identity = SteamIdentity, minimized = Program.StartMinimized, elevated = LocalDiscovery.IsElevated() });
    private bool? SelectedSteamRunning()
    {
        if (!steam.IsValid) return null;
        try { return steam.IsSelectedSteamRunning(); }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
    public async Task<Envelope> StatusAsync(CancellationToken ct)
    {
        var counts = await Snapshot(() => new { active = queue.ActiveCount, total = queue.Items.Count, failed = queue.History.Count(i => i.Failed) }, ct);
        return Envelope.Success(new
        {
            protocol = Protocol.Version, instance_id = InstanceId, version = UpdateService.CurrentVersion.ToString(),
            steam = new { identity = SteamIdentity, valid = steam.IsValid, running = SelectedSteamRunning() }, queue = counts,
            helpers = Cabinet.CabinetService.HasActiveHelpers ? 1 : 0,
            capabilities = Catalogue.Commands.Select(c => new { command = c.Key, enabled = c.Status == "enabled" }), health = "ready", available_actions = Actions("application"),
        });
    }
    public Envelope Whoami() => Envelope.Success(new
    {
        state = auth.IsSignedIn ? "authenticated" : "guest", display_name = auth.DisplayName is null ? null : LogRedactor.Redact(auth.DisplayName),
        provider = auth.IsSignedIn ? "discord" : null, expiry_state = "unknown",
        owner = new { account_match = true, elevation_match = true, session_match = true, elevated = LocalDiscovery.IsElevated() }, available_actions = Actions("account"),
    });
}
