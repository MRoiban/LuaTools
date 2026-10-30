using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private static string SettingVersion(string key, object? value) => MutationCoordinator.Hash(key + ":" + JsonSerializer.Serialize(value, Protocol.Json));
    [AgentResource("setting")]
    private Task<Envelope> SettingAsync(AgentRequest r, CancellationToken ct) => r.Command switch
    {
        "setting list" => Snapshot(() => List(r, Protocol.Node(ReadSettings(settings))!.AsArray()), ct),
        "setting get" => Snapshot(() => GetSetting(r.Arguments[0]), ct),
        _ => _mutations.ExecuteAsync(r, token => Snapshot(() => PrepareSetting(r), token), ct),
    };
    private PreparedMutation PrepareSetting(AgentRequest r)
    {
        JsonObject input;
        try { input = BulkSelection.Data(r, "key", "value"); }
        catch (JsonException) { throw new MutationRejectedException("INVALID_DATA", 2); }
        string? value = r.Flags.GetValueOrDefault("value") ?? (input.ContainsKey("value") && input["value"] is null ? "system" : input["value"]?.ToString());
        string[] targets;
        try { targets = BulkSelection.Select(r, Protocol.Node(ReadSettings(settings))!.AsArray(), "key", ["key", "type", "read_only"], input["key"]?.ToString()); }
        catch (ArgumentException) { throw new MutationRejectedException("INVALID_SELECTION", 2); }
        if (value is null) throw new MutationRejectedException("INVALID_VALUE", 2);
        var rows = Protocol.Node(ReadSettings(settings))!.AsArray();
        var current = targets.Select(key => rows.FirstOrDefault(n => n?["key"]?.ToString() == key)).ToArray();
        string version = targets.Length == 1 ? current[0]?["version"]?.ToString() ?? "missing" : MutationCoordinator.Hash(string.Join(':', current.Select(n => n?["version"]?.ToString() ?? "missing")));
        var changes = targets.Select(key => (Key: key, Definition: SettingSchema.All.FirstOrDefault(d => d.Key == key))).ToArray();
        bool enabling = bool.TryParse(value, out bool enabled) && enabled && targets.Any(ExecutableConsentCoordinator.Keys.Contains);
        if (changes.Length == 1)
        {
            if (changes[0].Definition is not { } definition) throw new MutationRejectedException("NOT_FOUND", 3);
            if (definition.ReadOnly) throw new MutationRejectedException("READ_ONLY_SETTING", 5);
            if (!ValidSettingValue(definition, value)) throw new MutationRejectedException("INVALID_VALUE", 2);
        }
        if (targets.Any(k => k.StartsWith("Cabinet", StringComparison.Ordinal)) && cabinet is null) throw new MutationRejectedException("SERVICE_UNAVAILABLE", 7);
        if (targets.Any(k => k.StartsWith("Cabinet", StringComparison.Ordinal) && k != "CabinetEnabled") && !settings.CabinetEnabled) throw new MutationRejectedException("CABINET_DISABLED", 5);
        bool sharing = enabled && targets.Any(k => k.StartsWith("Cabinet", StringComparison.Ordinal));
        string context = PlanContext;
        var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, targets,
            changes.Select(change => change.Key + (change.Definition is { } definition && ValidSettingValue(definition, value) ? " = " + value : " (invalid target or value)")).Concat(sharing ? [Resources.Strings.Agent_CabinetImpact] : Array.Empty<string>()).ToArray(), enabling || sharing);
        return new(plan, token => Snapshot(() =>
        {
            // Dispatcher turn owns the settings transaction. GUI writes cannot interleave validation/commit.
            var fresh = PrepareSettingVersion(targets);
            if (PlanContext != context || fresh != version) throw new MutationRejectedException("PLAN_STALE", 6);
            var results = new List<object>(); bool failed = false;
            foreach (var (key, definition) in changes)
            {
                string? error = definition is null ? "NOT_FOUND" : definition.ReadOnly ? "READ_ONLY_SETTING" : !ValidSettingValue(definition, value) ? "INVALID_VALUE" : null;
                if (error is not null) { failed = true; results.Add(new { id = key, ok = false, code = error }); continue; }
                SetSetting(key, value, r.Flags.GetValueOrDefault("actor-note"));
                results.Add(new { id = key, ok = true });
            }
            if (failed) return Envelope.Fail("PARTIAL_FAILURE", 1, Resources.Strings.Agent_MutationRejected) with
            { Error = new("PARTIAL_FAILURE", 1, Resources.Strings.Agent_MutationRejected, Details: Protocol.Node(new { results })) };
            return enabling ? Envelope.Success(new { results, version = PrepareSettingVersion(targets) }, null,
                new Warning("EXECUTABLE_CONSENT_ENABLED", Resources.Strings.Agent_ConsentTitle, new { keys = targets.Where(ExecutableConsentCoordinator.Keys.Contains).ToArray(), actor = "agent" }))
                : Envelope.Success(new { results, version = PrepareSettingVersion(targets) });
        }, token));
    }
    private string PrepareSettingVersion(string[] targets)
    {
        var rows = Protocol.Node(ReadSettings(settings))!.AsArray();
        string[] versions = targets.Select(key => rows.FirstOrDefault(n => n?["key"]?.ToString() == key)?["version"]?.ToString() ?? "missing").ToArray();
        return targets.Length == 1 ? versions[0] : MutationCoordinator.Hash(string.Join(':', versions));
    }
    private static bool ValidSettingValue(SettingDefinition definition, string value) => definition.AllowedValues.Length > 0
        ? definition.AllowedValues.Any(v => string.Equals(v.ToString(), value, definition.Type == "bool" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        : definition.Type == "bool" && bool.TryParse(value, out _);
    private void SetSetting(string key, string value, string? note)
    {
        if (ExecutableConsentCoordinator.Keys.Contains(key)) { ExecutableConsentCoordinator.Set(settings, key, bool.Parse(value), "agent", note, toast); return; }
        switch (key)
        {
            case "Language": settings.Language = value == "system" ? null : value; break;
            case "DnsMode": settings.DnsMode = value; break;
            case "ManagePageSize": settings.ManagePageSize = int.Parse(value); break;
            case "FixesPageSize": settings.FixesPageSize = int.Parse(value); break;
            case "BuildsPageSize": settings.BuildsPageSize = int.Parse(value); break;
            case "AutoUpdateApps": settings.AutoUpdateApps = bool.Parse(value); break;
            case "StartWithWindows": settings.StartWithWindows = bool.Parse(value); break;
            case "MinimizeToTray": settings.MinimizeToTray = bool.Parse(value); break;
            case "FastFetch": settings.FastFetch = bool.Parse(value); break;
            case "CabinetEnabled": settings.CabinetEnabled = bool.Parse(value); cabinet!.SettingsChanged(); break;
            case "CabinetLivePlaytime": settings.CabinetLivePlaytime = bool.Parse(value); cabinet!.SettingsChanged(); break;
            case "CabinetFetchSchemas": settings.CabinetFetchSchemas = bool.Parse(value); cabinet!.SettingsChanged(); break;
            case "CabinetWorkshopLinks": settings.CabinetWorkshopLinks = bool.Parse(value); cabinet!.SettingsChanged(); break;
            default: throw new MutationRejectedException("REFACTOR_REQUIRED", 5);
        }
    }
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
        return SettingSchema.All.Select(d => (object)new { key = d.Key, value = Value(d.Key), type = d.Type, @default = d.Default, allowed_values = d.AllowedValues, read_only = d.ReadOnly, version = SettingVersion(d.Key, Value(d.Key)), available_actions = AvailableActions.For("setting", c => c.Verb == "update" && d.ReadOnly ? "READ_ONLY_SETTING" : null) }).ToArray();
    }
    private Envelope GetSetting(string key)
    {
        var rows = Protocol.Node(ReadSettings(settings))!.AsArray();
        var row = rows.FirstOrDefault(n => n?["key"]?.GetValue<string>() == key);
        return row is null ? Envelope.Fail("NOT_FOUND", 3, Resources.Strings.Agent_SettingNotFound) : Envelope.Success(row);
    }
}
