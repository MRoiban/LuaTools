using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("whoami")]
    private Task<Envelope> WhoamiRequestAsync(AgentRequest r, CancellationToken ct) => Task.FromResult(Whoami());
    public Envelope Whoami() => Envelope.Success(new
    {
        state = auth.IsSignedIn ? "authenticated" : "guest", display_name = auth.DisplayName is null ? null : LogRedactor.Redact(auth.DisplayName),
        provider = auth.IsSignedIn ? "discord" : null, expiry_state = "unknown",
        owner = new { account_match = true, elevation_match = true, session_match = true, elevated = LocalDiscovery.IsElevated() }, available_actions = Actions("account"),
    });}
