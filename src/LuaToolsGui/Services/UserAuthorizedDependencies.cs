using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>User consent and cached bundle integrity, separate from independently reviewed trust.
/// A receipt records downloaded bytes authorized by a user action; it is not an upstream security audit.</summary>
public static class UserAuthorizedDependencies
{
    public const string ReceiptFileName = ".user-authorized-dependencies.json";
    private const long MaximumBytes = 512L * 1024 * 1024;
    private const int MaximumFiles = 4096;
    private sealed record Receipt(string Component, Dictionary<string, string> Files);

    public static void RequireEnabled(string component, Func<string, bool> enabled)
    {
        if (!enabled(component)) throw new InvalidOperationException($"{component} is disabled in Settings.");
    }

    private static void RequireAction(string component, Func<string, bool> enabled, bool explicitUserAction)
    {
        RequireEnabled(component, enabled);
        if (!explicitUserAction) throw new InvalidOperationException($"{component} requires an explicit action.");
    }

    public static void ValidateArchiveAndExtract(string component, string archivePath, string stagingDir,
        Func<string, bool> enabled, bool explicitUserAction = false)
    {
        RequireAction(component, enabled, explicitUserAction);
        ManifestContentPolicy.EnsureNoReparsePoints(archivePath);
        ManifestContentPolicy.EnsureNoReparsePoints(stagingDir);
        if (Directory.Exists(stagingDir) && Directory.EnumerateFileSystemEntries(stagingDir).Any())
            throw new InvalidDataException("Dependency staging directory must be empty.");
        using var input = File.OpenRead(archivePath); // Prevent concurrent writer substitution on Windows.
        if (input.Length > MaximumBytes) throw new InvalidDataException("Dependency archive exceeds its size limit.");
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumFiles)
            throw new InvalidDataException("Invalid dependency archive entry count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
            ManifestContentPolicy.ValidateRelativePath(relative);
            if (Path.GetFileName(relative).Equals(ReceiptFileName, StringComparison.OrdinalIgnoreCase)
                || !names.Add(relative)) throw new InvalidDataException("Conflicting dependency archive entry.");
            int type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Dependency archive links are forbidden.");
            if (entry.Length < 0 || entry.Length > MaximumBytes || entry.CompressedLength < 0 || entry.CompressedLength > MaximumBytes
                || entry.Length > Math.Max(1024 * 1024, entry.CompressedLength * 200))
                throw new InvalidDataException("Dependency archive expansion exceeds its limit.");
            total = checked(total + entry.Length);
            if (total > MaximumBytes) throw new InvalidDataException("Dependency archive expansion exceeds its limit.");
            if (entry.FullName.EndsWith('/') && entry.Length != 0) throw new InvalidDataException("Invalid archive directory.");
        }
        Directory.CreateDirectory(stagingDir);
        foreach (var entry in archive.Entries)
        {
            RequireEnabled(component, enabled);
            string target = ContainedPath(stagingDir, entry.FullName.Replace('\\', '/').TrimEnd('/'));
            ManifestContentPolicy.EnsureNoReparsePoints(target);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            byte[] buffer = new byte[81920];
            long copied = 0;
            uint crc = uint.MaxValue;
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                copied += read;
                if (copied > entry.Length || copied > MaximumBytes) throw new InvalidDataException("Invalid dependency archive size.");
                crc = ArchiveCrc32.Update(crc, buffer.AsSpan(0, read));
                destination.Write(buffer, 0, read);
            }
            if (~crc != entry.Crc32) throw new InvalidDataException("Dependency archive CRC does not match its content.");
            if (copied != entry.Length) throw new InvalidDataException("Incomplete dependency archive entry.");
        }
        RecordDirectory(component, stagingDir, enabled, explicitUserAction);
    }

    public static void RecordDirectory(string component, string directory, Func<string, bool> enabled,
        bool explicitUserAction = false) =>
        RecordFiles(component, Path.Combine(directory, ReceiptFileName), DirectoryFiles(directory), enabled, explicitUserAction);

    public static void RecordFiles(string component, string receiptPath, IReadOnlyDictionary<string, string> files,
        Func<string, bool> enabled, bool explicitUserAction = false)
    {
        RequireAction(component, enabled, explicitUserAction);
        if (files.Count is 0 or > MaximumFiles) throw new InvalidDataException("Invalid dependency file count.");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var (name, path) in files)
        {
            ManifestContentPolicy.ValidateRelativePath(name);
            ManifestContentPolicy.EnsureNoReparsePoints(path);
            using var input = File.OpenRead(path);
            total = checked(total + input.Length);
            if (total > MaximumBytes) throw new InvalidDataException("Dependency bundle exceeds its size limit.");
            if (!hashes.TryAdd(name.Replace('\\', '/'), Hash(input))) throw new InvalidDataException("Duplicate dependency file.");
        }
        ManifestContentPolicy.EnsureNoReparsePoints(receiptPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(receiptPath))!);
        RequireEnabled(component, enabled);
        File.WriteAllBytes(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new Receipt(component, hashes)));
    }

    public static void RequireDirectory(string component, string directory, Func<string, bool> enabled)
    {
        RequireEnabled(component, enabled);
        RequireFiles(component, Path.Combine(directory, ReceiptFileName), DirectoryFiles(directory), enabled);
    }

    public static void RequireFiles(string component, string receiptPath, IReadOnlyDictionary<string, string> files,
        Func<string, bool> enabled)
    {
        RequireEnabled(component, enabled);
        Receipt receipt = ReadReceipt(receiptPath);
        if (receipt.Component != component || receipt.Files.Count != files.Count)
            throw new InvalidDataException("Dependency bundle does not match its consent receipt.");
        foreach (var (name, path) in files)
        {
            if (!receipt.Files.TryGetValue(name.Replace('\\', '/'), out var expected))
                throw new InvalidDataException("Dependency bundle contains an unexpected file.");
            ManifestContentPolicy.EnsureNoReparsePoints(path);
            using var input = File.OpenRead(path);
            if (!Hash(input).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Dependency bundle changed after authorization.");
        }
        RequireEnabled(component, enabled);
    }

    public static void RequireFile(string component, string path, Func<string, bool> enabled) =>
        ReadVerifiedBytes(component, path, enabled);

    public static byte[] ReadVerifiedBytes(string component, string path, Func<string, bool> enabled)
    {
        RequireEnabled(component, enabled);
        string directory = FindReceiptDirectory(path);
        RequireDirectory(component, directory, enabled);
        string name = Path.GetRelativePath(directory, path).Replace('\\', '/');
        return ReadVerifiedBytes(component, Path.Combine(directory, ReceiptFileName), name, path, enabled);
    }

    public static byte[] ReadVerifiedBytes(string component, string receiptPath, string logicalName, string path,
        Func<string, bool> enabled)
    {
        RequireEnabled(component, enabled);
        ManifestContentPolicy.ValidateRelativePath(logicalName);
        Receipt receipt = ReadReceipt(receiptPath);
        if (receipt.Component != component) throw new InvalidDataException("Dependency component does not match its consent receipt.");
        string name = logicalName.Replace('\\', '/');
        if (!receipt.Files.TryGetValue(name, out var expected)) throw new InvalidDataException("Dependency file is not authorized.");
        ManifestContentPolicy.EnsureNoReparsePoints(path);
        using var input = File.OpenRead(path);
        byte[] bytes = ManifestContentPolicy.ReadBounded(input, checked((int)MaximumBytes));
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dependency file changed after authorization.");
        RequireEnabled(component, enabled);
        return bytes;
    }

    public static void InstallArchive(string component, string archivePath, string installDir, string entryRelativePath,
        Func<string, bool> enabled, bool explicitUserAction = false)
    {
        RequireAction(component, enabled, explicitUserAction);
        string staging = installDir + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            ValidateArchiveAndExtract(component, archivePath, staging, enabled, explicitUserAction);
            if (!File.Exists(ContainedPath(staging, entryRelativePath))) throw new InvalidDataException("Dependency archive lacks its entry point.");
            PromoteDirectory(component, staging, installDir, enabled, explicitUserAction);
        }
        finally { DeleteStaging(staging); }
    }

    public static void InstallFile(string component, string downloadedPath, string installDir, string entryRelativePath,
        Func<string, bool> enabled, bool explicitUserAction = false)
    {
        RequireAction(component, enabled, explicitUserAction);
        string staging = installDir + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            ManifestContentPolicy.EnsureNoReparsePoints(staging);
            string destination = ContainedPath(staging, entryRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ManifestContentPolicy.EnsureNoReparsePoints(downloadedPath);
            using (var input = File.OpenRead(downloadedPath))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            {
                if (input.Length > MaximumBytes) throw new InvalidDataException("Dependency file exceeds its size limit.");
                input.CopyTo(output);
            }
            RecordDirectory(component, staging, enabled, explicitUserAction);
            PromoteDirectory(component, staging, installDir, enabled, explicitUserAction);
        }
        finally { DeleteStaging(staging); }
    }

    public static void PromoteDirectory(string component, string staging, string installDir, Func<string, bool> enabled, bool action = false)
    {
        RequireAction(component, enabled, action);
        RequireDirectory(component, staging, enabled);
        ManifestContentPolicy.EnsureNoReparsePoints(installDir);
        string previous = installDir + ".previous-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(installDir)) { _ = DirectoryFiles(installDir); Directory.Move(installDir, previous); }
        try { RequireEnabled(component, enabled); Directory.Move(staging, installDir); }
        catch { if (Directory.Exists(previous)) Directory.Move(previous, installDir); throw; }
        DeleteStaging(previous);
    }

    private static Dictionary<string, string> DirectoryFiles(string directory)
    {
        ManifestContentPolicy.EnsureNoReparsePoints(directory);
        if (!Directory.Exists(directory)) throw new InvalidDataException("Dependency bundle is not installed.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string current)
        {
            ManifestContentPolicy.EnsureNoReparsePoints(current);
            foreach (string path in Directory.EnumerateFiles(current))
            {
                ManifestContentPolicy.EnsureNoReparsePoints(path);
                if (path == Path.Combine(directory, ReceiptFileName)) continue;
                if (files.Count >= MaximumFiles) throw new InvalidDataException("Dependency file count exceeds its limit.");
                files.Add(Path.GetRelativePath(directory, path).Replace('\\', '/'), path);
            }
            foreach (string child in Directory.EnumerateDirectories(current)) Scan(child);
        }
        Scan(directory);
        return files;
    }

    private static Receipt ReadReceipt(string path)
    {
        ManifestContentPolicy.EnsureNoReparsePoints(path);
        using var input = File.OpenRead(path);
        var receipt = JsonSerializer.Deserialize<Receipt>(ManifestContentPolicy.ReadBounded(input, 2 * 1024 * 1024));
        if (receipt is null || receipt.Files is null || receipt.Files.Count is 0 or > MaximumFiles)
            throw new InvalidDataException("Dependency consent receipt is invalid.");
        return receipt with { Files = new(receipt.Files, StringComparer.OrdinalIgnoreCase) };
    }

    private static string FindReceiptDirectory(string path)
    {
        for (string? directory = Path.GetDirectoryName(Path.GetFullPath(path)); directory is not null; directory = Path.GetDirectoryName(directory))
            if (File.Exists(Path.Combine(directory, ReceiptFileName))) return directory;
        throw new InvalidDataException("Dependency file has no consent receipt.");
    }

    private static string Hash(Stream input)
    {
        if (input.Length > MaximumBytes) throw new InvalidDataException("Dependency file exceeds its size limit.");
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string ContainedPath(string root, string relative)
    {
        ManifestContentPolicy.ValidateRelativePath(relative);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Dependency path escapes staging.");
        return path;
    }

    private static void DeleteStaging(string directory)
    {
        if (!Directory.Exists(directory)) return;
        _ = DirectoryFiles(directory); // Recursive deletion never follows dependency links.
        Directory.Delete(directory, recursive: true);
    }
}
