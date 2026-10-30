using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;

public sealed partial class AgentCommandService
{
    [AgentResource("source")]
    private async Task<Envelope> SourceAsync(AgentRequest r, CancellationToken ct)
    {
        try
        {
            if (r.Flags.ContainsKey("legacy-index"))
            {
                if (r.Flags.ContainsKey("appid")) return AddFailure("INVALID_USAGE", 2);
                return List(r, Protocol.Node(LegacySourceCatalogue.Read().Select((s, i) => new { name = s.Name, index = i, available_actions = AvailableActions.For("source") }).ToArray())!.AsArray());
            }
            long id = AddAppId(r.Flags.GetValueOrDefault("appid"));
            var result = await Acquisition.Sources.CheckAsync(id, ct: ct);
            return List(r, Protocol.Node(result.Sources.Select(s => new { name = s.Name, display_name = s.DisplayName, status = s.Status,
                needs_key = s.NeedsKey, usage = s.Stats, available_actions = AvailableActions.ForReasons("game", c => c.Verb == "add" ? s.DisabledReasons.Concat(steam.IsValid ? [] : new[] { "STEAM_PATH_INVALID" }).ToArray() : [], r.Flags.ContainsKey("read-only")) }).ToArray())!.AsArray());
        }
        catch (MutationRejectedException ex) { return AddFailure(ex.Code, ex.ExitCode); }
    }
}
