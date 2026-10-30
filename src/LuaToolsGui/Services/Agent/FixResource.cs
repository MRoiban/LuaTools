using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.Services.OnlineFixMe;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("fix")]
    private async Task<Envelope> FixAsync(AgentRequest r, CancellationToken ct)
    {
        if (fixes is null) return Envelope.Fail("NOT_IMPLEMENTED", 5, Resources.Strings.Agent_NotImplemented);
        long appId = 0;
        if (r.Flags.GetValueOrDefault("appid") is { } app && (!long.TryParse(app, out appId) || appId <= 0)) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments);
        string? provider = r.Flags.GetValueOrDefault("provider");
        if (provider is not (null or "lua.tools" or "online-fix.me")) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments);
        if (r.Command is "fix list" or "fix search" or "fix get")
        {
            if (appId == 0)
            {
                if (r.Command == "fix get" || r.Flags.ContainsKey("folder-url")) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments);
                var data = await fixes.ListAsync(ct);
                var rows = FixCoordinator.Filter(data, r.Flags.GetValueOrDefault("tag"), r.Flags.ContainsKey("my-games"), r.Flags.GetValueOrDefault("query") ?? "", provider == "online-fix.me");
                if (provider == "lua.tools" && auth.IsGuest) rows = [];
                return List(r, Protocol.Node(rows.Select(g => new { id = g.AppId, appid = g.AppId, name = g.Name, fix_count = g.FixCount, tags = g.Tags, available_actions = FixActions(r, installed: fixes.InstallDir(long.Parse(g.AppId)) is not null) }).ToArray())!.AsArray());
            }
            if (r.Flags.ContainsKey("folder-url") && fixes.InstallDir(appId) is null) return Envelope.Fail("GAME_NOT_INSTALLED", 6, Resources.Strings.Fixes_Toast_GameNotFound);
            var detail = await fixes.DetailAsync(appId, r.Flags.GetValueOrDefault("folder-url"), r.Flags.ContainsKey("refresh") || r.Flags.ContainsKey("folder-url"), ct);
            var entries = detail.Fixes.Where(f => provider is null || (f.Folder is null ? "lua.tools" : "online-fix.me") == provider)
                .Where(f => r.Flags.GetValueOrDefault("tag") is not { } tag || f.Fix.Tags.Any(t => t.Id == tag))
                .Select(f => new { id = f.Fix.Id, appid = detail.AppId, provider = f.Folder is null ? "lua.tools" : "online-fix.me", title = f.Fix.Title, description = f.Fix.Description, tags = f.Fix.Tags, installed = f.Installed, applied = f.Applied, version = FixVersion(appId, f.Fix.Id), available_actions = FixActions(r, f.Installed, f.Applied, f.Folder is not null, f.Fix.Id, appId) }).ToArray();
            return r.Command == "fix get" ? Envelope.Success(new { appid = detail.AppId, name = detail.Name, fixes = entries, provider_status = detail.ProviderStatus.ToString() }) : List(r, Protocol.Node(entries)!.AsArray());
        }
        return await _mutations.ExecuteAsync(r, async token =>
        {
            var data = BulkSelection.Data(r, "appid", "id", "provider");
            if (appId == 0 && data["appid"] is { } idNode && long.TryParse(idNode.ToString(), out long parsedId)) appId = parsedId;
            if (appId <= 0) throw new MutationRejectedException("INVALID_USAGE", 2);
            bool apply = r.Command == "fix apply";
            provider ??= data["provider"]?.GetValue<string>();
            if (apply && provider is not ("lua.tools" or "online-fix.me")) throw new MutationRejectedException("INVALID_USAGE", 2);
            // Denial precedes provider discovery, acquisition and queue admission.
            if (apply) { RequireGate("DenuvoFix"); if (provider == "lua.tools" && auth.IsGuest) throw new MutationRejectedException("AUTH_REQUIRED", 4); }
            FixDetail? detail = apply ? await fixes.DetailAsync(appId, ct: token) : null;
            var selectable = Protocol.Node(detail?.Fixes.Select(f => new { id = f.Fix.Id, provider = f.Folder is null ? "lua.tools" : "online-fix.me", applied = f.Applied }).ToArray() ?? [])!.AsArray();
            string[] ids = BulkSelection.Select(r, selectable, "id", ["provider", "applied"], data["id"]?.GetValue<string>());
            if (ids.Any(id => id.Length is 0 or > 128 || id is "." or ".." || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_')))) throw new MutationRejectedException("INVALID_USAGE", 2);
            return await Snapshot(() =>
            {
                string CurrentVersion() => ids.Length == 1 ? FixVersion(appId, ids[0]) : MutationCoordinator.Hash(string.Join(':', ids.Select(id => FixVersion(appId, id))));
                string version = CurrentVersion();
                return new PreparedMutation(new(Guid.NewGuid().ToString("N"), PlanContext, version, ids.Concat([SteamIdentity]).ToArray(),
                    [apply ? Resources.Strings.Agent_System_FixApply : Resources.Strings.Agent_System_FixRevert], true), commit => Snapshot(() =>
                    {
                        if (r.ExpectedSteam != SteamIdentity || version != CurrentVersion()) throw new MutationRejectedException("PLAN_STALE", 6);
                        if (apply) RequireGate("DenuvoFix");
                        var results = new List<object>(); int failed = 0;
                        foreach (string id in ids)
                        {
                            var entry = detail?.Fixes.FirstOrDefault(f => f.Fix.Id == id && (f.Folder is null ? "lua.tools" : "online-fix.me") == provider);
                            string? code = apply && entry is null ? "NOT_FOUND" : apply && entry!.Applied ? "ALREADY_APPLIED" : apply && !entry!.Installed ? "GAME_NOT_INSTALLED" : null;
                            if (apply && entry is not null && !entry.Fix.HasFix) code = "FIX_UNAVAILABLE";
                            if (!apply)
                            {
                                string? dir = fixes.InstallDir(appId);
                                if (dir is null) code = "GAME_NOT_INSTALLED";
                                else if (!File.Exists(ManifestJobFactory.GetFixRecordPath(dir, id))) code = "NOT_APPLIED";
                            }
                            if (code is not null) { failed++; results.Add(new { id, ok = false, code }); continue; }
                            if (apply)
                            {
                                try
                                {
                                    var item = queue.Enqueue(fixes.CreateJob(appId, detail!.Name, entry!.Fix, entry.Folder, "fix"));
                                    results.Add(new { id, ok = true, job_id = item.Id, state = "queued" });
                                }
                                catch (InvalidOperationException) { failed++; results.Add(new { id, ok = false, code = "WORKFLOW_DISABLED" }); }
                            }
                            else
                            {
                                string fixId = id;
                                string job = Jobs.Register("fix.revert", lifetime => Task.Run(() => SystemResult(fixes.Revert(appId, fixId, appId.ToString()).Ok), lifetime), _hostLifetime);
                                results.Add(new { id, ok = true, job_id = job, state = "accepted" });
                            }
                        }
                        return failed == 0 ? Envelope.Success(new { results }) : Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_System_Failed) with { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_System_Failed, Details: Protocol.Node(new { results })) };
                    }, commit));
            }, token);
        }, ct);
    }
    private string FixVersion(long appId, string id)
    {
        string? dir = fixes?.InstallDir(appId);
        string path = dir is null ? "" : ManifestJobFactory.GetFixRecordPath(dir, id);
        string stamp = File.Exists(path) ? new FileInfo(path).LastWriteTimeUtc.Ticks + ":" + new FileInfo(path).Length : "absent";
        return MutationCoordinator.Hash(PlanContext + dir + id + stamp);
    }
    private object[] FixActions(AgentRequest r, bool installed, bool applied = false, bool online = false, string? fixId = null, long appId = 0) => AvailableActions.ForReasons("fix", c =>
    {
        var reasons = new List<string>();
        if (c.Verb is "apply" or "revert" && !installed) reasons.Add("GAME_NOT_INSTALLED");
        if (c.Verb == "apply")
        {
            if (!settings.IsExecutableWorkflowEnabled("DenuvoFix")) reasons.Add("WORKFLOW_DISABLED");
            if (!online && auth.IsGuest) reasons.Add("AUTH_REQUIRED");
            if (applied) reasons.Add("ALREADY_APPLIED");
            if (fixId is not null && queue.FindActive(online ? $"onlinefixme:{appId}:{fixId}" : $"denuvo:{fixId}:fix") is not null) reasons.Add("FIX_ACTIVE");
        }
        if (c.Verb == "revert" && !applied) reasons.Add("NOT_APPLIED");
        return reasons.ToArray();
    }, r.Flags.ContainsKey("read-only"));
}
