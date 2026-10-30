using LuaTools.Agent;
using LuaToolsGui.Models;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("link")]
    private Task<Envelope> LinkAsync(AgentRequest r, CancellationToken ct)
    {
        string? url = r.Flags.GetValueOrDefault("kind") switch
        {
            "website" => AppConfig.ApiBaseUrl,
            "hubcap" => AppConfig.HubcapBaseUrl,
            "source" => SourceMeta.Get(r.Flags.GetValueOrDefault("source") ?? "").DiscordUrl,
            "markdown" => r.Flags.GetValueOrDefault("url") is { } value && SafeMarkdown.IsSafeBrowserLink(value) ? value : null,
            "steamdb" => long.TryParse(r.Flags.GetValueOrDefault("appid"), out long id) && id > 0 && id < 2_000_000_000 ? $"https://steamdb.info/app/{id}/" : null,
            _ => null,
        };
        return Task.FromResult(url is null ? Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments) : Envelope.Success(new { url }));
    }
}
