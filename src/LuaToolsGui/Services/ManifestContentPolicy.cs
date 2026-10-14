using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>Downloaded manifests are a bounded data language, never general-purpose Lua.</summary>
public static class ManifestContentPolicy
{
    public const int MaxLuaBytes = 2 * 1024 * 1024;
    public const int MaxManifestBytes = 64 * 1024 * 1024;
    public const int MaxEntries = 4096;
    public const int MaxDocumentationBytes = 256 * 1024;
    public const long MaxArchiveBytes = 256L * 1024 * 1024;
    private static readonly Regex Declaration = new(
        "^(?:addappid[ \\t]*\\([ \\t]*[1-9][0-9]{0,9}[ \\t]*(?:,[ \\t]*[01][ \\t]*(?:,[ \\t]*(?:\"[0-9a-fA-F]{1,64}\"|'[0-9a-fA-F]{1,64}')[ \\t]*)?)?\\)|(?:setManifestid|addtoken)[ \\t]*\\([ \\t]*[1-9][0-9]{0,9}[ \\t]*,[ \\t]*(?:\"[0-9]{1,20}\"|'[0-9]{1,20}')[ \\t]*(?:,[ \\t]*[0-9]{1,20}[ \\t]*)?\\))[ \\t]*;?[ \\t]*(?:--[^\\r\\n]*)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex ManifestName = new(@"^[1-9][0-9]{0,9}_[0-9]{1,20}\.manifest$", RegexOptions.CultureInvariant);

    private static bool IsDeclaration(string line)
    {
        if (!Declaration.IsMatch(line)) return false;
        if (line.StartsWith("addtoken", StringComparison.OrdinalIgnoreCase))
        {
            int closing = line.IndexOf(')');
            if (line[..closing].Count(c => c == ',') != 1) return false;
        }
        return true;
    }

    public static byte[] ReadBounded(Stream stream, int maximum)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Manifest content exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public static void ValidateLuaFile(string path, long? expectedAppId = null)
    {
        EnsureNoReparsePoints(path);
        using var stream = File.OpenRead(path);
        ValidateLua(ReadBounded(stream, MaxLuaBytes), expectedAppId);
    }

    public static void ValidateLua(byte[] data, long? expectedAppId = null)
    {
        if (data.Length > MaxLuaBytes) throw new InvalidDataException("Lua data exceeds its size limit.");
        string text = new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        bool declaration = false, requestedAppDeclared = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            // Long comments can terminate on later lines and expose text that appears commented.
            if (line.Contains("[[", StringComparison.Ordinal) || Regex.IsMatch(line, @"\[=+\["))
                throw new InvalidDataException("Lua block strings and comments are not allowed.");
            if (line.Length == 0) continue;
            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                string disabled = line[2..].TrimStart();
                if ((disabled.StartsWith("addappid", StringComparison.OrdinalIgnoreCase) || disabled.StartsWith("setManifestid", StringComparison.OrdinalIgnoreCase) || disabled.StartsWith("addtoken", StringComparison.OrdinalIgnoreCase)) &&
                    !IsDeclaration(disabled))
                    throw new InvalidDataException("Invalid disabled manifest declaration.");
                if (IsDeclaration(disabled))
                {
                    declaration = true;
                    requestedAppDeclared |= DeclaresApp(disabled, expectedAppId);
                }
                continue;
            }
            if (!IsDeclaration(line)) throw new InvalidDataException("Only declarative addappid, addtoken and setManifestid data is allowed.");
            declaration = true;
            requestedAppDeclared |= DeclaresApp(line, expectedAppId);
        }
        if (expectedAppId is not null && !requestedAppDeclared)
            throw new InvalidDataException("Lua data does not declare the requested application.");
        if (!declaration) throw new InvalidDataException("No manifest declarations were found.");
    }

    private static bool DeclaresApp(string line, long? expectedAppId)
    {
        if (expectedAppId is null) return false;
        var match = Regex.Match(line, @"^addappid[ 	]*\([ 	]*([1-9][0-9]{0,9})[ 	]*[,)]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups[1].Value, out long id) && id == expectedAppId;
    }

    /// <summary>Accept the original raw format and its single-file ZIP transport wrapper.</summary>
    public static byte[] ReadManifestData(byte[] data, string? name = null)
    {
        if (data.Length > MaxManifestBytes) throw new InvalidDataException("Steam manifest exceeds its size limit.");
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4)
        {
            using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            ValidateArchive(archive, manifestPackage: false);
            var files = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToArray();
            if (files.Length != 1) throw new InvalidDataException("A Steam manifest wrapper must contain exactly one file.");
            using var stream = files[0].Open();
            data = ReadBounded(stream, MaxManifestBytes);
        }
        ValidateRawManifest(data, name);
        return data;
    }

    public static void ValidateManifest(byte[] data, string? name = null) => ReadManifestData(data, name);

    private static void ValidateRawManifest(byte[] data, string? name)
    {
        if (data.Length > MaxManifestBytes || data.Length < 20 || (name is not null && !ManifestName.IsMatch(name)))
            throw new InvalidDataException("Invalid Steam manifest data or filename.");
        int offset = 0;
        bool payload = false, metadata = false;
        while (offset + 4 <= data.Length)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            if (magic == 0x32C415AB)
            {
                if (!payload || !metadata || offset != data.Length) break;
                if (ManifestFile.ReadMetadata(data) is not { DepotId: > 0, GidManifest: > 0 } info)
                    break;
                if (name is not null)
                {
                    string[] ids = Path.GetFileNameWithoutExtension(name).Split('_');
                    if (info.DepotId != long.Parse(ids[0]) || info.GidManifest != ulong.Parse(ids[1])) break;
                }
                return;
            }
            if (offset + 4 > data.Length) break;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            if (length > data.Length - offset) break;
            if (magic == 0x71F617D0 && !payload && !metadata) payload = true;
            else if (magic == 0x1F4812BE && payload && !metadata) metadata = true;
            else if (magic != 0x1B81B817 || !metadata) break;
            offset += (int)length;
        }
        throw new InvalidDataException("Malformed Steam manifest sections.");
    }

    public static void ValidateManifestFile(string path)
    {
        EnsureNoReparsePoints(path);
        using var stream = File.OpenRead(path);
        ValidateManifest(ReadBounded(stream, MaxManifestBytes), Path.GetFileName(path));

    }

    public static void ValidateArchive(ZipArchive archive, bool manifestPackage = true, long? expectedAppId = null)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("Archive entry count exceeds its limit.");
        long total = 0;
        int luaCount = 0, fileCount = 0;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidateRelativePath(entry.FullName.TrimEnd('/'));
            if (!names.Add(entry.FullName.Replace('\\', '/'))) throw new InvalidDataException("Duplicate archive entry.");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive links and special files are forbidden.");
            if (entry.Length > MaxManifestBytes || entry.Length < 0 || entry.CompressedLength < 0 ||
                entry.Length > Math.Max(1024 * 1024, entry.CompressedLength * 200))
                throw new InvalidDataException("Archive entry exceeds expansion limits.");
            total = checked(total + entry.Length);
            if (total > MaxArchiveBytes) throw new InvalidDataException("Archive exceeds its expansion limit.");
            if (entry.FullName.EndsWith('/'))
            {
                if (entry.Length != 0) throw new InvalidDataException("Archive directory contains data.");
                continue;
            }
            if (!manifestPackage) continue;
            if (IsDocumentation(entry.Name))
            {
                using var document = entry.Open();
                byte[] bytes = ReadBounded(document, MaxDocumentationBytes);
                string text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                if (text.StartsWith("MZ", StringComparison.Ordinal) || text.Any(c => c < 32 && c is not ('\r' or '\n' or '\t')))
                    throw new InvalidDataException("Archive documentation must be plain UTF-8 text.");
                continue; // Metadata was ignored by the original installer and is never installed.
            }
            fileCount++;
            if (!targets.Add(entry.Name)) throw new InvalidDataException("Duplicate manifest install target.");
            using var stream = entry.Open();
            if (entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                if (++luaCount > 1) throw new InvalidDataException("Manifest packages may contain only one Lua data file.");
                ValidateLua(ReadBounded(stream, MaxLuaBytes), expectedAppId);
            }
            else if (entry.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                ValidateManifest(ReadBounded(stream, MaxManifestBytes), entry.Name);
            else throw new InvalidDataException("Manifest packages may contain only Lua data, Steam manifests and plain text documentation.");
        }
        if (manifestPackage && fileCount == 0) throw new InvalidDataException("Manifest package contains no data files.");
    }

    public static bool IsDocumentation(string name) =>
        name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    public static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 240 || relative[0] is '/' or '\\' || relative.Contains(':'))
            throw new InvalidDataException("Unsafe archive path.");
        foreach (string part in relative.Replace('\\', '/').Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>\"|?*".Contains(c)) ||
                Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsafe archive path component.");
        }
    }

    public static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are forbidden in install paths.");
    }
}
