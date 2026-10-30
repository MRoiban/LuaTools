using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("job")]
    private async Task<Envelope> JobAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "job list")
        {
            if (r.Flags.GetValueOrDefault("idempotency-key") is { } key)
            {
                var receipt = await _mutations.FindAsync(key, ct);
                var ids = JobReceipts.Ids(receipt);
                if (receipt is { Ok: false } && ids.Length == 0) return receipt;
                return await Snapshot(() => List(r, Protocol.Node(ids.Select(Jobs.Get).Where(x => x is not null).ToArray())!.AsArray()), ct);
            }
            return await Snapshot(() => List(r, Protocol.Node(Jobs.List(r.Flags.ContainsKey("active")))!.AsArray()), ct);
        }
        if (r.Command is "job get" or "job wait") return await Snapshot(() => Jobs.Get(r.Arguments[0]) is { } value
            ? Envelope.Success(value) : Envelope.Fail("JOB_EXPIRED", 3, Resources.Strings.Agent_JobMissing), ct);
        if (r.Command == "job confirm" && acquisition?.Local.Operations.FirstOrDefault(b => b.Id == r.Arguments.FirstOrDefault()) is { } batch)
            return await MutateAsync(r, token => Snapshot(() =>
            {
                BulkSelection.Data(r);
                string version = AgentJobRegistry.LocalVersion(batch), context = PlanContext;
                if (!ConsentCoordinator.HasPending(batch.Id)) throw new MutationRejectedException("NO_PENDING_DECISION", 6);
                return new PreparedMutation(new(Guid.NewGuid().ToString("N"), context, version, [batch.Id], [Resources.Strings.Agent_Finish_DecisionImpact], r.Flags.GetValueOrDefault("decision") == "replace"), commit => Snapshot(() =>
                {
                    if (context != PlanContext || version != AgentJobRegistry.LocalVersion(batch) || !ConsentCoordinator.Decide(batch.Id, r.Flags.GetValueOrDefault("decision") == "replace")) throw new MutationRejectedException("PLAN_STALE", 6);
                    return Envelope.Success(new { id = batch.Id, decision = r.Flags.GetValueOrDefault("decision") });
                }, commit));
            }, token), ct);
        return await MutateAsync(r, token => Snapshot(() =>
        {
            string[] targets;
            try { targets = BulkSelection.Select(r, Protocol.Node(Jobs.List())!.AsArray(), "id", ["id", "state", "kind"]); }
            catch (ArgumentException) { throw new MutationRejectedException("INVALID_SELECTION", 2); }
            if (r.Flags.ContainsKey("data")) throw new MutationRejectedException("INVALID_DATA", 2);
            var items = targets.Select(Jobs.Find).ToArray();
            string verb = r.Command.Split(' ')[1];
            bool Supported(DownloadItem? item) => item is not null && (verb switch
            {
                "pause" => item.CanPause, "resume" => item.CanResume && item.RunningAttempt is not { IsCompleted: false },
                "retry" => item.CanRetry, "cancel" => item.CanCancel && (!r.Flags.ContainsKey("delete-created") || item.Job.Kind == DownloadKind.Depot && DepotDownloaderService.HasDownloadedContent(item.Job.OutputPath)),
                "confirm" => item.NeedsAction && ConsentCoordinator.HasPending(item.Id), _ => false,
            });
            string version = items.Length == 1 ? items[0] is { } one ? AgentJobRegistry.Version(one) : "missing"
                : MutationCoordinator.Hash(string.Join(':', items.Select(i => i is null ? "missing" : AgentJobRegistry.Version(i))));
            string context = PlanContext;
            var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, targets,
                [verb == "retry" ? Resources.Strings.Agent_RetryImpact : r.Command], r.Flags.ContainsKey("delete-created") || verb == "retry" || verb == "confirm" && r.Flags.GetValueOrDefault("decision") == "replace");
            return new PreparedMutation(plan, commitToken => Snapshot(() =>
            {
                string fresh = items.Length == 1 ? items[0] is { } one ? AgentJobRegistry.Version(one) : "missing"
                    : MutationCoordinator.Hash(string.Join(':', items.Select(i => i is null ? "missing" : AgentJobRegistry.Version(i))));
                if (PlanContext != context || fresh != version) throw new MutationRejectedException("PLAN_STALE", 6);
                var results = new List<object>(); bool failed = false; object? last = null;
                for (int index = 0; index < items.Length; index++)
                {
                    var item = items[index];
                    if (!Supported(item))
                    {
                        if (items.Length == 1) throw new MutationRejectedException(item is null ? "JOB_EXPIRED" : "ACTION_UNAVAILABLE", item is null ? 3 : 6);
                        failed = true; results.Add(new { id = targets[index], ok = false, code = "ACTION_UNAVAILABLE" }); continue;
                    }
                    switch (verb)
                    {
                        case "pause": queue.Pause(item!); break;
                        case "resume": queue.Resume(item!); break;
                        case "cancel":
                            if (r.Flags.ContainsKey("delete-created"))
                            {
                                var cancelled = item!;
                                string operation = Jobs.Register("depot-cleanup", async _ =>
                                    await DownloadCancellationCoordinator.CancelAsync(queue, cancelled, true)
                                        ? Envelope.Success(new { child_job_id = cancelled.Id })
                                        : Envelope.Fail("CLEANUP_FAILED", 1, Resources.Strings.Agent_MutationRejected), _hostLifetime);
                                last = new { id = operation, job_id = operation, child_job_id = cancelled.Id, state = "running" };
                                results.Add(new { id = operation, ok = true, job = last });
                                continue;
                            }
                            queue.Cancel(item!); break;
                        case "retry": item = queue.Retry(item!); break;
                        case "confirm": if (!ConsentCoordinator.Decide(item!.Id, r.Flags.GetValueOrDefault("decision") == "replace")) throw new MutationRejectedException("PLAN_STALE", 6); break;
                    }
                    last = AgentJobRegistry.Describe(item!);
                    results.Add(new { id = item!.Id, ok = true, job = last });
                }
                if (failed) return Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_MutationRejected) with
                { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_MutationRejected, Details: Protocol.Node(new { results })) };
                return items.Length == 1 ? Envelope.Success(last) : Envelope.Success(new { results });
            }, commitToken));
        }, token), ct);
    }
}
