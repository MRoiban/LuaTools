using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("job")]
    private async Task<Envelope> JobAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "job list") return await Snapshot(() => List(r, Protocol.Node(Jobs.List(r.Flags.ContainsKey("active")))!.AsArray()), ct);
        if (r.Command is "job get" or "job wait") return await Snapshot(() => Jobs.Get(r.Arguments[0]) is { } value
            ? Envelope.Success(value) : Envelope.Fail("JOB_EXPIRED", 3, Resources.Strings.Agent_JobMissing), ct);
        return await _mutations.ExecuteAsync(r, token => Snapshot(() =>
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
                "retry" => item.CanRetry, "cancel" => item.CanCancel && !r.Flags.ContainsKey("delete-created"),
                "confirm" => item.NeedsAction && ConsentCoordinator.HasPending(item.Id), _ => false,
            });
            string version = items.Length == 1 ? items[0] is { } one ? AgentJobRegistry.Version(one) : "missing"
                : MutationCoordinator.Hash(string.Join(':', items.Select(i => i is null ? "missing" : AgentJobRegistry.Version(i))));
            var plan = new MutationPlan(Guid.NewGuid().ToString("N"), InstanceId + SteamIdentity, version, targets,
                [verb == "retry" ? Resources.Strings.Agent_RetryImpact : r.Command], verb == "retry" || verb == "confirm" && r.Flags.GetValueOrDefault("decision") == "replace");
            return new PreparedMutation(plan, commitToken => Snapshot(() =>
            {
                string fresh = items.Length == 1 ? items[0] is { } one ? AgentJobRegistry.Version(one) : "missing"
                    : MutationCoordinator.Hash(string.Join(':', items.Select(i => i is null ? "missing" : AgentJobRegistry.Version(i))));
                if (fresh != version) throw new MutationRejectedException("PLAN_STALE", 6);
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
                        case "cancel": queue.Cancel(item!); break;
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
