using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services.Agent;

/// <summary>Explicit, read-only application boundary. No DI resolution or plugin dispatch is exposed.</summary>
public sealed class AgentCommandService(SettingsService settings, SteamService steam, AuthService auth,
    DownloadQueue queue, UnlockerService unlocker, PluginInstallerService plugin, UpdateService updates)
{
    private readonly SnapshotPager _pager = new();
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public string SteamIdentity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((steam.EffectivePath ?? "").ToUpperInvariant())));
    private static Task<T> Snapshot<T>(Func<T> read, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess() ? Task.FromResult(read()) : dispatcher.InvokeAsync(read, DispatcherPriority.Background, ct).Task;
    }
    public async Task<Envelope> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        if (request.Protocol != Protocol.Version) return Envelope.Fail("PROTOCOL_MISMATCH", 6, Resources.Strings.Agent_ContextChanged);
        if (request.InstanceId != InstanceId || request.Flags.GetValueOrDefault("expect-instance") is { } instance && instance != InstanceId || request.ExpectedSteam is { } selected && selected != SteamIdentity)
            return Envelope.Fail("CONTEXT_CHANGED", 6, Resources.Strings.Agent_ContextChanged);
        // Validate again at the trust boundary, including projections. Never trust client parsing.
        var args = request.Command.Split(' ').Concat(request.Arguments).ToList();
        foreach (var (flag, value) in request.Flags) { args.Add("--" + flag); if (value is not null) args.Add(value); }
        var parsed = CommandParser.Parse(args.ToArray());
        if (parsed.Problems.Length != 0) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments, problems: parsed.Problems);
        if (parsed.Command is null || parsed.Command.Key != request.Command) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments);
        if (request.Flags.ContainsKey("read-only") && !parsed.Command.ReadOnly) return Envelope.Fail("READ_ONLY", 5, Resources.Strings.Agent_ReadOnly);
        if (parsed.Command.Status != "enabled" || request.Flags.ContainsKey("fix")) return Envelope.Fail("NOT_IMPLEMENTED", 5, Resources.Strings.Agent_NotImplemented);
        Envelope result = request.Command switch
        {
            "ping" or "application start" => Ping(),
            "status" => await StatusAsync(ct),
            "whoami" => Whoami(),
            "doctor" => await DoctorAsync(ct, request.ClientVersion),
            "setting list" => List(request, Protocol.Node(ReadSettings(settings))!.AsArray()),
            "setting get" => GetSetting(request.Arguments[0]),
            "download list" or "queue list" => List(request, await DownloadSnapshotAsync(ct)),
            "activity-log list" => List(request, await Task.Run(() => AppEventLog.Shared.ReadAgentEvents(request.Flags), ct)),
            _ => Envelope.Fail("NOT_IMPLEMENTED", 5, Resources.Strings.Agent_NotImplemented),
        };
        return Results.Project(result, request.Flags.GetValueOrDefault("fields"));
    }
    public Envelope Ping() => Envelope.Success(new { protocol = Protocol.Version, instance_id = InstanceId, ready = true });
    private bool? SelectedSteamRunning()
    {
        if (!steam.IsValid) return null;
        try { return steam.IsSelectedSteamRunning(); }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
    public async Task<Envelope> StatusAsync(CancellationToken ct)
    {
        var counts = await Snapshot(() => new { active = queue.ActiveCount, total = queue.Items.Count, failed = queue.History.Count(i => i.Failed) }, ct);
        return Envelope.Success(new
        {
            protocol = Protocol.Version, instance_id = InstanceId, version = UpdateService.CurrentVersion.ToString(),
            steam = new { identity = SteamIdentity, valid = steam.IsValid, running = SelectedSteamRunning() }, queue = counts,
            helpers = Cabinet.CabinetService.HasActiveHelpers ? 1 : 0,
            capabilities = Catalogue.Commands.Select(c => new { command = c.Key, enabled = c.Status == "enabled" }), health = "ready", available_actions = Actions("application"),
        });
    }
    public Envelope Whoami() => Envelope.Success(new
    {
        state = auth.IsSignedIn ? "authenticated" : "guest", display_name = auth.DisplayName is null ? null : LogRedactor.Redact(auth.DisplayName),
        provider = auth.IsSignedIn ? "discord" : null, expiry_state = "unknown",
        owner = new { account_match = true, elevation_match = true, session_match = true, elevated = LocalDiscovery.IsElevated() }, available_actions = Actions("account"),
    });
    public static object[] Actions(string resource) => Catalogue.Commands.Where(c => c.Resource == resource).Select(c => (object)new
    {
        resource, verb = c.Verb, enabled = c.Status == "enabled", disabled_reasons = c.Status == "enabled" ? [] : new[] { new { code = "NOT_IMPLEMENTED", message = Resources.Strings.Agent_NotImplemented, gate = "implementation", source_capability_id = c.CapabilityIds.FirstOrDefault() } },
        destructive = c.Destructive, requires_confirmation = c.Destructive, required_flags = c.Destructive ? new[] { "--yes" } : [],
    }).ToArray();
    public static object[] ReadSettings(SettingsService s)
    {
        // Explicit getters prevent a new credential property ever becoming public by reflection.
        object? Value(string key) => key switch
        {
            "Language" => s.Language, "DnsMode" => s.DnsMode, "ManagePageSize" => s.ManagePageSize,
            "FixesPageSize" => s.FixesPageSize, "BuildsPageSize" => s.BuildsPageSize, "DonateKeys" => false,
            "AutoUpdateApps" => s.AutoUpdateApps, "StartWithWindows" => s.StartWithWindows, "MinimizeToTray" => s.MinimizeToTray,
            "FastFetch" => s.FastFetch, "CabinetEnabled" => s.CabinetEnabled, "CabinetLivePlaytime" => s.CabinetLivePlaytime,
            "CabinetFetchSchemas" => s.CabinetFetchSchemas, "CabinetWorkshopLinks" => s.CabinetWorkshopLinks,
            "ExecutableGameFixesEnabled" => s.ExecutableGameFixesEnabled, "BetterSteamToolsEnabled" => s.BetterSteamToolsEnabled,
            "OpenSteamToolsEnabled" => s.OpenSteamToolsEnabled, "SteamPluginEnabled" => s.SteamPluginEnabled,
            "DepotDownloaderEnabled" => s.DepotDownloaderEnabled, "SteamlessEnabled" => s.SteamlessEnabled,
            "SteamAutoCrackEnabled" => s.SteamAutoCrackEnabled, "CloudRedirectEnabled" => s.CloudRedirectEnabled,
            "SelectedMode" => s.SelectedMode, _ => null,
        };
        return SettingSchema.All.Select(d => (object)new { key = d.Key, value = Value(d.Key), type = d.Type, @default = d.Default, allowed_values = d.AllowedValues, read_only = true, available_actions = Actions("setting") }).ToArray();
    }
    private Envelope GetSetting(string key)
    {
        var rows = Protocol.Node(ReadSettings(settings))!.AsArray();
        var row = rows.FirstOrDefault(n => n?["key"]?.GetValue<string>() == key);
        return row is null ? Envelope.Fail("NOT_FOUND", 3, Resources.Strings.Agent_SettingNotFound) : Envelope.Success(row);
    }
    private Task<JsonArray> DownloadSnapshotAsync(CancellationToken ct) => Snapshot(() => Protocol.Node(queue.Items.Select(i => new
    {
        id = i.Id, appid = i.AppId, title = LogRedactor.Redact(i.Title), state = JsonNamingPolicy.SnakeCaseLower.ConvertName(i.Status.ToString()),
        bytes = i.BytesRead, total_bytes = i.TotalBytes, rate = i.BytesPerSecond, eta_seconds = i.Eta?.TotalSeconds,
        enqueued_at = i.EnqueuedAt.ToUniversalTime(), available_actions = Actions("download"),
    }).ToArray())!.AsArray(), ct);
    private Envelope List(AgentRequest request, JsonArray rows)
    {
        bool reverse = request.Flags.GetValueOrDefault("sort") == "reverse";
        if (reverse) rows = new JsonArray(rows.Reverse().Select(x => x?.DeepClone()).ToArray());
        string key = request.Command + ":" + string.Join(';', request.Flags.Where(p => p.Key is "severity" or "since" or "category" or "sort").OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
        return _pager.List(key, rows, request.Flags);
    }
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
    }
}
