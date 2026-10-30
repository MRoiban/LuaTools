using System.Text.Json;
using LuaTools.Agent;
using LuaToolsGui.Services.AppInfo;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private string LaunchVersion() => MutationCoordinator.Hash(JsonSerializer.Serialize(launch!.Store.All, Protocol.Json) + (launch.AppInfoPath is { } path && System.IO.File.Exists(path) ? new System.IO.FileInfo(path).LastWriteTimeUtc.Ticks + ":" + new System.IO.FileInfo(path).Length : "missing"));
    private object LaunchRow(int id)
    {
        var state = launch!.Inspect(id);
        return new { appid = id, current = state?.Current, desired = launch.Store.Get(id)?.Desired,
            is_modded = state?.IsModded ?? false, version = LaunchVersion(),
            available_actions = AvailableActions.For("launch-option", c => !c.ReadOnly && !launch.IsAvailable ? "APPINFO_UNAVAILABLE" : null) };
    }
    [AgentResource("launch-option")]
    private async Task<Envelope> LaunchOptionAsync(AgentRequest r, CancellationToken ct)
    {
        if (launch is null) return Envelope.Fail("SERVICE_UNAVAILABLE", 7, Resources.Strings.Agent_Depot_Unavailable);
        try
        {
            if (r.Command == "launch-option get")
            {
                int id = (int)DepotAppId(r);
                return await Task.Run(() => launch.Inspect(id) is null ? Envelope.Fail("NOT_FOUND", 3, Resources.Strings.Agent_Depot_Unavailable) : Envelope.Success(LaunchRow(id)), ct);
            }
            if (r.Command == "launch-option list")
                return await Task.Run(() =>
                {
                    var ids = r.Flags.ContainsKey("drifted") ? launch.FindDrifted() : launch.Store.All.Select(m => m.AppId).ToList();
                    return List(r, Protocol.Node(ids.Select(LaunchRow))!.AsArray());
                }, ct);
            return await _mutations.ExecuteAsync(r, token => Snapshot(() => PrepareLaunch(r), token), ct);
        }
        catch (MutationRejectedException ex) { return Envelope.Fail(ex.Code, ex.ExitCode, Resources.Strings.Agent_Depot_Unavailable); }
    }
    private PreparedMutation PrepareLaunch(AgentRequest r)
    {
        try { BulkSelection.Data(r); } catch (JsonException) { throw new MutationRejectedException("INVALID_DATA", 2); }
        if (!launch!.IsAvailable) throw new MutationRejectedException("APPINFO_UNAVAILABLE", 5);
        int[] ids = r.Command == "launch-option revert" ? [(int)DepotAppId(r)] : launch.FindDrifted().ToArray();
        if (ids.Any(id => launch.Store.Get(id) is null)) throw new MutationRejectedException("NOT_FOUND", 3);
        bool running = SelectedSteamRunning() ?? throw new MutationRejectedException("STEAM_IDENTITY_UNPROVEN", 5);
        string version = LaunchVersion(), context = PlanContext;
        var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, ids.Select(id => id.ToString()).ToArray(),
            [Resources.Strings.Agent_Depot_LaunchImpact], true, running);
        return new(plan, token => Snapshot(() =>
        {
            if (PlanContext != context || LaunchVersion() != version) throw new MutationRejectedException("PLAN_STALE", 6);
            // Register the expensive cache rewrite with owner lifetime; never run it on the dispatcher.
            string job = Jobs.Register(r.Command, async lifetime => await Task.Run(() =>
            {
                if (LaunchVersion() != version) throw new MutationRejectedException("PLAN_STALE", 6);
                bool nowRunning = SelectedSteamRunning() ?? throw new MutationRejectedException("STEAM_IDENTITY_UNPROVEN", 5);
                if (nowRunning && (!r.Flags.ContainsKey("restart-steam") || !r.Flags.ContainsKey("yes"))) throw new MutationRejectedException("STEAM_RUNNING", 6);
                var coordinator = new LaunchEditCoordinator(launch);
                var result = r.Command == "launch-option revert" ? coordinator.Restore(ids[0]) : coordinator.Reapply(ids);
                return result.Ok ? Envelope.Success(new { results = ids.Select(id => new { id, ok = true }) })
                    : Envelope.Fail("APPLY_FAILED", 1, Resources.Strings.Agent_Depot_Unavailable);
            }, lifetime), _hostLifetime);
            return Envelope.Success(new { job_id = job, state = "accepted" });
        }, token));
    }
}
