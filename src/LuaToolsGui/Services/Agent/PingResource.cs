using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("ping")]
    private Task<Envelope> PingRequestAsync(AgentRequest r, CancellationToken ct) => Task.FromResult(Ping());
    public Envelope Ping() => Envelope.Success(new { protocol = Protocol.Version, instance_id = InstanceId, ready = true, steam_identity = SteamIdentity, minimized = Program.StartMinimized, elevated = LocalDiscovery.IsElevated() });
}
