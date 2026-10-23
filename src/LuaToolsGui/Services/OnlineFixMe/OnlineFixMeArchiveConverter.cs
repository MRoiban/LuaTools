using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using LuaToolsGui.Services.Downloads;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.SevenZip;
using SharpCompress.Readers;

namespace LuaToolsGui.Services.OnlineFixMe;

internal static class OnlineFixMeArchiveConverter
{
    private const string CorruptArchive = "Wrong password or corrupt archive.";

    internal sealed record SourceEntry(string Key, bool IsDirectory, bool IsLinkOrSpecial, Func<Stream> Open);

    public static void ConvertToZip(string archivePath, string? password, string destinationZipPath, CancellationToken ct = default)
    {
        bool created = false;
        bool completed = false;
        try
        {
            ConvertArchive();
            completed = true;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is SharpCompressException or IOException or InvalidOperationException
            or ArgumentException or NotSupportedException or IndexOutOfRangeException or OverflowException
            or System.Security.Cryptography.CryptographicException)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidDataException(CorruptArchive, ex);
        }
        finally
        {
            // Only remove a file this call created; an existing destination belongs to its caller.
            if (created && !completed) File.Delete(destinationZipPath);
        }

        void ConvertArchive()
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(destinationZipPath) || Directory.Exists(destinationZipPath))
                throw new InvalidDataException("Destination ZIP already exists.");
            using var input = File.OpenRead(archivePath);
            if (input.Length > ManifestContentPolicy.MaxArchiveBytes)
                throw new InvalidDataException("Source archive exceeds its size limit.");
            // A stream prevents filename-based probing for adjacent archive volumes.
            using var archive = ArchiveFactory.OpenArchive(input, new ReaderOptions { Password = password, LeaveStreamOpen = true });
            if (archive.Type is not (ArchiveType.Rar or ArchiveType.Zip or ArchiveType.SevenZip))
                throw new InvalidDataException("Unsupported archive format.");
            if (archive.Type == ArchiveType.Zip) ValidateZipVolumes(input);
            foreach (var volume in archive.Volumes)
            {
                ct.ThrowIfCancellationRequested();
                // Only RarVolume overrides the library's default IsMultiVolume = true.
                if (volume.Index != 0 || volume is RarVolume { IsMultiVolume: true })
                    throw new InvalidDataException("Multi-volume archives are not supported.");
            }
            int fileCount = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.IsComplete || entry.IsSplitAfter)
                    throw new InvalidDataException("Multi-volume or incomplete archives are not supported.");
                if (!entry.IsDirectory && ++fileCount > ManifestContentPolicy.MaxEntries)
                    throw new InvalidDataException("Archive file count exceeds its limit.");
            }
            using var output = new FileStream(destinationZipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                WriteValidatedZip(ReadEntries(reader, password, ct), input.Length, output, ct);
            }
            else
            {
                WriteValidatedZip(ReadEntries(archive, password, ct), input.Length, output, ct);
            }
        }
    }

    internal static void WriteValidatedZip(IEnumerable<SourceEntry> entries, long sourceArchiveBytes, Stream output, CancellationToken ct = default)
    {
        if (sourceArchiveBytes < 0 || sourceArchiveBytes > ManifestContentPolicy.MaxArchiveBytes)
            throw new InvalidDataException("Source archive exceeds its size limit.");
        ct.ThrowIfCancellationRequested();
        long maximum = Math.Min(ManifestContentPolicy.MaxArchiveBytes, Math.Max(1024 * 1024, sourceArchiveBytes * 200));
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] buffer = new byte[81920];
        // Keep rejected data out of the caller's stream, including non-seekable streams.
        using var staged = new FileStream(Path.Combine(Path.GetTempPath(), "LuaTools-fix-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, buffer.Length, FileOptions.DeleteOnClose);
        long total = 0;
        using (var zip = new ZipArchive(staged, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                string key = entry.Key.Replace('\\', '/');
                if (entry.IsDirectory && key.EndsWith('/')) key = key[..^1];
                ManifestContentPolicy.ValidateRelativePath(key);
                if (key.Split('/').Any(part => part.Equals(ManifestJobFactory.FixRecordDir, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Archive contains a reserved fix-record path.");
                if (entry.IsLinkOrSpecial)
                    throw new InvalidDataException("Archive links and special files are forbidden.");
                for (int slash = key.IndexOf('/'); slash >= 0; slash = key.IndexOf('/', slash + 1))
                {
                    string parent = key[..slash];
                    if (files.Contains(parent)) throw new InvalidDataException("Archive file and directory paths collide.");
                    directories.Add(parent);
                }
                if (entry.IsDirectory)
                {
                    if (files.Contains(key)) throw new InvalidDataException("Archive file and directory paths collide.");
                    directories.Add(key);
                    continue;
                }
                if (directories.Contains(key)) throw new InvalidDataException("Archive file and directory paths collide.");
                if (!files.Add(key)) throw new InvalidDataException("Duplicate archive file path.");
                if (files.Count > ManifestContentPolicy.MaxEntries)
                    throw new InvalidDataException("Archive file count exceeds its limit.");
                using var source = entry.Open();
                using var target = zip.CreateEntry(key, CompressionLevel.Optimal).Open();
                long length = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // Read at most one byte beyond a bound, then stop without draining the entry.
                    int request = (int)Math.Min(buffer.Length, Math.Min(ManifestContentPolicy.MaxManifestBytes - length, maximum - total) + 1);
                    int read = source.Read(buffer, 0, request);
                    ct.ThrowIfCancellationRequested();
                    if (read == 0) break;
                    length += read;
                    total += read;
                    if (length > ManifestContentPolicy.MaxManifestBytes || total > maximum)
                        throw new InvalidDataException("Archive exceeds its expansion limits.");
                    target.Write(buffer, 0, read);
                }
            }
            if (files.Count == 0) throw new InvalidDataException("Archive contains no files.");
        }
        ct.ThrowIfCancellationRequested();
        if (staged.Length > ManifestContentPolicy.MaxArchiveBytes)
            throw new InvalidDataException("Converted ZIP exceeds its size limit.");
        staged.Position = 0;
        using (var zip = new ZipArchive(staged, ZipArchiveMode.Read, leaveOpen: true))
            // Optimal compression can make even inert data exceed the apply policy's per-entry
            // 200:1 ratio; rejecting that rebuilt ZIP is intentional (fail closed).
            ManifestContentPolicy.ValidateArchive(zip, manifestPackage: false);
        staged.Position = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = staged.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
    }

    private static IEnumerable<SourceEntry> ReadEntries(IReader reader, string? password, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!reader.MoveToNextEntry()) yield break;
            var entry = reader.Entry;
            yield return DescribeEntry(entry, password,
                () => new CheckedEntryStream(reader.OpenEntryStream(), entry, reader.Type, reader.Cancel));
        }
    }

    private static IEnumerable<SourceEntry> ReadEntries(IArchive archive, string? password, CancellationToken ct)
    {
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return DescribeEntry(entry, password,
                () => new CheckedEntryStream(entry.OpenEntryStream(), entry, archive.Type));
        }
    }

    private static SourceEntry DescribeEntry(IEntry entry, string? password, Func<Stream> open)
    {
        if (entry.IsSplitAfter) throw new InvalidDataException("Multi-volume archives are not supported.");
        if (entry.IsEncrypted && string.IsNullOrEmpty(password)) throw new InvalidDataException(CorruptArchive);
        int attributes = entry.Attrib ?? 0;
        bool special = !string.IsNullOrEmpty(entry.LinkTarget) || entry is RarEntry { IsRedir: true }
            || (attributes & (int)FileAttributes.ReparsePoint) != 0 || SpecialUnixType(attributes >> 16)
            || entry is RarEntry && SpecialUnixType(attributes)
            || entry is SevenZipEntry sevenZip && SpecialUnixType(sevenZip.ExtendedAttrib ?? 0);
        return new SourceEntry(entry.Key ?? "", entry.IsDirectory, special, open);
    }

    private static bool SpecialUnixType(int attributes) => (attributes & 0xF000) is not (0 or 0x8000 or 0x4000);

    private static void ValidateZipVolumes(Stream input)
    {
        // ZIP's disk flags are not exposed by IVolume. Inspect its bounded end record, including
        // a ZIP64 locator, before SharpCompress reads the directory. Never discover sibling files.
        byte[] tail = new byte[(int)Math.Min(input.Length, 22 + ushort.MaxValue)];
        input.Position = input.Length - tail.Length;
        input.ReadExactly(tail);
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            var end = tail.AsSpan(i);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50
                || BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != end.Length - 22) continue;
            if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != BinaryPrimitives.ReadUInt16LittleEndian(end[10..]))
                throw new InvalidDataException("Multi-volume archives are not supported.");
            long locatorOffset = input.Length - tail.Length + i - 20;
            if (locatorOffset >= 0)
            {
                Span<byte> locator = stackalloc byte[20];
                input.Position = locatorOffset;
                input.ReadExactly(locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == 0x07064b50)
                {
                    if (BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0
                        || BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1)
                        throw new InvalidDataException("Multi-volume archives are not supported.");
                    ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
                    if (offset > (ulong)locatorOffset || locatorOffset - (long)offset < 56)
                        throw new InvalidDataException(CorruptArchive);
                    Span<byte> zip64 = stackalloc byte[56];
                    input.Position = (long)offset;
                    input.ReadExactly(zip64);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50
                        || BinaryPrimitives.ReadUInt32LittleEndian(zip64[16..]) != 0
                        || BinaryPrimitives.ReadUInt32LittleEndian(zip64[20..]) != 0
                        || BinaryPrimitives.ReadUInt64LittleEndian(zip64[24..]) != BinaryPrimitives.ReadUInt64LittleEndian(zip64[32..]))
                        throw new InvalidDataException("Multi-volume or corrupt ZIP archive.");
                }
            }
            input.Position = 0;
            return;
        }
        throw new InvalidDataException(CorruptArchive);
    }

    private sealed class CheckedEntryStream(Stream source, IEntry entry, ArchiveType type, Action? cancel = null) : Stream
    {
        private long _length;
        private uint _crc = uint.MaxValue;
        private bool _completed;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            _length += read;
            _crc = ArchiveCrc32.Update(_crc, buffer.AsSpan(offset, read));
            if (read == 0)
            {
                _completed = true;
                // RAR validates its own CRC/BLAKE2, including encrypted checksums. ZIP AE-2
                // has no CRC (the AES stream validates its authentication tag instead).
                bool checkCrc = type == ArchiveType.SevenZip
                    || type == ArchiveType.Zip && (!entry.IsEncrypted || entry.Crc != 0);
                if (_length != entry.Size || checkCrc && ~_crc != (uint)entry.Crc)
                    throw new InvalidDataException(CorruptArchive);
            }
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // EntryStream normally drains unread data on disposal. Cancel first on failure
                // so a byte limit or cancellation cannot turn into unbounded decompression.
                if (!_completed) cancel?.Invoke();
                source.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
