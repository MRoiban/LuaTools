using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
namespace LuaToolsGui.Services;

/// <summary>Fail closed unless the effective protocol command belongs to this ordinary Steam installation.</summary>
public sealed class SteamInstallHandoff(SteamService steam)
{
    internal static bool Owns(string selected, string? command, bool isolated, bool delegated) =>
        !isolated && !delegated && command is not null &&
        (command.Equals($"\"{selected}\" \"%1\"", StringComparison.OrdinalIgnoreCase) ||
         command.Equals($"\"{selected}\" -url \"%1\"", StringComparison.OrdinalIgnoreCase));
    public bool CanHandoff()
    {
        try
        {
            if (!steam.IsValid) return false;
            string exe = SteamService.SteamExePathFor(steam.EffectivePath!);
            var environment = SteamProcessControl.ResolveEnvironment(exe, Environment.GetEnvironmentVariable, File.Exists);
            bool isolated = new[] { SteamProcessControl.LauncherVariable, SteamProcessControl.SteamVariable, SteamProcessControl.StateVariable }.Any(k => environment(k) is not null);
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\steam\UserChoice");
            string progId = choice?.GetValue("ProgId") as string ?? "steam";
            // Only the fixed Steam scheme registration is supported, including no delegation/DDE override.
            if (!progId.Equals("steam", StringComparison.OrdinalIgnoreCase)) return false;
            using var protocol = Registry.ClassesRoot.OpenSubKey("steam");
            using var command = protocol?.OpenSubKey(@"shell\open\command");
            using var dde = protocol?.OpenSubKey(@"shell\open\ddeexec");
            return protocol?.GetValue("URL Protocol") is string && Owns(exe, command?.GetValue(null) as string, isolated,
                command?.GetValue("DelegateExecute") is not null || dde is not null);
        }
        catch { return false; }
    }
    public bool Install(long appId)
    {
        if (!CanHandoff()) return false;
        // Use the exact verified handler rather than resolving the protocol a second time.
        var start = new ProcessStartInfo(SteamService.SteamExePathFor(steam.EffectivePath!)) { UseShellExecute = false };
        start.ArgumentList.Add("-url"); start.ArgumentList.Add($"steam://install/{appId}");
        using var process = Process.Start(start);
        return process is not null;
    }
}
