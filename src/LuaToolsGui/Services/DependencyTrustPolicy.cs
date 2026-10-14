using System.IO;
using System.Security.Cryptography;

namespace LuaToolsGui.Services;

/// <summary>Independent, source-controlled approval of executable dependencies. Never populated from
/// a release API, downloaded manifest, local cache, or user-writable settings.</summary>
public static class DependencyTrustPolicy
{
    // Intentionally empty. Each addition requires separate source/binary review, recorded in
    // DEPENDENCY_TRUST.md. Key: component/relative-path, value: independently reviewed SHA-256.
    private static readonly IReadOnlyDictionary<string, string> ReviewedHashes =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static string BlockedMessage(string component) => $"{component} is unavailable in this build.";

    public static void RequireConfigured(string component)
    {
        if (!ReviewedHashes.Keys.Any(k => k.StartsWith(component + "/", StringComparison.Ordinal)))
            throw new InvalidOperationException(BlockedMessage(component));
    }

    public static void RequireInstallApproval(string component, bool reviewedInstallApproved)
    {
        RequireConfigured(component);
        if (!reviewedInstallApproved)
            throw new InvalidOperationException($"{component} installation has not been approved.");
    }

    public static bool IsTrustedFile(string component, string path, string? relativePath = null)
    {
        try { RequireFile(component, path, relativePath); return true; }
        catch { return false; }
    }

    public static byte[] ReadVerifiedBytes(string component, string path, string? relativePath = null)
    {
        RequireConfigured(component);
        string key = component + "/" + (relativePath ?? Path.GetFileName(path)).Replace('\\', '/');
        if (!ReviewedHashes.TryGetValue(key, out var expected))
            throw new InvalidOperationException(BlockedMessage(component));
        RequireNoLinks(component, path);
        var bytes = File.ReadAllBytes(path);
        VerifyHash(bytes, expected);
        return bytes;
    }

    public static void VerifyHash(byte[] bytes, string expectedSha256)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)
            || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dependency failed its independently reviewed SHA-256 verification.");
    }

    public static void RequireFile(string component, string path, string? relativePath = null) =>
        ReadVerifiedBytes(component, path, relativePath);

    /// <summary>Verify every file, including plugin DLLs, scripts, and loader configuration. Unknown
    /// files fail closed; an approved entry point alone cannot approve its side-loaded dependencies.</summary>
    public static void RequireDirectory(string component, string directory)
    {
        RequireConfigured(component);
        if (!Directory.Exists(directory)) throw new InvalidOperationException(BlockedMessage(component));
        RequireNoLinks(component, directory);
        // Require every reviewed bundle file to be present, including DLLs or loader configuration.
        foreach (string key in ReviewedHashes.Keys.Where(k => k.StartsWith(component + "/", StringComparison.Ordinal)))
        {
            string relative = key[(component.Length + 1)..];
            if (relative == "archive.zip") continue;
            string full = Path.GetFullPath(Path.Combine(directory, relative));
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(BlockedMessage(component));
            RequireFile(component, full, relative);
        }
        VerifyDirectory(component, directory, directory);
    }

    private static void RequireNoLinks(string component, string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"{component}: dependency links are not permitted.");
            current = Path.GetDirectoryName(current);
        }
    }

    private static void VerifyDirectory(string component, string root, string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"{component}: dependency directory links are not permitted.");
        foreach (var file in Directory.EnumerateFiles(directory))
            RequireFile(component, file, Path.GetRelativePath(root, file));
        foreach (var child in Directory.EnumerateDirectories(directory))
            VerifyDirectory(component, root, child);
    }
}
