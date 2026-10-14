using System.IO;
using System.IO.Compression;

namespace LuaToolsGui.Services;

/// <summary>User consent permits executable fixes; it does not certify their publisher or code.</summary>
internal static class ExecutableFixPolicy
{
    internal static void RequireEnabled(SettingsService settings)
    {
        if (!settings.IsExecutableWorkflowEnabled("DenuvoFix"))
            throw new InvalidOperationException("Executable game fixes are disabled in Settings.");
    }

    internal static byte[] ReadPackage(SettingsService settings, string path)
    {
        RequireEnabled(settings);
        ManifestContentPolicy.EnsureNoReparsePoints(path);
        using var input = File.OpenRead(path);
        if (input.Length > ManifestContentPolicy.MaxArchiveBytes)
            throw new InvalidDataException("Fix archive exceeds its size limit.");
        var bytes = ManifestContentPolicy.ReadBounded(input, checked((int)ManifestContentPolicy.MaxArchiveBytes));
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        ManifestContentPolicy.ValidateArchive(archive, manifestPackage: false);
        // ZIP directory lengths are remote claims. Validate actual decompression before
        // any game file is backed up or overwritten using this immutable snapshot.
        var buffer = new byte[81920];
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            using var content = entry.Open();
            long length = 0;
            uint crc = uint.MaxValue;
            int read;
            while ((read = content.Read(buffer)) > 0)
            {
                length += read;
                crc = ArchiveCrc32.Update(crc, buffer.AsSpan(0, read));
                total += read;
                if (length > entry.Length || length > ManifestContentPolicy.MaxManifestBytes
                    || total > ManifestContentPolicy.MaxArchiveBytes)
                    throw new InvalidDataException("Fix archive has invalid expanded lengths.");
            }
            if (length != entry.Length || ~crc != entry.Crc32)
                throw new InvalidDataException("Fix archive is truncated or corrupt.");
        }
        RequireEnabled(settings);
        return bytes;
    }
}
