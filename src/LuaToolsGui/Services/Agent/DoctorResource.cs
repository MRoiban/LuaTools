using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    [AgentResource("doctor")]
    private Task<Envelope> DoctorRequestAsync(AgentRequest r, CancellationToken ct) => DoctorAsync(ct, r.ClientVersion);
    public async Task<Envelope> DoctorAsync(CancellationToken ct, string clientVersion = "unknown")
    {
        var downloads = await DownloadSnapshotAsync(ct);
        int failed = await Snapshot(() => queue.History.Count(x => x.Failed), ct);
        var addon = await unlocker.GetCloudRedirectStateAsync(checkUpdate: false, ct: ct);
        var findings = new List<object>();
        void Add(string severity, string code, string message, string? fix, object? details = null) => findings.Add(new { severity, code, message, fix_command = fix, details, available_actions = Actions("doctor") });
        Add("info", "APP_VERSION", Resources.Strings.Agent_DoctorVersion, null, new { app = UpdateService.CurrentVersion.ToString(), cli = Version.TryParse(clientVersion, out var cliVersion) ? cliVersion.ToString() : "unknown", cli_protocol = Protocol.Version });
        Add(steam.IsValid ? "info" : "warn", "STEAM_LOCATION", Resources.Strings.Agent_DoctorSteam, steam.IsValid ? null : "luatools steam update --auto", new { path = steam.EffectivePath, valid = steam.IsValid, running = SelectedSteamRunning(), identity = SteamIdentity });
        Add(settings.SelectedMode is null ? "warn" : "info", "MODE_STATE", Resources.Strings.Agent_DoctorMode, settings.SelectedMode is null ? "luatools mode update ost --yes" : null, new { selected_mode = settings.SelectedMode, cloudredirect = addon, plugin_installed = plugin.IsInstalledLocally() });
        Add(auth.IsSignedIn ? "info" : "warn", "LOGIN_STATE", Resources.Strings.Agent_DoctorLogin, auth.IsSignedIn ? null : "luatools account login --browser", new { signed_in = auth.IsSignedIn, public_sources_available = true });
        bool key = !string.IsNullOrWhiteSpace(settings.HubcapApiKey);
        Add(key ? "info" : "warn", "HUBCAP_STATE", Resources.Strings.Agent_DoctorHubcap, key ? null : "luatools hubcap-key update --secret-stdin", new { configured = key });
        foreach (var row in Protocol.Node(ReadSettings(settings))!.AsArray().Where(n => n?["key"]?.GetValue<string>().EndsWith("Enabled", StringComparison.Ordinal) == true && n?["value"]?.ToString() == "false"))
        {
            string name = row!["key"]!.GetValue<string>();
            if (name.StartsWith("Cabinet", StringComparison.Ordinal)) continue;
            Add("info", "WORKFLOW_DISABLED", Resources.Strings.Agent_DoctorWorkflow, "luatools setting update " + name + " --value true --yes", new { key = name });
        }
        Add(failed > 0 ? "warn" : "info", "DOWNLOAD_STATE", Resources.Strings.Agent_DoctorDownloads, "luatools download list --json", new { active = downloads.Count(n => n?["state"]?.ToString() is not ("completed" or "failed" or "cancelled")), failed });
        var recent = await Task.Run(() => AppEventLog.Shared.ReadAgentEvents(new() { ["severity"] = "warn", ["since"] = DateTimeOffset.UtcNow.AddHours(-24).ToString("O") }), ct);
        Add(recent.Count > 0 ? "warn" : "info", "RECENT_ACTIVITY", Resources.Strings.Agent_DoctorActivity, "luatools activity-log list --severity warn --json", new { warnings_and_errors = recent.Count });
        Add(updates.HasStagedUpdate ? "warn" : "info", "UPDATE_STATE", Resources.Strings.Agent_DoctorUpdate, updates.HasStagedUpdate ? "luatools update apply --yes" : null, new { staged_version = updates.StagedVersion?.ToString(), checked_now = false });
        return Envelope.Success(findings);
    }}
