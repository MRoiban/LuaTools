using System.Text.Json;
using System.IO;
using System.Text.Json.Nodes;
using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private Envelope SystemList(AgentRequest r, JsonArray rows)
    {
        if (r.Flags.GetValueOrDefault("sort") == "reverse") rows = new JsonArray(rows.Reverse().Select(n => n?.DeepClone()).ToArray());
        string key = r.Command + ":" + string.Join(';', r.Flags.Where(f => f.Key is "appid" or "query" or "tag" or "provider" or "my-games" or "sort" or "read-only").OrderBy(f => f.Key).Select(f => f.Key + "=" + f.Value));
        return _pager.List(key, rows, r.Flags);
    }
    private string SystemVersion(string target)
    {
        string[] names = target switch
        {
            "mode" => ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"],
            "cloudredirect" => ["cloud_redirect.dll", "opensteamtool.toml"],
            "plugin" => ["winmm.dll", "winmm_real.dll"], _ => [],
        };
        string Stamp(string path)
        {
            try { var info = new FileInfo(path); return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "absent"; }
            catch (IOException) { return "unavailable"; }
            catch (UnauthorizedAccessException) { return "unavailable"; }
        }
        return MutationCoordinator.Hash(PlanContext + target + unlocker.SelectedMode + updates.StagedVersion +
            string.Join(':', names.Select(n => steam.EffectivePath is { } root ? Stamp(Path.Combine(root, n)) : "absent")) +
            (target == "plugin" ? Stamp(BundledPlugin.ReceiptPath) : ""));
    }
    private void RequireSystemContext(AgentRequest r, string version, string target)
    {
        if (r.ExpectedSteam != SteamIdentity || SystemVersion(target) != version) throw new MutationRejectedException("PLAN_STALE", 6);
    }
    private void RequireGate(string component)
    {
        if (!settings.IsExecutableWorkflowEnabled(component)) throw new MutationRejectedException("WORKFLOW_DISABLED", 5);
    }
    private bool RequireSteamState()
    {
        if (!steam.IsValid) throw new MutationRejectedException("STEAM_NOT_FOUND", 6);
        return SelectedSteamRunning() ?? throw new MutationRejectedException("STEAM_IDENTITY_UNAVAILABLE", 6);
    }
    private void RequireProcessConsent(AgentRequest r)
    {
        if (RequireSteamState() && !r.Flags.ContainsKey("restart-steam")) throw new MutationRejectedException("STEAM_RUNNING", 6);
        if (!r.Flags.ContainsKey("yes")) throw new MutationRejectedException("CONSENT_REQUIRED", 8);
    }
    private Envelope RegisterSystemJob(string kind, Func<CancellationToken, Task<Envelope>> operation) =>
        Envelope.Success(new { job_id = Jobs.Register(kind, token => Task.Run(async () =>
        {
            try { return await operation(token); }
            catch (MutationRejectedException ex) { return Envelope.Fail(ex.Code, ex.ExitCode, Resources.Strings.Agent_System_Failed); }
        }, token), _hostLifetime), state = "accepted" });
    private static Envelope SystemResult(bool success) => success ? Envelope.Success(new { state = "completed" })
        : Envelope.Fail("OPERATION_FAILED", 1, Resources.Strings.Agent_System_Failed);
    private static void ValidateScalarInput(AgentRequest r, params string[] fields)
    {
        // Unsupported bulk operands must not be silently accepted as a single-target mutation.
        if (r.Flags.ContainsKey("ids") || r.Flags.ContainsKey("where")) throw new MutationRejectedException("INVALID_USAGE", 2);
        try { BulkSelection.Data(r, fields); }
        catch (JsonException) { throw new MutationRejectedException("INVALID_USAGE", 2); }
    }
    private string[] SystemReasons(string resource, Command c)
    {
        var reasons = new List<string>();
        if (c.ReadOnly) return [];
        if (resource is "mode" or "plugin" or "steam" or "cloudredirect" && !steam.IsValid) reasons.Add("STEAM_NOT_FOUND");
        if (resource is "mode" or "plugin" && SelectedSteamRunning() == true) reasons.Add("STEAM_RUNNING");
        if (resource == "plugin" && c.Verb == "update" && !settings.IsExecutableWorkflowEnabled("LuaToolsPlugin")) reasons.Add("WORKFLOW_DISABLED");
        if (resource == "cloudredirect")
        {
            if (unlocker.SelectedMode is not (Models.UnlockerMode.Ost or Models.UnlockerMode.Bst)) reasons.Add("MODE_REQUIRED");
            if (c.Verb == "manage" && !unlocker.IsCloudRedirectAddonEnabled) reasons.Add("ADDON_DISABLED");
            if (!settings.IsExecutableWorkflowEnabled("CloudRedirect")) reasons.Add("WORKFLOW_DISABLED");
        }
        if (resource == "tool" && !settings.IsExecutableWorkflowEnabled("SteamAutoCrack")) reasons.Add("WORKFLOW_DISABLED");
        if (resource is "application" or "update" && (c.Verb is "exit" or "restart" or "apply"))
        {
            if (queue.ActiveCount + Jobs.ActiveOperations > 0 || Cabinet.CabinetService.HasActiveHelpers) reasons.Add("JOBS_ACTIVE");
            if (c.Verb == "apply" && !updates.HasStagedUpdate) reasons.Add("NO_STAGED_UPDATE");
        }
        return reasons.ToArray();
    }
}
