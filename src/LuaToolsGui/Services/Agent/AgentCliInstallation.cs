using System.IO;
using System.Security.Cryptography;
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
    // Leave unsupported data and overlong values untouched. Never cast a non-string
    // registry value into the same state as an absent value.
    public static bool RegisterPath(RegistryKey environment, string directory)
    {
        bool exists = environment.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase);
        var kind = exists ? environment.GetValueKind("Path") : RegistryValueKind.ExpandString;
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) throw new InvalidDataException("Unsupported user PATH registry type.");
        object? raw = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (exists && raw is not string) throw new InvalidDataException("Invalid user PATH value.");
        string? previous = raw as string;
        string updated = AddToPath(previous, directory);
        // Windows' environment variable limit includes its terminating null. Bound both
        // the stored and expanded text, preserving expandable entries verbatim.
        if (updated.Length > 32766 || Environment.ExpandEnvironmentVariables(updated).Length > 32766) throw new InvalidDataException("User PATH length limit exceeded.");
        if (updated == previous) return false;
        environment.SetValue("Path", updated, kind);
        return true;
    }
    public static void Install(ILogger log) => TryInstall(log, true);
    public static async Task InstallAsync(ILogger log)
    {
        // The old copied client may still be running while it auto-starts an updated GUI.
        // Retry locked files after that client exits; do not require another GUI restart.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (TryInstall(log, attempt == 49)) return;
            await Task.Delay(500).ConfigureAwait(false);
        }
    }
    private static bool TryInstall(ILogger log, bool report)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return true;
            LocalDiscovery.SecureDirectory(LocalDiscovery.Root);
            LocalDiscovery.Write(LocalDiscovery.Launcher, new LauncherRecord(Environment.ProcessPath!));
            string[] names = ["luatools.exe", "luatools.dll", "luatools.deps.json", "luatools.runtimeconfig.json"];
            foreach (string name in names)
            {
                string source = Path.Combine(AppContext.BaseDirectory, "agent-cli", name), target = Path.Combine(LocalDiscovery.Root, name);
                if (!File.Exists(source)) throw new FileNotFoundException();
                if (Path.GetFullPath(source).Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target))
                {
                    LocalDiscovery.Verify(target);
                    using var original = File.OpenRead(source); using var installed = File.OpenRead(target);
                    if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), SHA256.HashData(installed))) continue;
                }
                string temp = Path.Combine(LocalDiscovery.Root, Guid.NewGuid().ToString("N") + ".new");
                try { File.Copy(source, temp); LocalDiscovery.SecureNewFile(temp); LocalDiscovery.Verify(temp); File.Move(temp, target, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            using var environment = Registry.CurrentUser.CreateSubKey("Environment", true);
            if (RegisterPath(environment, LocalDiscovery.Root))
                SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero, "Environment", 2, 2000, out _);
            return true;
        }
        catch (Exception ex)
        {
            if (report) log.LogWarning("Agent CLI installation unavailable ({Type}): {Reason}", ex.GetType().Name, LogRedactor.Redact(ex.Message));
            return false;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
