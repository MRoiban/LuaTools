using System.IO;
using System.Runtime.InteropServices;
using LuaTools.Agent;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services.Agent;

public static class AgentCliInstallation
{
    public static string AddToPath(string? current, string directory)
    {
        string path = current ?? "";
        static string Normalize(string value) => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).TrimEnd('\\', '/');
        return path.Split(';').Any(p => string.Equals(Normalize(p), Normalize(directory), StringComparison.OrdinalIgnoreCase))
            ? path : path + (path.Length > 0 && !path.EndsWith(';') ? ";" : "") + directory;
    }
    public static void Install(ILogger log)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            LocalDiscovery.SecureDirectory(LocalDiscovery.Root);
            LocalDiscovery.Write(LocalDiscovery.Launcher, new LauncherRecord(Environment.ProcessPath!));
            string[] names = ["luatools.exe", "luatools.dll", "luatools.deps.json", "luatools.runtimeconfig.json"];
            foreach (string name in names)
            {
                string source = Path.Combine(AppContext.BaseDirectory, "agent-cli", name), target = Path.Combine(LocalDiscovery.Root, name);
                if (!File.Exists(source)) throw new FileNotFoundException();
                if (Path.GetFullPath(source).Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target)) LocalDiscovery.Verify(target);
                string temp = Path.Combine(LocalDiscovery.Root, Guid.NewGuid().ToString("N") + ".new");
                try { File.Copy(source, temp); LocalDiscovery.Verify(temp); File.Move(temp, target, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            using var environment = Registry.CurrentUser.CreateSubKey("Environment", true);
            string? previous = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string updated = AddToPath(previous, LocalDiscovery.Root);
            if (updated != previous)
            {
                var kind = previous is null ? RegistryValueKind.ExpandString : environment.GetValueKind("Path");
                environment.SetValue("Path", updated, kind);
                SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero, "Environment", 2, 2000, out _);
            }
        }
        catch (Exception ex) { log.LogWarning("Agent CLI installation unavailable ({Type})", ex.GetType().Name); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
