using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using LuaToolsGui.Services;
using LuaToolsGui.Services.OnlineFixMe;
using Xunit;
using SourceEntry = LuaToolsGui.Services.OnlineFixMe.OnlineFixMeArchiveConverter.SourceEntry;

namespace LuaToolsGui.Tests;

public sealed class OnlineFixMeArchiveConverterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LuaToolsArchiveConversion", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Ini = Encoding.UTF8.GetBytes("[OnlineFix]\nFixture=inert synthetic data\n");
    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("Nested inert archive fixture.\nNever loaded or executed.\n");

    public OnlineFixMeArchiveConverterTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static SourceEntry FileEntry(string key, byte[]? bytes = null) =>
        new(key, false, false, () => new MemoryStream(bytes ?? Ini));

    private static SourceEntry DirectoryEntry(string key) =>
        new(key, true, false, () => throw new InvalidOperationException("Directory data must not be read."));

    private static void Reject(IEnumerable<SourceEntry> entries, long archiveBytes = 1024 * 1024)
    {
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => OnlineFixMeArchiveConverter.WriteValidatedZip(entries, archiveBytes, output));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("../x.dll")]
    [InlineData("a/../../x")]
    [InlineData("/abs")]
    [InlineData("C:/x")]
    [InlineData("a\\..\\x")]
    [InlineData(".luatools-fix/x.json")]
    [InlineData("sub/.LUATOOLS-FIX/x.json")]
    [InlineData("x:stream")]
    [InlineData("CON.dll")]
    [InlineData("sub/a\u0001")]
    public void UnsafePathRejectsWholeArchive(string key) => Reject([FileEntry("good.ini"), FileEntry(key)]);

    [Fact]
    public void OverlongPathIsRejected() => Reject([FileEntry(new string('a', 241))]);

    [Fact]
    public void DirectoryPathsAreValidatedBeforeSkipping() => Reject([DirectoryEntry("../")]);

    [Fact]
    public void CaseInsensitiveDuplicateFilesAreRejected() => Reject([FileEntry("bin/Fix.dll"), FileEntry("BIN/fix.dll")]);

    [Fact]
    public void BackslashNormalizationCannotBypassDuplicates() => Reject([FileEntry("bin\\fix.dll"), FileEntry("bin/fix.dll")]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileAndExplicitDirectoryCannotShareAPath(bool reverse)
    {
        SourceEntry[] entries = [FileEntry("bin"), DirectoryEntry("BIN/")];
        Reject(reverse ? entries.Reverse() : entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileAndImpliedDirectoryCannotShareAPath(bool reverse)
    {
        SourceEntry[] entries = [FileEntry("bin"), FileEntry("BIN/fix.dll")];
        Reject(reverse ? entries.Reverse() : entries);
    }

    [Fact]
    public void LinksAndSpecialFilesAreRejectedBeforeOpening() => Reject([
        FileEntry("good.ini"),
        new SourceEntry("linked.dll", false, true, () => throw new InvalidOperationException("Link must not be opened."))]);

    [Fact]
    public void SpecialDirectoriesAreRejected() => Reject([new SourceEntry("bin/", true, true, () => Stream.Null)]);

    [Fact]
    public void FileCountIsBounded() => Reject(Enumerable.Range(0, ManifestContentPolicy.MaxEntries + 1)
        .Select(i => FileEntry($"{i}.txt", [])));

    [Fact]
    public void SingleFileBoundUsesActualBytesAndStopsImmediately()
    {
        using var source = new GeneratedStream(ManifestContentPolicy.MaxManifestBytes + 100L);
        Reject([new SourceEntry("large.dll", false, false, () => source)]);
        Assert.Equal(ManifestContentPolicy.MaxManifestBytes + 1L, source.BytesRead);
    }

    [Theory]
    [InlineData(0, 1024 * 1024)]
    [InlineData(6000, 6000 * 200)]
    public void AggregateRatioUsesActualBytesAcrossEntries(long archiveBytes, long maximum)
    {
        long half = maximum / 2;
        using var first = new GeneratedStream(half);
        using var second = new GeneratedStream(maximum);
        Reject([new SourceEntry("one.txt", false, false, () => first),
            new SourceEntry("two.txt", false, false, () => second)], archiveBytes);
        Assert.Equal(maximum + 1, first.BytesRead + second.BytesRead);
    }

    [Fact]
    public void AggregateExpansionCannotExceedArchiveLimit()
    {
        var sources = Enumerable.Range(0, 5).Select(_ => new GeneratedStream(ManifestContentPolicy.MaxManifestBytes)).ToArray();
        Reject(sources.Select((source, i) => new SourceEntry($"{i}.txt", false, false, () => source)),
            ManifestContentPolicy.MaxArchiveBytes);
        Assert.Equal(ManifestContentPolicy.MaxArchiveBytes + 1, sources.Sum(source => source.BytesRead));
    }

    [Fact]
    public void EmptyArchiveIsRejected() => Reject([]);

    [Fact]
    public void OnlyDirectoriesIsRejected() => Reject([DirectoryEntry("bin/"), DirectoryEntry("bin/nested/")]);

    [Fact]
    public void RebuiltZipMustAlsoPassTheApplyExpansionPolicy()
    {
        using var source = new GeneratedStream(2 * 1024 * 1024);
        Reject([new SourceEntry("compressible.txt", false, false, () => source)]);
    }

    [Fact]
    public void NestedLayoutRoundTripsBytesAndPassesApplyPolicy()
    {
        byte[] fakeDll = Encoding.UTF8.GetBytes("inert synthetic DLL fixture; never executable");
        using var output = new MemoryStream();
        OnlineFixMeArchiveConverter.WriteValidatedZip([
            DirectoryEntry("Game_Data/"), FileEntry("OnlineFix.ini"),
            FileEntry("Game_Data\\Plugins\\x86_64\\steam_api64.dll", fakeDll)], 2048, output);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        ManifestContentPolicy.ValidateArchive(zip, manifestPackage: false);
        Assert.Equal(2, zip.Entries.Count);
        Assert.Equal(Ini, Read(zip.GetEntry("OnlineFix.ini")!));
        Assert.Equal(fakeDll, Read(zip.GetEntry("Game_Data/Plugins/x86_64/steam_api64.dll")!));
    }

    [Fact]
    public void CancellationDuringCopyStopsBeforeAnotherRead()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new GeneratedStream(1024 * 1024, cancellation.Cancel);
        using var output = new MemoryStream();
        Assert.ThrowsAny<OperationCanceledException>(() => OnlineFixMeArchiveConverter.WriteValidatedZip(
            [new SourceEntry("test.txt", false, false, () => source)], 2048, output, cancellation.Token));
        Assert.Equal(81920, source.BytesRead);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void CancellationBetweenEntriesStopsEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new MemoryStream();
        IEnumerable<SourceEntry> Entries()
        {
            yield return FileEntry("first.ini");
            cancellation.Cancel();
            yield return new SourceEntry("second.ini", false, false, () => throw new InvalidOperationException("Cancelled entry was opened."));
        }
        Assert.ThrowsAny<OperationCanceledException>(() => OnlineFixMeArchiveConverter.WriteValidatedZip(Entries(), 2048, output, cancellation.Token));
        Assert.Equal(0, output.Length);
    }

    // Generated with the repository's static 7zz from inert text and a subfolder:
    // a -tzip -mem=AES256 -ponline-fix.me; a -t7z -mhe=on -ms=on -ponline-fix.me.
    // 7zz cannot create RAR; RAR5/AES compatibility is covered by the parent's live check.
    [Theory]
    [InlineData("password-aes256.zip")]
    [InlineData("password-solid.7z")]
    public void PasswordProtectedArchivesRoundTripAndPassExecutableFixPolicy(string fixture)
    {
        // Intentionally discard the extension: detection must use archive content.
        string input = Path.Combine(_directory, "download.bin");
        File.Copy(Fixture(fixture), input);
        string output = Path.Combine(_directory, "normalized.zip");
        OnlineFixMeArchiveConverter.ConvertToZip(input, "online-fix.me", output);
        var settings = new SettingsService(new AppSettings { ExecutableGameFixesEnabled = true });
        byte[] snapshot = ExecutableFixPolicy.ReadPackage(settings, output);
        using var zip = new ZipArchive(new MemoryStream(snapshot));
        Assert.Equal(2, zip.Entries.Count);
        Assert.Equal(Ini, Read(zip.GetEntry("OnlineFix.ini")!));
        Assert.Equal(Notes, Read(zip.GetEntry("Game_Data/Plugins/notes.txt")!));
    }

    [Theory]
    [InlineData("password-aes256.zip", "wrong")]
    [InlineData("password-solid.7z", "wrong")]
    [InlineData("password-aes256.zip", null)]
    [InlineData("password-solid.7z", null)]
    public void WrongOrMissingPasswordLeavesNoOutput(string fixture, string? password)
    {
        string output = Path.Combine(_directory, "normalized.zip");
        var error = Assert.Throws<InvalidDataException>(() => OnlineFixMeArchiveConverter.ConvertToZip(Fixture(fixture), password, output));
        Assert.Equal("Wrong password or corrupt archive.", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TraversalFixtureRejectsWholeArchiveAndRemovesOutput()
    {
        string output = Path.Combine(_directory, "normalized.zip");
        Assert.Throws<InvalidDataException>(() => OnlineFixMeArchiveConverter.ConvertToZip(Fixture("traversal.zip"), null, output));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(_directory, "evil.txt")));
    }

    [Fact]
    public void ExistingDestinationIsNeverOverwrittenOrDeleted()
    {
        string output = Path.Combine(_directory, "normalized.zip");
        File.WriteAllBytes(output, Notes);
        Assert.Throws<InvalidDataException>(() => OnlineFixMeArchiveConverter.ConvertToZip(Fixture("password-aes256.zip"), "online-fix.me", output));
        Assert.Equal(Notes, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000))] // Unix symlink.
    [InlineData(0x21B60000)] // Unix character device.
    [InlineData(0x00000400)] // Windows reparse point.
    public void ArchiveAttributesCannotHideLinksOrSpecialFiles(int attributes)
    {
        string input = MakeZip("attributes.zip", "link.txt", attributes);
        string output = Path.Combine(_directory, "normalized.zip");
        Assert.Throws<InvalidDataException>(() => OnlineFixMeArchiveConverter.ConvertToZip(input, null, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ZipVolumeFlagsAreRejectedEvenIfThePayloadIsComplete()
    {
        string input = MakeZip("split.zip", "test.txt");
        byte[] bytes = File.ReadAllBytes(input);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 6), 1);
        File.WriteAllBytes(input, bytes);
        string output = Path.Combine(_directory, "normalized.zip");
        Assert.Throws<InvalidDataException>(() => OnlineFixMeArchiveConverter.ConvertToZip(input, null, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void CorruptStoredZipCannotBeLaunderedIntoANewValidChecksum()
    {
        string input = MakeZip("corrupt.zip", "test.txt");
        byte[] bytes = File.ReadAllBytes(input);
        int payload = 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26))
            + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28));
        bytes[payload] ^= 0x20;
        File.WriteAllBytes(input, bytes);
        string output = Path.Combine(_directory, "normalized.zip");
        Assert.Throws<InvalidDataException>(() => OnlineFixMeArchiveConverter.ConvertToZip(input, null, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PreCancelledConversionLeavesNoOutput()
    {
        string output = Path.Combine(_directory, "normalized.zip");
        Assert.ThrowsAny<OperationCanceledException>(() => OnlineFixMeArchiveConverter.ConvertToZip(
            Fixture("password-aes256.zip"), "online-fix.me", output, new CancellationToken(true)));
        Assert.False(File.Exists(output));
    }

    private string MakeZip(string name, string key, int attributes = 0)
    {
        string path = Path.Combine(_directory, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(key, CompressionLevel.NoCompression);
        entry.ExternalAttributes = attributes;
        using var stream = entry.Open();
        stream.Write(Notes);
        return path;
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "OnlineFixMe", name);

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var output = new MemoryStream();
        source.CopyTo(output);
        return output.ToArray();
    }

    private sealed class GeneratedStream(long length, Action? onRead = null) : Stream
    {
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Min(count, length - BytesRead);
            Array.Clear(buffer, offset, read);
            BytesRead += read;
            if (read > 0) onRead?.Invoke();
            return read;
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
