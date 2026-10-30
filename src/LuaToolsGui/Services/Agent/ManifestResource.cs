using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("manifest")]
    private Task<Envelope> ManifestAsync(AgentRequest r, CancellationToken ct) => MutateAsync(r, async token =>
    {
        AddNoData(r);
        if (r.Arguments.Length != 1) throw new MutationRejectedException("INVALID_USAGE", 2);
        long id = AddAppId(r.Flags.GetValueOrDefault("appid"));
        var snapshot = await Snapshot(() => (Context: PlanContext, Version: AddVersion(id)), token);
        var fixes = await Acquisition.Api.GetDenuvoFixesAsync(id.ToString(), token);
        var fix = fixes?.Fixes.FirstOrDefault(f => f.Id == r.Arguments[0] && f.HasManifest) ?? throw new MutationRejectedException("NOT_FOUND", 3);
        var plan = new MutationPlan(Guid.NewGuid().ToString("N"), snapshot.Context, snapshot.Version, [id.ToString(), fix.Id], [Resources.Strings.Agent_Add_Impact, "force_locked=true"], true);
        return new PreparedMutation(plan, commitToken => Snapshot(() =>
        {
            AddSteamGate();
            if (auth.IsGuest) throw new MutationRejectedException("LOGIN_REQUIRED", 4);
            if (PlanContext != snapshot.Context || AddVersion(id) != snapshot.Version) throw new MutationRejectedException("PLAN_STALE", 6);
            var job = Acquisition.Factory.CreateDenuvoJob(fix.Id, "manifest", fix.ManifestFilename ?? $"{id}.zip", id, fixes!.Name, fix.Title,
                admitted: () => AddVersion(id) == snapshot.Version);
            var download = job.DownloadAsync;
            job = job with { DownloadAsync = (item, progress, lifetime) =>
            {
                if (auth.IsGuest) throw new DownloadAbortedException(Resources.Strings.Add_SignIn_Download);
                return download(item, progress, lifetime);
            }, ConfirmAsync = (_, _, _) => Task.FromResult(AddVersion(id) == snapshot.Version) };
            var item = queue.Enqueue(job);
            return Envelope.Success(new { id = item.Id, job_id = item.Id, state = "queued" });
        }, commitToken));
    }, ct);
}
