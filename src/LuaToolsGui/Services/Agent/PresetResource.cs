using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private static long DepotAppId(AgentRequest r)
    {
        if (!long.TryParse(r.Flags.GetValueOrDefault("appid") ?? r.Arguments.FirstOrDefault(), out long id) || id <= 0 || id > int.MaxValue)
            throw new MutationRejectedException("INVALID_APPID", 2);
        return id;
    }
    private string VaultVersion(long id) => MutationCoordinator.Hash(JsonSerializer.Serialize(new { variants = vault!.GetVariants(id), live = vault.GetActiveHash(id), edit_base = vault.GetEditBase(id) }, Protocol.Json));
    private string? PresetActive(long id) => ViewModels.BuildsViewModel.ResolveActiveHash(vault!.GetActiveHash(id), vault.GetVariants(id), vault.GetEditBase(id));
    private object PresetRow(long id, LuaVariant v) => new
    {
        id = v.Hash, appid = id, name = v.DisplayLabel, kind = v.Kind, build_id = v.BuildId,
        captured_at = v.CapturedAt, depot_count = v.DepotCount, dlc_count = v.DlcCount,
        active = v.Hash == PresetActive(id),
        version = VaultVersion(id), available_actions = AvailableActions.For("preset", c =>
            c.Verb is "delete" or "apply" && v.Hash == PresetActive(id) ? "PRESET_ACTIVE" : null),
    };
    [AgentResource("preset")]
    private async Task<Envelope> PresetAsync(AgentRequest r, CancellationToken ct)
    {
        if (vault is null) return Envelope.Fail("SERVICE_UNAVAILABLE", 7, Resources.Strings.Agent_Depot_Unavailable);
        try
        {
            long id = DepotAppId(r);
            if (r.Command is "preset list" or "preset get")
                return await Task.Run(() =>
                {
                    var rows = vault.GetVariants(id);
                    if (r.Command == "preset list") return List(r, Protocol.Node(rows.Select(v => PresetRow(id, v)))!.AsArray());
                    var found = rows.FirstOrDefault(v => v.Hash == r.Arguments[0]);
                    return found is null ? Envelope.Fail("NOT_FOUND", 3, Resources.Strings.Agent_Depot_Unavailable) : Envelope.Success(PresetRow(id, found));
                }, ct);
            return await _mutations.ExecuteAsync(r, token => Snapshot(() => PreparePreset(r, id), token), ct);
        }
        catch (MutationRejectedException ex) { return Envelope.Fail(ex.Code, ex.ExitCode, Resources.Strings.Agent_Depot_Unavailable); }
    }
    private PreparedMutation PreparePreset(AgentRequest r, long id)
    {
        JsonObject data;
        try { data = BulkSelection.Data(r, "id", "name"); }
        catch (JsonException) { throw new MutationRejectedException("INVALID_DATA", 2); }
        if (r.Flags.ContainsKey("from-live") || r.Command == "preset create") throw new MutationRejectedException("REFACTOR_REQUIRED", 5);
        string? name = r.Flags.GetValueOrDefault("name") ?? data["name"]?.ToString();
        if (r.Command == "preset update" && name is null) throw new MutationRejectedException("INVALID_VALUE", 2);
        var rows = Protocol.Node(vault!.GetVariants(id).Select(v => PresetRow(id, v)))!.AsArray();
        string[] targets;
        try { targets = BulkSelection.Select(r, rows, "id", ["id", "kind", "active"], data["id"]?.ToString()); }
        catch (ArgumentException) { throw new MutationRejectedException("INVALID_SELECTION", 2); }
        string version = VaultVersion(id), context = PlanContext;
        bool destructive = r.Command != "preset update";
        var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, targets,
            [r.Command == "preset apply" ? Resources.Strings.Agent_Depot_ApplyImpact : r.Command == "preset delete" ? Resources.Strings.Agent_Depot_DeleteImpact : Resources.Strings.Agent_Depot_RenameImpact], destructive);
        return new(plan, token => Snapshot(() =>
        {
            if (PlanContext != context || VaultVersion(id) != version) throw new MutationRejectedException("PLAN_STALE", 6);
            var coordinator = new DepotPresetCoordinator(vault);
            var results = new List<object>(); bool failed = false;
            foreach (string hash in targets)
            {
                var variant = vault.GetVariants(id).FirstOrDefault(v => v.Hash == hash);
                string? error = variant is null ? "NOT_FOUND" : r.Command is "preset apply" or "preset delete" && hash == PresetActive(id) ? "PRESET_ACTIVE" : null;
                if (error is null)
                {
                    bool ok = true;
                    if (r.Command == "preset update") coordinator.Rename(id, hash, name);
                    else if (r.Command == "preset apply") ok = coordinator.Apply(id, variant!);
                    else ok = coordinator.Delete(id, hash);
                    if (!ok) error = "MUTATION_FAILED";
                }
                failed |= error is not null;
                results.Add(new { id = hash, ok = error is null, code = error });
            }
            return failed ? Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Depot_Unavailable) with
            { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Depot_Unavailable, Details: Protocol.Node(new { results })) }
                : Envelope.Success(new { results, version = VaultVersion(id) });
        }, token));
    }
}
