using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Models;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("mode")]
    private async Task<Envelope> ModeAsync(AgentRequest r, CancellationToken ct)
    {
        if (modes is null) return Envelope.Fail("NOT_IMPLEMENTED", 5, Resources.Strings.Agent_NotImplemented);
        if (r.Command != "mode update")
        {
            var state = await modes.ReadAsync(r.Flags.ContainsKey("refresh"), ct: ct);
            var rows = await Snapshot(() => state.States.Select(s => new
            {
                id = s.Mode.ToString().ToLowerInvariant(), selected = s.IsActive, status = s.Status.ToString(), latest_version = s.LatestVersion,
                detected_mode = state.Detected?.ToString().ToLowerInvariant(), version = SystemVersion("mode"),
                available_actions = AvailableActions.ForReasons("mode", c => SystemReasons("mode", c).Concat(c.Verb == "update" && s.Mode != UnlockerMode.Custom && !settings.IsExecutableWorkflowEnabled(s.Mode == UnlockerMode.Bst ? "BetterSteamTools" : "OpenSteamTools") ? new[] { "WORKFLOW_DISABLED" } : []).ToArray(), r.Flags.ContainsKey("read-only")),
            }).ToArray(), ct);
            return r.Command == "mode list" ? List(r, Protocol.Node(rows)!.AsArray()) : Envelope.Success(new { selected_mode = unlocker.SelectedMode?.ToString().ToLowerInvariant(), detected_mode = state.Detected?.ToString().ToLowerInvariant(), modes = rows, version = SystemVersion("mode") });
        }
        return await _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            ValidateScalarInput(r, "mode"); var data = BulkSelection.Data(r, "mode");
            string? value = r.Arguments.FirstOrDefault() ?? data["mode"]?.GetValue<string>();
            if (!Enum.TryParse<UnlockerMode>(value, true, out var mode) || !Enum.IsDefined(mode)) throw new MutationRejectedException("INVALID_USAGE", 2);
            void Gate() { if (mode != UnlockerMode.Custom) RequireGate(mode == UnlockerMode.Bst ? "BetterSteamTools" : "OpenSteamTools"); }
            Gate(); bool running = RequireSteamState(); string version = SystemVersion("mode");
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), PlanContext, version, [mode.ToString(), SteamIdentity],
                [Resources.Strings.Agent_System_ModeImpact, Resources.Strings.Agent_System_ModeStartsSteam], true, running),
                commit => Snapshot(() =>
                {
                    RequireSystemContext(r, version, "mode"); Gate(); RequireProcessConsent(r);
                    return RegisterSystemJob("mode.update", async lifetime =>
                    {
                        RequireSystemContext(r, version, "mode"); Gate(); RequireProcessConsent(r);
                        var result = await modes.InstallAsync(mode, ct: lifetime, revalidate: () => { RequireSystemContext(r, version, "mode"); Gate(); RequireProcessConsent(r); });
                        return SystemResult(result.Result.Success && result.Started);
                    });
                }, commit));
        }, token), ct);
    }
}
