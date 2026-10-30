namespace LuaToolsGui.Services;

/// <summary>One selected-installation process action shared by the GUI and agent.</summary>
public class SteamCoordinator(SteamService steam)
{
    public virtual bool IsRunning() => steam.IsSelectedSteamRunning();
    public virtual bool Restart() { using var selected = steam.PinSelectedInstallation(); return steam.RestartSteam(); }
}
