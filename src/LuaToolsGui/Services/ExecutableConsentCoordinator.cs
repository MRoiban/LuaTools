using LuaTools.Agent;
using Microsoft.Extensions.Logging;
namespace LuaToolsGui.Services;

/// <summary>Shared GUI/agent consent write. The SettingsService setters still publish live gate changes.</summary>
public static class ExecutableConsentCoordinator
{
    public static readonly string[] Keys = ["ExecutableGameFixesEnabled", "BetterSteamToolsEnabled", "OpenSteamToolsEnabled", "SteamPluginEnabled", "DepotDownloaderEnabled", "SteamlessEnabled", "SteamAutoCrackEnabled", "CloudRedirectEnabled"];
    public static void Set(SettingsService settings, string key, bool value, string actor, string? note = null, ToastService? toast = null)
    {
        switch (key)
        {
            case "ExecutableGameFixesEnabled": settings.ExecutableGameFixesEnabled = value; break;
            case "BetterSteamToolsEnabled": settings.BetterSteamToolsEnabled = value; break;
            case "OpenSteamToolsEnabled": settings.OpenSteamToolsEnabled = value; break;
            case "SteamPluginEnabled": settings.SteamPluginEnabled = value; break;
            case "DepotDownloaderEnabled": settings.DepotDownloaderEnabled = value; break;
            case "SteamlessEnabled": settings.SteamlessEnabled = value; break;
            case "SteamAutoCrackEnabled": settings.SteamAutoCrackEnabled = value; break;
            case "CloudRedirectEnabled": settings.CloudRedirectEnabled = value; break;
            default: throw new ArgumentException(nameof(key));
        }
        if (!value) return;
        string message = string.Format(Resources.Strings.Agent_ExecutableWarning, key, actor, LogRedactor.Redact(note ?? ""));
        AppEventLog.Shared.Write(LogLevel.Warning, "AgentConsent", message);
        toast?.Show(Resources.Strings.Agent_ConsentTitle, message, error: true);
    }
}
