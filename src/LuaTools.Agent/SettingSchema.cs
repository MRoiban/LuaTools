namespace LuaTools.Agent;

public sealed record SettingDefinition(string Key, string Type, object? Default, object[] AllowedValues, bool ReadOnly = true);
public static class SettingSchema
{
    public static readonly SettingDefinition[] All =
    [
        new("Language", "string?", null, ["system", "en", "fr"]),
        new("DnsMode", "string", "Never", ["Auto", "Always", "Never"]),
        new("ManagePageSize", "integer", 24, [12, 24, 48, 0]),
        new("FixesPageSize", "integer", 24, [12, 24, 48, 0]),
        new("BuildsPageSize", "integer", 10, [10, 17, 25, 50, 0]),
        new("DonateKeys", "bool", false, [false]),
        new("AutoUpdateApps", "bool", true, [false, true]),
        new("StartWithWindows", "bool", false, [false, true]),
        new("MinimizeToTray", "bool", false, [false, true]),
        new("FastFetch", "bool", false, [false, true]),
        new("CabinetEnabled", "bool", false, [false, true]),
        new("CabinetLivePlaytime", "bool", true, [false, true]),
        new("CabinetFetchSchemas", "bool", true, [false, true]),
        new("CabinetWorkshopLinks", "bool", true, [false, true]),
        new("ExecutableGameFixesEnabled", "bool", false, [false, true]),
        new("BetterSteamToolsEnabled", "bool", false, [false, true]),
        new("OpenSteamToolsEnabled", "bool", false, [false, true]),
        new("SteamPluginEnabled", "bool", false, [false, true]),
        new("DepotDownloaderEnabled", "bool", false, [false, true]),
        new("SteamlessEnabled", "bool", false, [false, true]),
        new("SteamAutoCrackEnabled", "bool", false, [false, true]),
        new("CloudRedirectEnabled", "bool", false, [false, true]),
        new("SelectedMode", "string?", null, ["Ost", "Bst", "Custom"]),
    ];
}
