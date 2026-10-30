using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("dlc")]
    private async Task<Envelope> DlcAsync(AgentRequest r, CancellationToken ct)
    {
        try
        {
            AddNoData(r);
            var service = Acquisition.Dlc;
            if (r.Command == "dlc get")
            {
                long id = AddAppId(r.Arguments[0]);
                if (r.Flags.GetValueOrDefault("batch") is { } batchId)
                {
                    var batch = service.Find(batchId);
                    return batch is null || batch.AppId != id ? AddFailure("NOT_FOUND", 3) : Envelope.Success(new { batch = service.Describe(batch), available_actions = AvailableActions.For("dlc", c => c.Verb == "add" && batch.Phase != "ready" ? "BATCH_NOT_READY" : null) });
                }
                var details = await Acquisition.Api.GetDetailsAsync(id.ToString(), ct);
                if (!ApiContract.IsAppId(details?.BaseAppId)) return AddFailure("BASE_APP_REQUIRED", 6);
                var info = await Acquisition.Api.GetDlcInfoAsync(id.ToString(), details!.BaseAppId!, ct);
                return Envelope.Success(new { appid = id, depot_count = info?.DepotCount, have_count = info?.HaveCount,
                    missing_count = info?.MissingCount, depots = info?.Depots.OrderByDescending(d => d.Included), available_actions = AvailableActions.For("dlc", c => c.Verb == "add" && info is null ? "SOURCE_UNAVAILABLE" : null) });
            }
            if (r.Command == "dlc check")
            {
                long id = AddAppId(r.Arguments[0]);
                long[] owned = (r.Flags.GetValueOrDefault("owned") ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(AddAppId).ToArray();
                if (owned.Length > DlcBatchService.MaxOwned) return AddFailure("INVALID_SELECTION", 2);
                return await Snapshot(() =>
                {
                    if (!service.Check(id, owned)) return AddFailure("BUSY", 7);
                    var batch = service.GetState(id)!;
                    string job = Jobs.Register("dlc-check", async lifetime => { using var cancel = lifetime.Register(() => service.Cancel(batch.AppId, batch.Id)); await batch.Work.WaitAsync(lifetime); return batch.Phase == "failed" ? AddFailure("SOURCE_UNAVAILABLE", 7) : Envelope.Success(service.Describe(batch)); }, _hostLifetime);
                    return Envelope.Success(new { id = batch.Id, batch_id = batch.Id, job_id = job, state = "checking" });
                }, ct);
            }
            if (r.Command == "dlc add" && !r.Flags.ContainsKey("batch")) return await AddRemoteAsync(r, true, ct);
            return await MutateAsync(r, token => Snapshot(() =>
            {
                string batchId = r.Command == "dlc cancel" ? r.Arguments[0] : r.Flags["batch"]!;
                var batch = service.Find(batchId) ?? throw new MutationRejectedException("NOT_FOUND", 3);
                if (r.Command == "dlc add" && AddAppId(r.Arguments[0]) != batch.AppId) throw new MutationRejectedException("INVALID_APPID", 2);
                string version = MutationCoordinator.Hash(System.Text.Json.JsonSerializer.Serialize(service.Describe(batch)));
                string context = PlanContext;
                var targetVersions = batch.Items.Where(i => i.State == "ready").ToDictionary(i => i.AppId, i => AddVersion(i.AppId));
                var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, targetVersions.Keys.Select(i => i.ToString()).ToArray(),
                    [r.Command == "dlc add" ? Resources.Strings.Agent_Add_Impact : Resources.Strings.Agent_Add_CancelImpact, MutationCoordinator.Hash(string.Join(':', targetVersions.Values))], r.Command == "dlc add");
                return new PreparedMutation(plan, commitToken => Snapshot(() =>
                {
                    if (PlanContext != context || version != MutationCoordinator.Hash(System.Text.Json.JsonSerializer.Serialize(service.Describe(batch))) || targetVersions.Any(p => AddVersion(p.Key) != p.Value)) throw new MutationRejectedException("PLAN_STALE", 6);
                    if (r.Command == "dlc cancel") return service.Cancel(batch.AppId, batch.Id) ? Envelope.Success(new { id = batch.Id, cancel_requested = true }) : AddFailure("ACTION_UNAVAILABLE", 6);
                    AddSteamGate();
                    if (!service.Apply(batch.AppId, batch.Id)) throw new MutationRejectedException("BATCH_NOT_READY", 6);
                    string job = Jobs.Register("dlc-apply", async lifetime =>
                    {
                        using var cancel = lifetime.Register(() => service.Cancel(batch.AppId, batch.Id));
                        await batch.Work.WaitAsync(lifetime);
                        var outcome = service.Describe(batch);
                        return batch.Phase is "cancelled" or "failed" || batch.Items.Any(i => i.State is "failed" or "limit") ? Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Add_InstallFailed) with { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Add_InstallFailed, Details: Protocol.Node(outcome)) } : Envelope.Success(outcome);
                    }, _hostLifetime);
                    return Envelope.Success(new { id = job, job_id = job, batch_id = batch.Id, state = "running" });
                }, commitToken));
            }, token), ct);
        }
        catch (MutationRejectedException ex) { return AddFailure(ex.Code, ex.ExitCode); }
        catch (ApiException) { return AddFailure("SOURCE_UNAVAILABLE", 7); }
    }
}
