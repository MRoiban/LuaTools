using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("status")]
    private Task<Envelope> StatusRequestAsync(AgentRequest r, CancellationToken ct) => StatusAsync(ct);
    public async Task<Envelope> StatusAsync(CancellationToken ct)
    {
        var counts = await Snapshot(() => new { active = queue.ActiveCount, operations = Jobs.ActiveOperations, total = queue.Items.Count, failed = queue.History.Count(i => i.Failed) }, ct);
        return Envelope.Success(new
        {
            protocol = Protocol.Version, instance_id = InstanceId, version = UpdateService.CurrentVersion.ToString(),
            steam = new { identity = SteamIdentity, valid = steam.IsValid, running = SelectedSteamRunning() }, queue = counts,
            helpers = Cabinet.CabinetService.HasActiveHelpers ? 1 : 0,
            capabilities = Catalogue.Commands.Select(c => new { command = c.Key, enabled = c.Status == "enabled" }), health = "ready", available_actions = Actions("application"),
        });
    }
}
