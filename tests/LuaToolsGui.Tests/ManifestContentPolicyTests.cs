using System.IO;
using System.IO.Compression;
using System.Text;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class ManifestContentPolicyTests
{
    [Fact]
    public void DeclarativeKeysAndPinsRemainData()
    {
        string lua = $"-- Title\naddappid(480)\naddappid (481, 1, \"{new string('a', 64)}\") -- Depot\nsetManifestid(481, \"123\", 0)\n-- setManifestid(481, \"124\")";
        ManifestContentPolicy.ValidateLua(Encoding.UTF8.GetBytes(lua));
    }

    [Theory]
    [InlineData("os.execute('calc')")]
    [InlineData("addappid(480); os.execute('calc')")]
    [InlineData("addappid(loadstring('evil')())")]
    [InlineData("addappid(480)\n--[[ comment\n]] os.execute('calc')")]
    [InlineData("-- ignored\ros.execute('calc')")]
    [InlineData("addappid(480)\n--addappid(481);os.execute('calc')")]
    [InlineData("MZ executable")]
    public void ExecutableLuaAndDisguisedBinaryFailClosed(string lua) =>
        Assert.ThrowsAny<Exception>(() => ManifestContentPolicy.ValidateLua(Encoding.UTF8.GetBytes(lua)));

    [Theory]
    [InlineData("../480.lua")]
    [InlineData("/480.lua")]
    [InlineData("C:/480.lua")]
    [InlineData("\\\\server\\480.lua")]
    [InlineData("480.lua:payload.exe")]
    [InlineData("folder/../480.lua")]
    [InlineData("folder./480.lua")]
    [InlineData("CON.lua")]
    [InlineData("folder\\..\\480.lua")]
    public void UnsafePathsAreRejected(string path) =>
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateRelativePath(path));

    [Fact]
    public void ArchiveRejectsExecutablePayloadEvenAlongsideValidLua()
    {
        using var archive = MakeArchive(("480.lua", "addappid(480)"), ("plugin.dll", "MZ"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateArchive(archive));
    }

    [Fact]
    public void ArchiveRejectsTraversalEvenThoughInstallWouldFlattenName()
    {
        using var archive = MakeArchive(("../480.lua", "addappid(480)"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateArchive(archive));
    }

    [Fact]
    public void ArchiveRejectsDuplicateLuaTargets()
    {
        using var archive = MakeArchive(("480.lua", "addappid(480)"), ("481.lua", "addappid(481)"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateArchive(archive));
    }

    [Fact]
    public void ArchiveRejectsUnixSymlink()
    {
        using var archive = MakeArchive(("480.lua", "addappid(480)"));
        // Entry attributes must be set while creating, so construct an update archive for this case.
        using var memory = new MemoryStream();
        using (var write = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var link = write.CreateEntry("480.lua");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var stream = new StreamWriter(link.Open());
            stream.Write("addappid(480)");
        }
        memory.Position = 0;
        using var read = new ZipArchive(memory, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateArchive(read));
    }

    [Fact]
    public void ArchiveRejectsExpansionBomb()
    {
        using var archive = MakeArchive(("480.lua", new string(' ', 2 * 1024 * 1024)));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateArchive(archive));
    }

    [Fact]
    public void BoundedReadRejectsActualBytesBeyondLimit()
    {
        using var stream = new MemoryStream(new byte[1025]);
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ReadBounded(stream, 1024));
    }

    [Fact]
    public void ValidManifestIdentityAndFramingAreRequired()
    {
        byte[] data = MakeManifest();
        ManifestContentPolicy.ValidateManifest(data, "480_123.manifest");
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateManifest(data, "481_123.manifest"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateManifest(data[..^1], "480_123.manifest"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateManifest(data.Concat(new byte[] { 1 }).ToArray(), "480_123.manifest"));
    }

    [Fact]
    public void ArchiveEntryCountIsBounded()
    {
        using var memory = new MemoryStream();
        using (var write = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i <= ManifestContentPolicy.MaxEntries; i++) write.CreateEntry($"{i}/");
        memory.Position = 0;
        using var read = new ZipArchive(memory, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateArchive(read));
    }

    [Fact]
    public void DocumentedAccessTokenAndDisabledDataAreAccepted()
    {
        ManifestContentPolicy.ValidateLua(Encoding.UTF8.GetBytes(
            "addappid(1361510)\naddtoken(1361510,\"2764735786934684318\")\nsetManifestid(1361511,\"5656605350306673283\",12345678)"));
        ManifestContentPolicy.ValidateLua(Encoding.UTF8.GetBytes("-- AddAppId(480)\n--setManifestid(481,\"123\")"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateLua(
            Encoding.UTF8.GetBytes("addtoken(480,\"123\",0)")));
    }

    [Fact]
    public void SingleFileWrappedManifestRetainsIdentity()
    {
        byte[] wrapped = WrapManifest("z", MakeManifest());
        Assert.Equal(MakeManifest(), ManifestContentPolicy.ReadManifestData(wrapped, "480_123.manifest"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateManifest(wrapped, "481_123.manifest"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateManifest(WrapManifest("../z", MakeManifest())));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateManifest(WrapManifest("z", wrapped)));
    }

    [Fact]
    public void PackageAcceptsWrappedManifestAlongsideDeclarativeLua()
    {
        using var memory = new MemoryStream();
        using (var write = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var stream = write.CreateEntry("480.lua").Open())
                stream.Write(Encoding.UTF8.GetBytes("addappid(480)"));
            using (var stream = write.CreateEntry("480_123.manifest").Open())
                stream.Write(WrapManifest("z", MakeManifest()));
        }
        memory.Position = 0;
        using var read = new ZipArchive(memory, ZipArchiveMode.Read);
        ManifestContentPolicy.ValidateArchive(read);
    }

    [Fact]
    public void CacheLookupRejectsTruncatedAndSubstitutedDataButAcceptsWrappedData()
    {
        string root = Path.Combine(Path.GetTempPath(), "manifest-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "480_123.manifest");
        try
        {
            File.WriteAllBytes(path, WrapManifest("z", MakeManifest()));
            Assert.True(ManifestFile.Matches(path, 480, "123"));
            Assert.False(ManifestFile.Matches(path, 481, "123"));
            File.WriteAllBytes(path, MakeManifest()[..^1]);
            Assert.False(ManifestFile.Matches(path, 480, "123"));
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("MZ disguised binary"));
            Assert.False(ManifestFile.Matches(path, 480, "123"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DocumentationIsAcceptedButDisguisedBinaryIsRejected()
    {
        using var valid = MakeArchive(("480.lua", "addappid(480)"), ("README.md", "# Data package\nNotes."));
        ManifestContentPolicy.ValidateArchive(valid);
        using var binary = MakeArchive(("480.lua", "addappid(480)"), ("README.txt", "MZ\0binary"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateArchive(binary));
        using var empty = MakeArchive(("README.txt", "Only documentation."));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateArchive(empty));
    }

    [Theory]
    [InlineData("addtoken(480,os.execute('calc'))")]
    [InlineData("addtoken(480,\"123\");os.execute('calc')")]
    [InlineData("addtoken(480,\"123\\\"..os.execute('calc')..\"\")")]
    [InlineData("addtoken(480,\"123\") --[[\n]] os.execute('calc')")]
    [InlineData("--addtoken(480,\"123\");os.execute('calc')")]
    public void TokenArgumentsCannotEscapeDataLanguage(string lua) =>
        Assert.ThrowsAny<Exception>(() => ManifestContentPolicy.ValidateLua(Encoding.UTF8.GetBytes(lua)));

    [Fact]
    public void RequestedApplicationMustBeDeclaredWithoutRequiringLineOrder()
    {
        byte[] data = Encoding.UTF8.GetBytes("addappid(481,0,\"aabb\")\n-- addappid(480)\nsetManifestid(481,\"123\")");
        ManifestContentPolicy.ValidateLua(data, 480);
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateLua(data, 482));
        using var archive = MakeArchive(("480.lua", "addappid(481)"));
        Assert.Throws<InvalidDataException>(() => ManifestContentPolicy.ValidateArchive(archive, expectedAppId: 480));
    }

    private static byte[] WrapManifest(string name, byte[] data)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(data);
        }
        return memory.ToArray();
    }

    private static byte[] MakeManifest()
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(0x71F617D0u); writer.Write(0u);
        byte[] metadata = [8, 0xE0, 3, 16, 123]; // depot 480, gid 123
        writer.Write(0x1F4812BEu); writer.Write((uint)metadata.Length); writer.Write(metadata);
        writer.Write(0x32C415ABu);
        return memory.ToArray();
    }

    private static ZipArchive MakeArchive(params (string Name, string Data)[] entries)
    {
        var memory = new MemoryStream();
        using (var write = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                var entry = write.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = new StreamWriter(entry.Open());
                stream.Write(data);
            }
        memory.Position = 0;
        return new ZipArchive(memory, ZipArchiveMode.Read);
    }
}
