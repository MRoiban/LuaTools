using System.IO;
using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private string ReconcileVersion(long id) => MutationCoordinator.Hash(VaultVersion(id) + string.Join(':', vault!.EnumerateLooseBuildLuas().Where(v => v.AppId == id).OrderBy(v => v.Path).Select(v => v.BuildId + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(v.Path))))));
    [AgentResource("depot")]
    private async Task<Envelope> DepotAsync(AgentRequest r, CancellationToken ct)
    {
        if (vault is null) return Envelope.Fail("SERVICE_UNAVAILABLE", 7, Resources.Strings.Agent_Depot_Unavailable);
        if (r.Command is "depot plan" or "depot download") return await PlanDepotDownload(r, ct);
        if (r.Command == "depot list" && r.Flags.ContainsKey("games")) return await ReadDepotGames(r, ct);
        if (r.Command == "depot update")
        {
            long editApp;
            try { editApp = DepotAppId(r); } catch (MutationRejectedException ex) { return Envelope.Fail(ex.Code, ex.ExitCode, Resources.Strings.Agent_Depot_Unavailable); }
            var metadata = depotInfo is null ? null : await depotInfo.GetAsync(editApp, ct);
            if (metadata is null) return Envelope.Fail("METADATA_UNAVAILABLE", 7, Resources.Strings.Agent_Depot_Unavailable);
            return await _mutations.ExecuteAsync(r, token => Snapshot(() => PrepareDepotEdit(r, metadata), token), ct);
        }
        if (r.Command is "depot list" or "depot search" or "depot get") return await ReadDepots(r, ct);
        if (r.Command != "depot reconcile") return Envelope.Fail("REFACTOR_REQUIRED", 5, Resources.Strings.Agent_Depot_Unavailable);
        return await _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            long id = DepotAppId(r);
            ValidateScalarInput(r);
            string version = ReconcileVersion(id), context = PlanContext;
            var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, [id + ":live-and-stored-presets"], [Resources.Strings.Agent_Depot_ReconcileImpact], true);
            return new PreparedMutation(plan, commitToken => Snapshot(() =>
            {
                if (PlanContext != context || ReconcileVersion(id) != version) throw new MutationRejectedException("PLAN_STALE", 6);
                new DepotPresetCoordinator(vault).Reconcile(id);
                if (r.Flags.ContainsKey("refresh") && depotInfo is not null)
                {
                    depotInfo.Invalidate(id);
                    string job = Jobs.Register("depot.reconcile-refresh", async lifetime =>
                    {
                        var metadata = await depotInfo.GetAsync(id, lifetime);
                        return metadata is null ? Envelope.Fail("METADATA_UNAVAILABLE", 7, Resources.Strings.Agent_Depot_Unavailable) : Envelope.Success(new { appid = id, refreshed = true, version = ReconcileVersion(id) });
                    }, _hostLifetime);
                    return Envelope.Success(new { appid = id, job_id = job, reconciled = true });
                }
                return Envelope.Success(new { appid = id, version = ReconcileVersion(id), presets = vault.GetVariants(id).Select(v => PresetRow(id, v)) });
            }, commitToken));
        }, token), ct);
    }
    private async Task<Envelope> ReadDepots(AgentRequest r, CancellationToken ct)
    {
        long id;
        try { id = DepotAppId(r); }
        catch (MutationRejectedException ex) { return Envelope.Fail(ex.Code, ex.ExitCode, Resources.Strings.Agent_Depot_Unavailable); }
        if (r.Flags.ContainsKey("games") || depotInfo is null) return Envelope.Fail("REFACTOR_REQUIRED", 5, Resources.Strings.Agent_Depot_Unavailable);
        var info = await depotInfo.GetAsync(id, ct);
        if (info is null) return Envelope.Fail("METADATA_UNAVAILABLE", 7, Resources.Strings.Agent_Depot_Unavailable);
        var lua = LuaFileParser.ParseText(vault!.ReadLiveText(id), id);
        var sections = DepotProjection.BuildRows(info, lua, appid => appList?.GetName(appid) ?? appInfo?.GetCached(appid)?.Name);
        var rows = sections.InLua.Select(row => (row, section: "in-lua"))
            .Concat(sections.Missing.Select(row => (row, section: "missing")))
            .Concat(sections.Unknown.Select(row => (row, section: "unknown")));
        if (r.Flags.GetValueOrDefault("section") is { } section) rows = rows.Where(x => x.section == section);
        if (r.Flags.GetValueOrDefault("query") is { } query) rows = rows.Where(x => x.row.Matches(query));
        if (r.Command == "depot get") rows = rows.Where(x => x.row.Id.ToString() == r.Arguments[0]);
        var output = rows.Select(x => new { id = x.row.Id, appid = id, x.section, title = x.row.Title, size = x.row.Size,
            os = x.row.Os, language = x.row.Language, enabled = x.row.IsEnabled, locked = x.row.IsLocked,
            manifest_id = x.row.ManifestId, commented_manifest_id = x.row.CommentedManifestId,
            public_manifest_id = x.row.PublicManifestId, is_dlc = x.row.IsDlc, is_shared = x.row.IsShared,
            has_key = DepotProjection.Declarations(lua).GetValueOrDefault(x.row.ToggleId)?.HasKey ?? false,
            version = VaultVersion(id), available_actions = AvailableActions.For("depot") }).ToArray();
        return r.Command == "depot get" ? output.Length == 0 ? Envelope.Fail("NOT_FOUND", 3, Resources.Strings.Agent_Depot_Unavailable) : Envelope.Success(output[0])
            : List(r, Protocol.Node(output)!.AsArray());
    }

    private PreparedMutation PrepareDepotEdit(AgentRequest r, AppDepotInfo info)
    {
        long id = DepotAppId(r);
        System.Text.Json.Nodes.JsonObject data;
        try { data = BulkSelection.Data(r, "locked", "enabled"); } catch (System.Text.Json.JsonException) { throw new MutationRejectedException("INVALID_DATA", 2); }
        bool? Value(string key)
        {
            string? text = r.Flags.GetValueOrDefault(key) ?? data[key]?.ToString();
            if (text is null) return null;
            return bool.TryParse(text, out bool value) ? value : throw new MutationRejectedException("INVALID_VALUE", 2);
        }
        bool? locked = Value("locked"), enabled = Value("enabled");
        if (locked is null && enabled is null) throw new MutationRejectedException("INVALID_VALUE", 2);
        string? hash = r.Flags.GetValueOrDefault("preset");
        var target = hash is null ? null : vault!.GetVariants(id).FirstOrDefault(v => v.Hash == hash) ?? throw new MutationRejectedException("NOT_FOUND", 3);
        string? active = ViewModels.BuildsViewModel.ResolveActiveHash(vault!.GetActiveHash(id), vault.GetVariants(id), vault.GetEditBase(id));
        bool live = hash is null || hash == active;
        string before = live ? vault.ReadLiveText(id) : vault.ReadText(id, hash!);
        var declarations = DepotProjection.Declarations(LuaFileParser.ParseText(before, id));
        var sections = DepotProjection.BuildRows(info, LuaFileParser.ParseText(before, id), appid => appList?.GetName(appid) ?? appInfo?.GetCached(appid)?.Name);
        var depotRows = sections.InLua.ToDictionary(row => row.Id);
        var rows = Protocol.Node(depotRows.Keys.Select(key => new { id = key.ToString() }))!.AsArray();
        string[] targets;
        try { targets = BulkSelection.Select(r, rows, "id", ["id"]); } catch (ArgumentException) { throw new MutationRejectedException("INVALID_SELECTION", 2); }
        foreach (string key in targets)
        {
            if (!long.TryParse(key, out long depot) || !depotRows.TryGetValue(depot, out var row) || !declarations.TryGetValue(row.ToggleId, out var entry)) throw new MutationRejectedException("NOT_DECLARED", 5);
            if (locked is not null && entry.ManifestId is null && entry.CommentedManifestId is null) throw new MutationRejectedException("NO_MANIFEST_PIN", 5);
            if (enabled is not null && depotRows[depot].IsBaseApp) throw new MutationRejectedException("BASE_APP", 5);
        }
        string version = VaultVersion(id), context = PlanContext;
        var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, targets.Select(key => id + ":" + (live ? "live" : hash) + ":" + key).ToArray(),
            [live ? Resources.Strings.Agent_Depot_LiveEditImpact : Resources.Strings.Agent_Depot_StoredEditImpact], true);
        return new(plan, token => Snapshot(() =>
        {
            if (PlanContext != context || VaultVersion(id) != version) throw new MutationRejectedException("PLAN_STALE", 6);
            var result = new DepotPresetCoordinator(vault).Edit(id, target, live, text =>
            {
                foreach (string key in targets)
                {
                    long depot = depotRows[long.Parse(key)].ToggleId;
                    if (locked is { } pin) text = LuaEditor.SetDepotLocked(text, depot, pin);
                    if (enabled is { } on) text = LuaEditor.SetDepotEnabled(text, depot, on);
                }
                return text;
            });
            return result.Ok ? Envelope.Success(new { results = targets.Select(key => new { id = key, ok = true }), hash = result.Hash, version = VaultVersion(id) })
                : Envelope.Fail("MUTATION_FAILED", 1, Resources.Strings.Agent_Depot_Unavailable);
        }, token));
    }

    private async Task<Envelope> ReadDepotGames(AgentRequest r, CancellationToken ct)
    {
        if (r.Flags.ContainsKey("appid")) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments);
        if (appList is not null) await appList.EnsureLoadedAsync();
        string? dir = await Snapshot(() => steam.StPlugInDir, ct);
        var rows = await Task.Run(() =>
        {
            string query = r.Flags.GetValueOrDefault("query") ?? "";
            return new DepotPresetCoordinator(vault!).Games(dir, appList, appInfo)
                .Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || g.AppId.ToString().Contains(query))
                .Select(g => new { id = g.AppId, appid = g.AppId, name = g.Name, installed = System.IO.File.Exists(g.FilePath), active_hash = vault!.GetActiveHash(g.AppId), version = VaultVersion(g.AppId), available_actions = AvailableActions.For("depot", c => c.Verb == "download" && !settings.DepotDownloaderEnabled ? "WORKFLOW_DISABLED" : null) }).ToArray();
        }, ct);
        return List(r, Protocol.Node(rows)!.AsArray());
    }
    private async Task<Envelope> PlanDepotDownload(AgentRequest r, CancellationToken ct)
    {
        if (depotTool is null || depotInfo is null || factory is null) return Envelope.Fail("SERVICE_UNAVAILABLE", 7, Resources.Strings.Agent_Depot_Unavailable);
        var planning = r.Command == "depot plan" ? r with { Flags = r.Flags.Where(f => f.Key != "read-only").ToDictionary(f => f.Key, f => f.Value) } : r;
        if (r.Command == "depot plan") planning.Flags["dry-run"] = null;
        return await _mutations.ExecuteAsync(planning, async token =>
        {
            ValidateScalarInput(r);
            long id = DepotAppId(r);
            if (r.Command == "depot download") RequireGate("DepotDownloader");
            var info = await depotInfo.GetAsync(id, token) ?? throw new MutationRejectedException("METADATA_UNAVAILABLE", 7);
            var rows = DepotProjection.BuildRows(info, LuaFileParser.ParseText(vault!.ReadLiveText(id), id), appid => appList?.GetName(appid)).InLua;
            var picks = await Task.Run(() => DepotDownloadPlanning.Build(rows, depotTool, id, SteamService.SteamLanguage), token);
            var requested = (r.Flags.GetValueOrDefault("depot") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (r.Flags.ContainsKey("all-depots") && requested.Length > 0) throw new MutationRejectedException("INVALID_SELECTION", 2);
            if (requested.Any(key => !picks.Any(p => p.DepotId.ToString() == key))) throw new MutationRejectedException("NOT_FOUND", 3);
            var selected = picks.Where(p => r.Flags.ContainsKey("all-depots") || requested.Length > 0 ? r.Flags.ContainsKey("all-depots") || requested.Contains(p.DepotId.ToString()) : p.IsSelected).ToArray();
            if (selected.Length == 0 || selected.Any(p => !p.CanDownload)) throw new MutationRejectedException("DEPOT_UNAVAILABLE", 6);
            string? output = r.Flags.GetValueOrDefault("output");
            if (output is not null) output = DepotDownloadPlanning.ValidateOutput(output, steam.EffectivePath);
            if (r.Command == "depot download" && output is null) throw new MutationRejectedException("OUTPUT_REQUIRED", 2);
            var selections = selected.Select(p => new DepotSelection(p.DepotId, p.ManifestId, p.ManifestPath, p.Size) { FromAppId = p.FromAppId }).ToArray();
            string version = MutationCoordinator.Hash(VaultVersion(id) + string.Join(',', depotTool.ResolveKeys(id).OrderBy(k => k.Key).Select(k => k.Key + ":" + MutationCoordinator.Hash(k.Value))));
            string context = await Snapshot(() => PlanContext, token);
            long size = selected.Sum(p => p.Size);
            var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, selected.Select(p => id + ":live:" + p.DepotId + ":" + p.ManifestId).ToArray(),
                [Resources.Strings.Agent_Finish_DepotImpact, System.Text.Json.JsonSerializer.Serialize(new { output, estimated_bytes = size, disabled_reasons = settings.DepotDownloaderEnabled ? Array.Empty<string>() : new[] { "WORKFLOW_DISABLED" }, depots = selected.Select(p => new { id = p.DepotId, manifest_id = p.ManifestId, size = p.Size, needs_fetch = p.NeedsFetch }) })], true);
            return new PreparedMutation(plan, commit => Snapshot(() =>
            {
                if (r.Command == "depot plan") return Envelope.Success(new { plan });
                if (PlanContext != context || MutationCoordinator.Hash(VaultVersion(id) + string.Join(',', depotTool.ResolveKeys(id).OrderBy(k => k.Key).Select(k => k.Key + ":" + MutationCoordinator.Hash(k.Value)))) != version) throw new MutationRejectedException("PLAN_STALE", 6);
                RequireGate("DepotDownloader");
                DepotDownloadPlanning.ValidateOutput(output!, steam.EffectivePath);
                if (size > 0 && DepotDownloaderService.FreeSpaceFor(output!) is { } free && free < size) throw new MutationRejectedException("NO_SPACE", 6);
                string selectedSteam = SteamIdentity;
                var item = queue.Enqueue(factory.CreateDepotJob(id, appList?.GetName(id) ?? id.ToString(), selections, output!, revalidate: () =>
                {
                    if (selectedSteam != SteamIdentity) throw new MutationRejectedException("CONTEXT_CHANGED", 6);
                    RequireGate("DepotDownloader");
                    DepotDownloadPlanning.ValidateOutput(output!, steam.EffectivePath);
                }));
                return Envelope.Success(new { id = item.Id, job_id = item.Id, state = item.Status.ToString() });
            }, commit));
        }, ct);
    }
}
