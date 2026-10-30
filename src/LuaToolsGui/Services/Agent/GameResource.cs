using System.IO;
using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("game")]
    private async Task<Envelope> GameAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "game install") return await MutateAsync(r, token => Snapshot(() =>
        {
            AddNoData(r); long id = AddAppId(r.Arguments.FirstOrDefault());
            var handler = new SteamInstallHandoff(steam);
            if (!handler.CanHandoff()) throw new MutationRejectedException("STEAM_PROTOCOL_ISOLATION_UNPROVEN", 5);
            if (libraryCache is null || !libraryCache.GetLoadedAppIds().Contains(id)) throw new MutationRejectedException("NOT_PENDING_ADDED", 6);
            string context = PlanContext;
            return new PreparedMutation(new(Guid.NewGuid().ToString("N"), context, context, [id.ToString()], [Resources.Strings.Agent_Finish_InstallImpact], true), commit => Snapshot(() =>
            {
                if (PlanContext != context) throw new MutationRejectedException("PLAN_STALE", 6);
                if (!handler.Install(id)) throw new MutationRejectedException("STEAM_PROTOCOL_ISOLATION_UNPROVEN", 5);
                return Envelope.Success(new { appid = id, state = "handed_off" });
            }, commit));
        }, token), ct);
        if (r.Command == "game add") return await AddRemoteAsync(r, false, ct);
        if (libraryNames is null || libraryDetails is null || libraryCache is null)
            return Envelope.Fail("ACTION_UNAVAILABLE", 6, Resources.Strings.Agent_Library_Unavailable);
        string? dir = await Snapshot(() => steam.StPlugInDir, ct);
        var entries = dir is null ? [] : await LibraryCoordinator.ScanAsync(dir, libraryNames, libraryDetails);
        if (r.Command == "game get")
        {
            long? id = long.TryParse(r.Arguments[0], out long number) && number > 0 && number < 2_000_000_000 ? number : SteamLinkParser.AppIdFrom(r.Arguments[0]);
            if (id is null) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments);
            var entry = entries.FirstOrDefault(e => e.AppId == id);
            bool identityOnly = r.Flags.ContainsKey("installed") || r.Flags.GetValueOrDefault("fields") is { } fields && fields.Split(',').All(f => f is "id" or "appid" || f.StartsWith("paths") || f.StartsWith("urls"));
            var info = libraryDetails.GetCached(id.Value);
            if (!identityOnly) info ??= await libraryDetails.ResolveAsync(id.Value, ct);
            if (!identityOnly) await libraryDetails.EnsureFullDetailsAsync(id.Value, ct);
            if (entry is null && info is null && !r.Flags.ContainsKey("installed")) return Envelope.Fail("NOT_FOUND", 3, Resources.Strings.Agent_Library_Unavailable);
            var row = Protocol.Node(GameData(entry ?? new(id.Value, "", default, info?.Name ?? string.Format(Resources.Strings.Common_AppFallback, id), false)))!.AsObject();
            var state = pluginAdd?.GetState(id.Value);
            row["add_status"] = Protocol.Node(new { generation = state?.Generation, checking = state?.Checking ?? false, busy = state?.Busy ?? false, sources_loaded = state?.SourcesLoaded ?? false,
                install_status = state?.InstallStatus is { } status ? LogRedactor.Redact(status) : null, install_failed = state?.InstallFailed ?? false,
                error = state?.Error is { } error ? LogRedactor.Redact(error) : null, cancelled = state?.Cancelled ?? false,
                sources = state?.Sources.Select(s => new { name = s.Name, status = s.Status, disabled_reasons = s.DisabledReasons }) });
            return Envelope.Success(row);
        }
        if (r.Command is "game delete" or "game remove-drm")
            return await LibraryMutationAsync(r, entries, ct);
        if (r.Flags.ContainsKey("cabinet"))
            return await Snapshot(() => GamePage(r, Protocol.Node(LibraryCoordinator.CabinetGames(cabinet?.Status.Games ?? []).Select(g => new { id = g.AppId, name = g.Name, cabinet = g }))!.AsArray()), ct);
        if (r.Flags.ContainsKey("featured"))
        {
            if (libraryApi is null || libraryHardware is null) return Envelope.Fail("ACTION_UNAVAILABLE", 6, Resources.Strings.Agent_Library_Unavailable);
            await libraryHardware.EnsureFreshAsync();
            var (top, fresh) = await libraryApi.GetFeaturedAsync(ct);
            return GamePage(r, Protocol.Node(top.Where(i => !libraryHardware.IsBlacklisted(i.Id)).Select(i => new { id = i.Id, name = i.Name, category = "top-sellers", image = i.LargeCapsuleImage })
                .Concat(fresh.Where(i => !libraryHardware.IsBlacklisted(i.Id)).Select(i => new { id = i.Id, name = i.Name, category = "new-releases", image = i.LargeCapsuleImage })))!.AsArray());
        }
        if (r.Command == "game search" && !r.Flags.ContainsKey("added") && !r.Flags.ContainsKey("installed"))
        {
            if (libraryApi is null || libraryHardware is null) return Envelope.Fail("ACTION_UNAVAILABLE", 6, Resources.Strings.Agent_Library_Unavailable);
            string q = r.Flags.GetValueOrDefault("query")?.Trim() ?? "";
            if (q.Length is 0 or > 100) return GamePage(r, []);
            await libraryHardware.EnsureFreshAsync();
            long? selected = q.Length >= 5 && long.TryParse(q, out long numeric) && numeric > 0 && numeric < 2_000_000_000 ? numeric : SteamLinkParser.AppIdFrom(q);
            if (selected is { } appid)
            {
                var details = await libraryApi.GetDetailsAsync(appid.ToString(), ct);
                return GamePage(r, details is null || libraryHardware.IsBlacklisted(appid) ? [] : Protocol.Node(new[] { new { id = details.AppId, appid = details.AppId, name = details.Name, image = details.HeaderImage } })!.AsArray());
            }
            var found = await libraryApi.SearchAsync(q, ct);
            return GamePage(r, Protocol.Node(found.Where(i => !libraryHardware.IsBlacklisted(i.AppId)).Select(i => new { id = i.AppId, appid = i.AppId, name = i.Name, image = i.Icon }))!.AsArray());
        }
        if (r.Flags.ContainsKey("pending-added"))
        {
            var ids = await Snapshot(() => libraryCache.GetLoadedAppIds().ToArray(), ct);
            return GamePage(r, Protocol.Node(ids.Select(id => new { id, appid = id, name = libraryNames.GetName(id), available_actions = AvailableActions.For("game") }))!.AsArray());
        }
        string F(string key) => r.Flags.GetValueOrDefault(key) ?? "Any";
        string price = F("price") switch { "free" => "Free", "paid" => "Paid", var v => v };
        string content = F("content") switch { "hide-adult" => "Hide adult", "adult-only" => "Adult only", var v => v };
        bool active = new[] { "type", "genre", "year", "price", "content" }.Any(r.Flags.ContainsKey);
        string query = r.Flags.GetValueOrDefault("query")?.Trim() ?? "";
        var filtered = entries.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || e.AppId.ToString().Contains(query))
            .Where(e => !active || libraryDetails.GetFilterData(e.AppId) is { } data && LibraryCoordinator.MatchesFilters(data, F("type"), F("genre"), F("year"), price, content));
        string sort = F("sort") switch { "name" => "Name (A–Z)", "release-date" => "Release date (newest)", "metacritic" => "Metacritic", "most-reviewed" => "Most reviewed", var v => v };
        return GamePage(r, Protocol.Node(LibraryCoordinator.Sort(filtered, sort, e => e.Name, e => e.AddedAt, e => libraryDetails.GetFilterData(e.AppId)).Select(GameData))!.AsArray());
    }
    private static string[] LibrarySelect(AgentRequest request, System.Text.Json.Nodes.JsonArray rows, string identity, string[] fields)
    {
        try { return BulkSelection.Select(request, rows, identity, fields); }
        catch (ArgumentException) { throw new MutationRejectedException("INVALID_SELECTION", 2); }
    }
    private object GameData(LibraryEntry e) => new { id = e.AppId, appid = e.AppId, name = e.Name, added_at = e.AddedAt, added = e.FilePath.Length > 0, installed = libraryInstalls?.GetInstallDir(e.AppId) is { } install && Directory.Exists(install),
        paths = new { lua = e.FilePath, install = libraryInstalls?.GetInstallDir(e.AppId) }, urls = new { store = $"steam://store/{e.AppId}", library = $"steam://nav/games/details/{e.AppId}", steamdb = $"https://steamdb.info/app/{e.AppId}/" },
        details = libraryDetails!.GetFilterData(e.AppId), overview = libraryDetails.GetOverview(e.AppId), version = LibraryCoordinator.Version(e.FilePath), available_actions = AvailableActions.ForReasons("game", c => GameReasons(c, e)) };
    private string[] GameReasons(Command command, LibraryEntry entry)
    {
        var reasons = new List<string>();
        if (command.Verb is "delete" or "remove-drm" && entry.FilePath.Length == 0) reasons.Add("NOT_INSTALLED");
        if (command.Verb == "remove-drm" && !settings.IsExecutableWorkflowEnabled("Steamless")) reasons.Add("WORKFLOW_DISABLED");
        if (command.Verb == "install")
        {
            if (!new SteamInstallHandoff(steam).CanHandoff()) reasons.Add("STEAM_PROTOCOL_ISOLATION_UNPROVEN");
            if (libraryCache is null || !libraryCache.GetLoadedAppIds().Contains(entry.AppId)) reasons.Add("NOT_PENDING_ADDED");
        }
        return reasons.ToArray();
    }
    private Envelope GamePage(AgentRequest r, System.Text.Json.Nodes.JsonArray data)
    {
        foreach (var row in data.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            if (!long.TryParse(row["appid"]?.ToString() ?? row["id"]?.ToString(), out long id)) continue;
            row["appid"] ??= id;
            row["added"] ??= steam.StPlugInDir is { } plug && File.Exists(Path.Combine(plug, id + ".lua"));
            row["installed"] ??= libraryInstalls?.GetInstallDir(id) is { } install && Directory.Exists(install);
        }
        return _pager.List(r.Command + string.Join(';', r.Flags.Where(f => f.Key is not ("cursor" or "limit" or "all" or "count" or "fields")).OrderBy(f => f.Key).Select(f => f.Key + "=" + f.Value)), data, r.Flags);
    }
    private Task<Envelope> LibraryMutationAsync(AgentRequest r, List<LibraryEntry> entries, CancellationToken ct) => MutateAsync(r, token => Snapshot(() =>
    {
        if (r.Flags.ContainsKey("data")) throw new MutationRejectedException("INVALID_DATA", 2);
        string[] ids = LibrarySelect(r, Protocol.Node(entries.Select(e => new { id = e.AppId.ToString(), name = e.Name }))!.AsArray(), "id", ["id", "name"]);
        var targets = ids.Select(id => entries.FirstOrDefault(e => e.AppId.ToString() == id)).ToArray();
        string Version() => targets.Length == 1 ? targets[0] is { } one ? LibraryCoordinator.Version(one.FilePath) : "missing" : MutationCoordinator.Hash(string.Join(':', targets.Select(e => e is null ? "missing" : LibraryCoordinator.Version(e.FilePath))));
        string version = Version(), context = PlanContext;
        bool drm = r.Command == "game remove-drm";
        if (drm && targets.Length != 1) throw new MutationRejectedException("INVALID_SELECTION", 2);
        if (drm && (librarySteamless is null || !settings.IsExecutableWorkflowEnabled("Steamless"))) throw new MutationRejectedException("WORKFLOW_DISABLED", 5);
        return new PreparedMutation(new(Guid.NewGuid().ToString("N"), context, version, ids, [Resources.Strings.Agent_Library_Impact], true), commit => Snapshot(() =>
        {
            if (context != PlanContext || version != Version()) throw new MutationRejectedException("PLAN_STALE", 6);
            if (drm && !settings.IsExecutableWorkflowEnabled("Steamless")) throw new MutationRejectedException("WORKFLOW_DISABLED", 5);
            if (drm)
            {
                var target = targets.SingleOrDefault() ?? throw new MutationRejectedException("NOT_FOUND", 3);
                string job = Jobs.Register("remove-drm", async lifetime =>
                {
                    var result = await LibraryCoordinator.RemoveDrmAsync(librarySteamless!, target.AppId, ct: lifetime);
                    return result.Failed ? Envelope.Fail("PATCH_FAILED", 1, Resources.Strings.Agent_Library_Unavailable) with { Error = new("PATCH_FAILED", 1, Resources.Strings.Agent_Library_Unavailable, Details: Protocol.Node(result)) } : Envelope.Success(result);
                }, _hostLifetime);
                return Envelope.Success(new { id = job, job_id = job, state = "running" });
            }
            var results = targets.Select((e, i) => new { id = ids[i], ok = e is not null && LibraryCoordinator.TryDelete(e.FilePath, out _) }).ToArray();
            return results.All(e => e.ok) ? Envelope.Success(new { results }) : Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Library_Unavailable) with { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Library_Unavailable, Details: Protocol.Node(new { results })) };
        }, commit));
    }, token), ct);
}
