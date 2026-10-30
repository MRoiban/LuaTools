using System.IO;
using System.Text.Json;
using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private static void ValidateAddInputs(AgentRequest r)
    {
        if (r.Files is not { Length: > 0 } files || r.InputPaths.Length != files.Length || files.Any(f => !FileSubmission.Valid(f))) throw new MutationRejectedException("FILE_SUBMISSION_REQUIRED", 2);
        if (files.Length > FileSubmission.MaxFiles || files.Sum(f => f.Length) > FileSubmission.MaxBytes) throw new MutationRejectedException("INPUT_LIMIT", 2);
        string? digests = r.Flags.GetValueOrDefault("file") ?? r.Flags.GetValueOrDefault("diff-file");
        if (digests != string.Join(',', files.Select(f => f.Sha256))) throw new MutationRejectedException("INPUT_DIGEST_MISMATCH", 2);
    }
    [AgentResource("lua")]
    private async Task<Envelope> LuaAsync(AgentRequest r, CancellationToken ct)
    {
        try
        {
            AddNoData(r);
            if (r.Command == "lua get")
            {
                long id = AddAppId(r.Arguments[0]);
                if (r.Flags.ContainsKey("diff-file"))
                {
                    ValidateAddInputs(r);
                    if (r.InputPaths.Length != 1) return AddFailure("INVALID_USAGE", 2);
                    var diff = await Acquisition.Overwrites.InspectAsync(r.InputPaths[0], id, ct: ct);
                    return Envelope.Success(new { appid = id, version = diff.Version, diff, available_actions = AvailableActions.For("lua") });
                }
                string? path = Acquisition.Installer.ReadInstalledLua(id);
                if (path is null) return AddFailure("NOT_FOUND", 3);
                var contents = LuaFileParser.Parse(path, id);
                // Canonical public representation: never send key/token-bearing live text or LuaEntry.
                return Envelope.Success(new { appid = id, version = AddVersion(id), entries = contents?.Entries.Select(e => new { id = e.Id, has_key = e.HasKey, manifest_id = e.ManifestId }),
                    content = r.Flags.ContainsKey("content") ? string.Join('\n', contents?.Entries.Select(e => $"addappid({e.Id})") ?? []) : null,
                    available_actions = AvailableActions.For("lua") });
            }
            if (r.Command == "lua update") return await UpdateLiveLua(r, ct);
            return await _mutations.ExecuteAsync(r, async token =>
            {
                ValidateAddInputs(r);
                if (r.Files!.Any(f => !LuaInstaller.IsInstallable(f.Name))) throw new MutationRejectedException("INVALID_FILE_TYPE", 2);
                var diffs = new List<OverwriteDiff>();
                for (int i = 0; i < r.InputPaths.Length; i++)
                {
                    string path = r.InputPaths[i];
                    long? id = Path.GetExtension(path).Equals(".lua", StringComparison.OrdinalIgnoreCase) ? LuaInstaller.AppIdFromFileName(path)
                        : Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) ? LuaInstaller.AppIdForZip(path) : null;
                    if (id is { } appId) diffs.Add(await Acquisition.Overwrites.InspectAsync(path, appId, enrich: false, ct: token));
                }
                string version = MutationCoordinator.Hash(JsonSerializer.Serialize(diffs));
                string context = await Snapshot(() => PlanContext, token);
                var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, r.Files!.Select(f => f.Name).ToArray(),
                    [Resources.Strings.Agent_Add_LocalImpact, JsonSerializer.Serialize(diffs, Protocol.Json)], true);
                return new PreparedMutation(plan, commitToken => Snapshot(() =>
                {
                    AddSteamGate();
                    if (PlanContext != context || diffs.Any(d => AddVersion(d.AppId) != d.Version)) throw new MutationRejectedException("PLAN_STALE", 6);
                    string[] paths = r.InputPaths;
                    r.RetainInputs = true;
                    string job;
                    try { job = Jobs.Register("lua-add", async lifetime =>
                    {
                        try
                        {
                            var results = await Acquisition.Local.InstallAsync(paths, (_, diff, _) => diffs.Any(d => d.AppId == diff.AppId && d.Version == diff.Version) ? Task.FromResult(true) : throw new MutationRejectedException("PLAN_STALE", 6), lifetime);
                            return results.Any(x => !x.Ok) ? Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Add_InstallFailed) with { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Add_InstallFailed, Details: Protocol.Node(new { results })) } : Envelope.Success(new { results });
                        }
                        finally { AgentInputStaging.DeleteRetained(paths); }
                    }, _hostLifetime); }
                    catch { r.RetainInputs = false; throw; }
                    return Envelope.Success(new { id = job, job_id = job, state = "running" });
                }, commitToken));
            }, ct);
        }
        catch (MutationRejectedException ex) { return AddFailure(ex.Code, ex.ExitCode); }
    }
    private Task<Envelope> UpdateLiveLua(AgentRequest r, CancellationToken ct) => _mutations.ExecuteAsync(r, async token =>
    {
        if (vault is null) throw new MutationRejectedException("SERVICE_UNAVAILABLE", 7);
        ValidateScalarInput(r);
        ValidateAddInputs(r);
        if (r.Files!.Length != 1) throw new MutationRejectedException("INVALID_USAGE", 2);
        long id = AddAppId(r.Arguments[0]);
        string text = await File.ReadAllTextAsync(r.InputPaths[0], token);
        try { ManifestContentPolicy.ValidateLua(System.Text.Encoding.UTF8.GetBytes(text), id); }
        catch { throw new MutationRejectedException("INVALID_LUA", 2); }
        string version = VaultVersion(id), context = await Snapshot(() => PlanContext, token);
        string origin = vault.GetEditBase(id) ?? vault.GetActiveVariant(id)?.Hash ?? "default";
        return new PreparedMutation(new(Guid.NewGuid().ToString("N"), context, version, [id + ":live:origin:" + origin],
            [Resources.Strings.Agent_Finish_LuaImpact], true), commit => Snapshot(() =>
        {
            if (PlanContext != context || VaultVersion(id) != version) throw new MutationRejectedException("PLAN_STALE", 6);
            var saved = new DepotPresetCoordinator(vault).SaveEdit(id, text);
            return saved is null ? AddFailure("SAVE_FAILED", 1) : Envelope.Success(new { appid = id, hash = saved.Hash, version = VaultVersion(id) });
        }, commit));
    }, ct);
}
