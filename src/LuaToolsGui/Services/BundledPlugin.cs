using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>The plugin is part of this application, never a downloaded executable dependency.</summary>
public static class BundledPlugin
{
    public static string Version => typeof(BundledPlugin).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    public static string InstallationDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirectoryName, "plugin");
    public static string ReceiptPath => Path.Combine(InstallationDirectory, "installed.json");
    public static bool IsActivated => ReadReceipt(ReceiptPath)?.ActivationEnabled == true;
    /// <summary>The updater must retain this installed bootstrap executable until explicit reinstall.</summary>
    public static string? LauncherDirectory => ReadLauncherDirectory(ReceiptPath);

    public static string? ReadLauncherDirectory(string receiptPath)
    {
        try
        {
            var receipt = ReadReceipt(receiptPath);
            if (receipt?.ActivationEnabled != true || receipt.LauncherPath is not { } launcher) return null;
            _ = LoaderConfiguration(launcher); // Existing local executable, no arguments or reparse ancestors.
            return Path.GetDirectoryName(Path.GetFullPath(launcher));
        }
        catch { return null; }
    }

    public static byte[] ReadBytes(string name)
    {
        if (name is not ("steam-plugin.js" or "steam-plugin.css" or "winmm.dll"))
            throw new ArgumentException("Unknown bundled plugin resource.", nameof(name));
        using var stream = typeof(BundledPlugin).Assembly.GetManifestResourceStream("LuaToolsGui.Plugin." + name)
            ?? throw new InvalidOperationException($"This build is missing bundled plugin resource {name}.");
        return ManifestContentPolicy.ReadBounded(stream, 16 * 1024 * 1024);
    }

    public static string ReadText(string name) => Encoding.UTF8.GetString(ReadBytes(name));
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string LoaderSha256 => Hash(ReadBytes("winmm.dll"));
    public static string FrontendSha256 => Hash(ReadBytes("steam-plugin.js").Concat(ReadBytes("steam-plugin.css")).ToArray());

    public static PluginInstallationReceipt? ReadReceipt(string path)
    {
        try
        {
            EnsureSafePath(path);
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<PluginInstallationReceipt>(ManifestContentPolicy.ReadBounded(stream, 1024 * 1024), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    // Check attributes directly: File.Exists/Directory.Exists can hide dangling junctions.
    public static void EnsureSafePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Reparse points are forbidden in plugin install paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static byte[] LoaderConfiguration(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Length < 3 || !char.IsAsciiLetter(executable[0])
            || executable[1] != ':' || executable[2] != '\\' || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            || executable.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
            throw new InvalidOperationException("Install from the fork's executable, not a dotnet host.");
        EnsureSafePath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("The fork executable is missing.", executable);
        return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Path.GetFullPath(executable))).ToArray();
    }

    /// <summary>Uninstall only removes bytes whose ownership was recorded by this installer.</summary>
    public static bool DeleteOwnedFile(string path, string? expectedHash)
    {
        EnsureSafePath(path);
        if (!File.Exists(path)) return true;
        if (expectedHash is null || !AssetHash.OfFile(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
        EnsureSafePath(path);
        File.Delete(path);
        return true;
    }
}

public sealed class PluginInstallationReceipt
{
    public bool ActivationEnabled { get; set; }
    public string? Tag { get; set; }
    public string? FrontendSha { get; set; }
    public string? SteamDirectory { get; set; }
    public string? LauncherPath { get; set; }
    public Dictionary<string, string>? DllShas { get; set; }
    public string? LoaderConfigurationSha { get; set; }
    public bool OwnsCdpMarker { get; set; }
    public Dictionary<string, List<string>>? DisabledMillenniumEntries { get; set; }
}
