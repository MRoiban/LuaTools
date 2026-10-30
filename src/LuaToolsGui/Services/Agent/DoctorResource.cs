using System.IO;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Models;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private sealed record DoctorFinding(string Severity, string Code, string Message, string? FixCommand, object? Details, bool Safe = false);
    [AgentResource("doctor")]
    private async Task<Envelope> DoctorRequestAsync(AgentRequest r, CancellationToken ct)
    {
        var report = await DoctorAsync(ct, r.ClientVersion);
        if (!r.Flags.ContainsKey("fix")) return report;
        if (r.Flags.ContainsKey("read-only")) return Envelope.Fail("READ_ONLY", 5, Resources.Strings.Agent_Doctor_Consent);
        var findings = report.Data!.AsArray();
        var repairs = new List<object>();
        if (!r.Flags.ContainsKey("dry-run") && !r.Flags.ContainsKey("yes"))
            return Envelope.Fail("CONSENT_REQUIRED", 8, Resources.Strings.Agent_Doctor_Consent) with { Error = new("CONSENT_REQUIRED", 8, Resources.Strings.Agent_Doctor_Consent, Details: report.Data) };
        bool failed = false;
        foreach (var finding in findings.Where(f => f?["fix_command"] is not null))
        {
            string command = finding!["fix_command"]!.GetValue<string>();
            bool safe = finding["safe"]?.GetValue<bool>() == true;
            Envelope? result = null;
            if (safe)
            {
                // Parse the advertised command and invoke the normal host boundary, including its
                // mutation coordinator, live context checks, planning and durable receipt paths.
                var parsed = CommandParser.Parse(command.Split(' ').Skip(1).ToArray());
                var flags = new Dictionary<string, string?>(parsed.Flags) { ["yes"] = null };
                if (r.Flags.ContainsKey("dry-run")) flags["dry-run"] = null;
                if (r.Flags.GetValueOrDefault("actor-note") is { } note) flags["actor-note"] = note;
                try
                {
                    result = await ExecuteAsync(r with { Command = parsed.Command!.Key, Arguments = parsed.Arguments, Flags = flags,
                        RequestId = Guid.NewGuid().ToString("N"), ExpectedSteam = SteamIdentity }, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result = Envelope.Fail("JOURNAL_UNAVAILABLE", 7, Resources.Strings.Agent_Doctor_Partial);
                }
                failed |= !result.Ok;
            }
            repairs.Add(new { command, status = safe ? r.Flags.ContainsKey("dry-run") ? "planned" : result!.Ok ? "accepted" : "failed" : "skipped", reason = safe ? null : Resources.Strings.Agent_Doctor_Unsafe, result });
        }
        var data = new { findings, repairs };
        return failed ? Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Doctor_Partial) with { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_Doctor_Partial, Details: Protocol.Node(data)) } : Envelope.Success(data);
    }
    public async Task<Envelope> DoctorAsync(CancellationToken ct, string clientVersion = "unknown")
    {
        var findings = new List<DoctorFinding>();
        void Add(string severity, string code, string message, string? fix = null, object? details = null, bool safe = false) => findings.Add(new(severity, code, message, fix, details, safe));
        var appVersion = UpdateService.CurrentVersion;
        bool mismatch = Version.TryParse(clientVersion, out var cliVersion) && (cliVersion.Major != appVersion.Major || cliVersion.Minor != appVersion.Minor || cliVersion.Build != appVersion.Build);
        Add(mismatch ? "warn" : "info", mismatch ? "CLI_VERSION_MISMATCH" : "APP_VERSION", Resources.Strings.Agent_Doctor_Version, null, new { app = appVersion.ToString(), cli = clientVersion, protocol = Protocol.Version });
        Add(steam.IsValid ? "info" : "error", steam.IsValid ? "STEAM_LOCATION" : string.IsNullOrWhiteSpace(steam.EffectivePath) ? "STEAM_NOT_FOUND" : "STEAM_PATH_INVALID", Resources.Strings.Agent_Doctor_Steam,
            steam.IsValid ? null : "luatools steam update --auto --yes", new { path = steam.EffectivePath, valid = steam.IsValid, running = SelectedSteamRunning(), identity = SteamIdentity }, !steam.IsValid);
        var addon = await unlocker.GetCloudRedirectStateAsync(checkUpdate: false, ct: ct);
        Add("info", "ADDON_STATE", Resources.Strings.Agent_Doctor_Mode, null, addon);
        if (Enum.TryParse<UnlockerMode>(settings.SelectedMode, true, out var selected))
        {
            var mode = await unlocker.GetStateAsync(selected, ct: ct);
            Add(mode.Status == ModeStatus.NotInstalled ? "error" : "info", mode.Status == ModeStatus.NotInstalled ? "MODE_NOT_INSTALLED" : "MODE_STATE", Resources.Strings.Agent_Doctor_Mode,
                mode.Status == ModeStatus.NotInstalled ? $"luatools mode update {selected.ToString().ToLowerInvariant()} --yes --restart-steam" : null, mode);
        }
        else Add("warn", "MODE_NOT_SELECTED", Resources.Strings.Agent_Doctor_Mode, "luatools mode update ost --yes --restart-steam");
        var pluginState = await (plugins ?? PluginCoordinator.For(plugin)).ReadAsync(false, ct);
        bool missing = !pluginState.FrontendInstalled || !pluginState.DllInstalled;
        bool broken = missing || !pluginState.DllMatches;
        Add(broken || pluginState.UpdateAvailable ? "warn" : "info", missing ? "PLUGIN_MISSING" : broken || pluginState.UpdateAvailable ? "PLUGIN_OUTDATED" : "PLUGIN_STATE", Resources.Strings.Agent_Doctor_Plugin,
            broken || pluginState.UpdateAvailable ? "luatools plugin update --install --yes --restart-steam" : null, pluginState);
        string loginCode = "LOGIN_STATE";
        if (auth.IsSignedIn)
        {
            try { await auth.GetValidAccessTokenAsync().WaitAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (RefreshRejectedException) { loginCode = "LOGIN_EXPIRED"; }
            catch (Exception) { loginCode = "LOGIN_UNAVAILABLE"; }
        }
        Add(auth.IsSignedIn && loginCode == "LOGIN_STATE" ? "info" : "warn", loginCode, Resources.Strings.Agent_Doctor_Login,
            !auth.IsSignedIn || loginCode == "LOGIN_EXPIRED" ? "luatools account login --browser" : null, new { signed_in = auth.IsSignedIn, human_browser_required = true, public_sources_available = true });
        string? key = await Snapshot(() => settings.HubcapApiKey, ct);
        var probe = key is not null && hubcap is not null ? await hubcap.ProbeStatsAsync(key, ct) : null;
        Add(probe?.DisabledReason is not null || key is null ? "warn" : "info", probe?.DisabledReason ?? (key is null ? "HUBCAP_KEY_MISSING" : "HUBCAP_STATE"), Resources.Strings.Agent_Doctor_Hubcap,
            key is null ? "luatools hubcap-key update --secret-stdin --yes" : probe?.DisabledReason is "HUBCAP_KEY_REJECTED" or "HUBCAP_KEY_EXPIRED" or "HUBCAP_KEY_INVALID" ? "luatools hubcap-key delete --yes" : null, new { configured = key is not null, checked_now = probe is not null, stats = probe?.Stats });
        foreach (var row in Protocol.Node(ReadSettings(settings))!.AsArray().Where(n => n?["key"]?.GetValue<string>().EndsWith("Enabled", StringComparison.Ordinal) == true && n?["value"]?.ToString() == "false"))
        {
            string name = row!["key"]!.GetValue<string>();
            if (!name.StartsWith("Cabinet", StringComparison.Ordinal)) Add("info", "WORKFLOW_DISABLED", Resources.Strings.Agent_Doctor_Workflow, "luatools setting update " + name + " --value true --yes", new { key = name });
        }
        var downloads = await DownloadSnapshotAsync(ct);
        Add("info", "DOWNLOAD_STATE", Resources.Strings.Agent_Doctor_Downloads, null, downloads);
        var liveFailures = await Snapshot(() => queue.Items.Where(x => x.Status == LuaToolsGui.Services.Downloads.DownloadStatus.Failed).Take(20).Select(x => new { id = x.Id, appid = x.AppId, message = LogRedactor.Redact(x.Message ?? "") }).ToArray(), ct);
        foreach (var failure in liveFailures) Add("error", "DOWNLOAD_FAILED", Resources.Strings.Agent_Doctor_Failed, null, failure);
        var failures = await Snapshot(() => queue.History.Where(x => x.Failed).Take(20).Select(x => new { id = x.Id, appid = x.AppId, message = LogRedactor.Redact(x.Message ?? "") }).ToArray(), ct);
        foreach (var failure in failures) Add("error", "DOWNLOAD_FAILED", Resources.Strings.Agent_Doctor_Failed, null, failure);
        var recent = await Task.Run(() => AppEventLog.Shared.ReadAgentEvents(new() { ["severity"] = "warn", ["since"] = DateTimeOffset.UtcNow.AddHours(-24).ToString("O") }), ct);
        Add(recent.Count > 0 ? "warn" : "info", "RECENT_ACTIVITY", Resources.Strings.Agent_Doctor_Activity, null, recent.Take(20).Select(n => n?.DeepClone()).ToArray());
        foreach (var group in recent.GroupBy(n => n?["message"]?.ToString()).Where(g => g.Count() > 1).Take(10))
            Add("warn", "REPEATED_ERROR", Resources.Strings.Agent_Doctor_Repeated, null, new { message = group.Key, count = group.Count() });
        Add(updates.HasStagedUpdate ? "warn" : "info", updates.HasStagedUpdate ? "UPDATE_AVAILABLE" : "UPDATE_STATE", Resources.Strings.Agent_Doctor_Update, updates.HasStagedUpdate ? "luatools update apply --yes" : null, new { staged_version = updates.StagedVersion?.ToString(), checked_now = false });
        foreach (string root in new[] { Path.GetPathRoot(DataPaths.Local), Path.GetPathRoot(steam.EffectivePath ?? "") }.OfType<string>().Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { var disk = new DriveInfo(root); long free = disk.AvailableFreeSpace; Add(free < 512L * 1024 * 1024 ? "error" : "info", free < 512L * 1024 * 1024 ? "DISK_SPACE_LOW" : "DISK_SPACE", Resources.Strings.Agent_Doctor_Disk, null, new { root, available_bytes = free, warning_threshold_bytes = 512L * 1024 * 1024 }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Add("warn", "DISK_SPACE_UNKNOWN", Resources.Strings.Agent_Doctor_Disk); }
        }
        return Envelope.Success(findings);
    }
}
