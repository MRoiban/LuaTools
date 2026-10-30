using System.Text.Json;
using LuaTools.Agent;
using LuaToolsGui.Services.AppInfo;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private bool LaunchRunning()
    {
        try { return launch!.IsSteamRunning; }
        catch (InvalidOperationException) { throw new MutationRejectedException("STEAM_IDENTITY_UNPROVEN", 5); }
        catch (System.ComponentModel.Win32Exception) { throw new MutationRejectedException("STEAM_IDENTITY_UNPROVEN", 5); }
    }
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
        var installation = Admitted;
        using var pinned = installation.Pin();
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
            if (r.Command is "launch-option create" or "launch-option update" or "launch-option delete") return await EditLaunch(r, ct);
            return await MutateAsync(r, async token =>
            {
                int[] ids = r.Command == "launch-option revert" ? [(int)DepotAppId(r)]
                    : await Task.Run(() =>
                    {
                        var drifted = launch.FindDrifted().ToArray();
                        if (!r.Flags.ContainsKey("ids") && !r.Flags.ContainsKey("where")) return drifted;
                        string[] selected;
                        try { selected = BulkSelection.Select(r, Protocol.Node(drifted.Select(id => new { appid = id, drifted = true }))!.AsArray(), "appid", ["appid", "drifted"]); }
                        catch (ArgumentException) { throw new MutationRejectedException("INVALID_SELECTION", 2); }
                        if (selected.Any(id => !int.TryParse(id, out int value) || !drifted.Contains(value))) throw new MutationRejectedException("INVALID_SELECTION", 2);
                        return selected.Select(int.Parse).ToArray();
                    }, token);
                return await Snapshot(() => PrepareLaunch(r, ids), token);
            }, ct);
        }
        catch (MutationRejectedException ex) { return Envelope.Fail(ex.Code, ex.ExitCode, Resources.Strings.Agent_Depot_Unavailable); }
    }
    private PreparedMutation PrepareLaunch(AgentRequest r, int[] ids)
    {
        var installation = Admitted;
        RequireAdmission(installation, r);
        try { BulkSelection.Data(r); } catch (JsonException) { throw new MutationRejectedException("INVALID_DATA", 2); }
        if (!launch!.IsAvailable) throw new MutationRejectedException("APPINFO_UNAVAILABLE", 5);
        if (ids.Any(id => launch.Store.Get(id) is null)) throw new MutationRejectedException("NOT_FOUND", 3);
        bool running = LaunchRunning();
        string version = LaunchVersion(), context = PlanContext;
        var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, ids.Select(id => id.ToString()).ToArray(),
            [Resources.Strings.Agent_Depot_LaunchImpact], true, running);
        return new(plan, token => Snapshot(() =>
        {
            if (PlanContext != context || LaunchVersion() != version) throw new MutationRejectedException("PLAN_STALE", 6);
            // Register the expensive cache rewrite with owner lifetime; never run it on the dispatcher.
            string job = Jobs.Register(r.Command, async lifetime => await Task.Run(() =>
            {
                if (PlanContext != context || LaunchVersion() != version) throw new MutationRejectedException("PLAN_STALE", 6);
                RequireAdmission(installation, r);
                using var selected = installation.Pin();
                bool nowRunning = LaunchRunning();
                if (nowRunning && (!r.Flags.ContainsKey("restart-steam") || !r.Flags.ContainsKey("yes"))) throw new MutationRejectedException("STEAM_RUNNING", 6);
                var coordinator = new LaunchEditCoordinator(launch);
                var result = r.Command == "launch-option revert" ? coordinator.Restore(ids[0]) : coordinator.Reapply(ids);
                return result.Ok ? Envelope.Success(new { results = ids.Select(id => new { id, ok = true }) })
                    : Envelope.Fail("APPLY_FAILED", 1, Resources.Strings.Agent_Depot_Unavailable);
            }, lifetime), _hostLifetime);
            return Envelope.Success(new { job_id = job, state = "accepted" });
        }, token));
    }
    private Task<Envelope> EditLaunch(AgentRequest r, CancellationToken ct) => MutateAsync(r, async token =>
    {
        var installation = Admitted;
        RequireAdmission(installation, r);
        using var pinned = installation.Pin();
        string context = PlanContext, version = LaunchVersion();
        ValidateScalarInput(r); // complex entry data travels only via the bounded file transport
        int id = (int)DepotAppId(r);
        var state = await Task.Run(() => launch!.Inspect(id), token) ?? throw new MutationRejectedException("NOT_FOUND", 3);
        RequireAdmission(installation, r);
        if (LaunchVersion() != version) throw new MutationRejectedException("PLAN_STALE", 6);
        var desired = (launch!.Store.Get(id)?.Desired ?? state.Current).Select(o => o.Clone()).ToList();
        int? at = r.Flags.GetValueOrDefault("entry") is { } entry && int.TryParse(entry, out int index) ? index : null;
        if (r.Flags.ContainsKey("entry") && (at is null || at < 0 || at >= desired.Count)) throw new MutationRejectedException("INVALID_ENTRY", 2);
        if (r.Command == "launch-option delete")
        {
            if (at is null) throw new MutationRejectedException("INVALID_ENTRY", 2);
            desired.RemoveAt(at.Value);
        }
        else
        {
            ValidateAddInputs(r);
            if (r.Files!.Length != 1 || r.Files[0].Length > 256 * 1024) throw new MutationRejectedException("INPUT_LIMIT", 2);
            using var json = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(r.InputPaths[0], token));
            var root = json.RootElement;
            if (r.Command == "launch-option create")
            {
                if (at is not null) throw new MutationRejectedException("INVALID_ENTRY", 2);
                desired.Add(ParseLaunchEntry(root, new LaunchOption { OsList = "windows" }));
            }
            else if (at is { } n) desired[n] = ParseLaunchEntry(root, desired[n]);
            else
            {
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 256) throw new MutationRejectedException("INVALID_DATA", 2);
                var previous = desired; desired = [];
                foreach (var row in root.EnumerateArray())
                {
                    // source_entry preserves unmodelled Steam fields across reordering.
                    var source = row.TryGetProperty("source_entry", out var sourceIndex) ? previous.ElementAtOrDefault(sourceIndex.GetInt32()) ?? throw new MutationRejectedException("INVALID_ENTRY", 2) : new LaunchOption { OsList = "windows" };
                    desired.Add(ParseLaunchEntry(row, source, allowSource: true));
                }
            }
        }
        if (desired.Any(o => string.IsNullOrWhiteSpace(o.Executable))) throw new MutationRejectedException("EXECUTABLE_REQUIRED", 2);
        bool apply = r.Flags.GetValueOrDefault("apply") == "true";
        bool running = apply && LaunchRunning();
        var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, [id + ":staged-launch-list"],
            [apply ? Resources.Strings.Agent_Depot_LaunchImpact : Resources.Strings.Agent_Finish_LaunchImpact], apply, running);
        return new PreparedMutation(plan, commit => Snapshot(() =>
        {
            if (PlanContext != context || LaunchVersion() != version) throw new MutationRejectedException("PLAN_STALE", 6);
            string job = Jobs.Register(r.Command, async lifetime => await Task.Run(() =>
            {
                if (PlanContext != context || LaunchVersion() != version) throw new MutationRejectedException("PLAN_STALE", 6);
                if (apply && LaunchRunning() && (!r.Flags.ContainsKey("restart-steam") || !r.Flags.ContainsKey("yes"))) throw new MutationRejectedException("STEAM_RUNNING", 6);
                RequireAdmission(installation, r);
                using var selected = installation.Pin();
                var coordinator = new LaunchEditCoordinator(launch!);
                coordinator.Stage(id, desired);
                if (apply && !coordinator.Reapply([id]).Ok) return Envelope.Fail("APPLY_FAILED", 1, Resources.Strings.Agent_Depot_Unavailable);
                return Envelope.Success(new { appid = id, staged = true, applied = apply, version = LaunchVersion() });
            }, lifetime), _hostLifetime);
            return Envelope.Success(new { id = job, job_id = job, state = "accepted" });
        }, commit));
    }, ct);
    private static LaunchOption ParseLaunchEntry(JsonElement row, LaunchOption source, bool allowSource = false)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new MutationRejectedException("INVALID_DATA", 2);
        var option = source.Clone();
        foreach (var field in row.EnumerateObject())
        {
            if (field.Name == "source_entry" && allowSource) { _ = field.Value.GetInt32(); continue; }
            if (field.Value.ValueKind != JsonValueKind.String) throw new MutationRejectedException("INVALID_DATA", 2);
            string value = field.Value.GetString()!;
            if (value.Length > 8192 || value.Contains('\0')) throw new MutationRejectedException("INVALID_DATA", 2);
            switch (field.Name)
            {
                case "executable": option.Executable = value; break;
                case "arguments": option.Arguments = value; break;
                case "working_dir": option.WorkingDir = value; break;
                case "description": option.Description = value; break;
                case "os_list": option.OsList = value; break;
                case "type": option.Type = value; break;
                case "beta_key": option.BetaKey = value; break;
                case "owns_dlc": option.OwnsDlc = value; break;
                default: throw new MutationRejectedException("INVALID_DATA", 2);
            }
        }
        return LaunchEditCoordinator.Normalize(option);
    }
}
