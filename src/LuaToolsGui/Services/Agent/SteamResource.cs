using System.IO;
using System.Text.Json;
using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("steam")]
    private async Task<Envelope> SteamAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "steam get") return await Snapshot(() => Envelope.Success(new
        {
            path = steam.EffectivePath, overridden = steam.IsOverridden, valid = steam.IsValid,
            running = SelectedSteamRunning(), steam_identity = SteamIdentity, version = SystemVersion("steam"),
            available_actions = AvailableActions.ForReasons("steam", c => SystemReasons("steam", c), r.Flags.ContainsKey("read-only")),
        }), ct);
        return await _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            var data = BulkSelection.Data(r, "path", "auto"); ValidateScalarInput(r, "path", "auto");
            string? path = r.Flags.GetValueOrDefault("path") ?? data["path"]?.GetValue<string>();
            bool auto = r.Flags.ContainsKey("auto") || data["auto"]?.GetValue<bool>() == true;
            bool restart = r.Command == "steam restart";
            if (restart && data.Count != 0) throw new MutationRejectedException("INVALID_USAGE", 2);
            if (!restart && (auto == (path is not null) || path is not null && !Path.IsPathFullyQualified(path))) throw new MutationRejectedException("INVALID_USAGE", 2);
            if (restart) RequireSteamState();
            string version = SystemVersion("steam");
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), PlanContext, version, [SteamIdentity],
                [restart ? Resources.Strings.Agent_System_SteamRestart : Resources.Strings.Agent_System_SteamSelection], true),
                commit => Snapshot(() =>
                {
                    RequireSystemContext(r, version, "steam");
                    if (restart) return RegisterSystemJob("steam.restart", _ =>
                    {
                        RequireSystemContext(r, version, "steam");
                        return Task.FromResult(SystemResult((processes ?? new(steam)).Restart()));
                    });
                    settings.SteamPathOverride = auto ? null : path;
                    return Envelope.Success(new { path = steam.EffectivePath, steam_identity = SteamIdentity, version = SystemVersion("steam") });
                }, commit));
        }, token), ct);
    }
}
