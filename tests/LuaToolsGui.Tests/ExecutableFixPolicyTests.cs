using System.IO;
using System.IO.Compression;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class ExecutableFixPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LuaToolsFixConsent", Guid.NewGuid().ToString("N"));
    public ExecutableFixPolicyTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private string Package(string name, byte[]? data = null)
    {
        var path = Path.Combine(_directory, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(data ?? [0x4d, 0x5a, 0, 0]); // Inert fixture, never loaded or executed.
        return path;
    }

    [Fact]
    public void DisabledFixesRejectBeforeReadingAnyFile()
    {
        var settings = new SettingsService(new AppSettings());
        Assert.Throws<InvalidOperationException>(() => ExecutableFixPolicy.ReadPackage(settings, "missing.zip"));
    }

    [Fact]
    public void EnabledFixAcceptsImmutableExecutableArchiveWithoutClaimingReviewedTrust()
    {
        var settings = new SettingsService(new AppSettings { ExecutableGameFixesEnabled = true });
        var path = Package("bin/fix.dll");
        var snapshot = ExecutableFixPolicy.ReadPackage(settings, path);
        File.WriteAllText(path, "substituted later");
        using var zip = new ZipArchive(new MemoryStream(snapshot));
        Assert.Equal("bin/fix.dll", Assert.Single(zip.Entries).FullName);
        Assert.Throws<InvalidOperationException>(() => DependencyTrustPolicy.RequireConfigured("DenuvoFix"));
    }

    [Fact]
    public void DisableAfterEnablePreventsTheNextInstall()
    {
        var state = new AppSettings { ExecutableGameFixesEnabled = true };
        var settings = new SettingsService(state);
        var path = Package("fix.dll");
        Assert.NotEmpty(ExecutableFixPolicy.ReadPackage(settings, path));
        settings.ExecutableGameFixesEnabled = false;
        Assert.Throws<InvalidOperationException>(() => ExecutableFixPolicy.ReadPackage(settings, path));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("C:/escape.dll")]
    [InlineData("fix.dll:payload")]
    [InlineData("CON.dll")]
    public void EnabledFixStillRejectsUnsafeArchivePaths(string name)
    {
        var settings = new SettingsService(new AppSettings { ExecutableGameFixesEnabled = true });
        var path = Package(name);
        Assert.ThrowsAny<InvalidDataException>(() => ExecutableFixPolicy.ReadPackage(settings, path));
    }

    [Fact]
    public void OtherWorkflowConsentCannotAuthorizeGameFixes()
    {
        var settings = new SettingsService(new AppSettings { SteamPluginEnabled = true, SteamlessEnabled = true });
        Assert.Throws<InvalidOperationException>(() => ExecutableFixPolicy.ReadPackage(settings, Package("fix.dll")));
    }

    [Fact]
    public void EnabledFixRejectsOversizedCompressedInputBeforeParsing()
    {
        var settings = new SettingsService(new AppSettings { ExecutableGameFixesEnabled = true });
        var path = Path.Combine(_directory, "oversized.zip");
        using (var file = File.Create(path)) file.SetLength(ManifestContentPolicy.MaxArchiveBytes + 1);
        Assert.Throws<InvalidDataException>(() => ExecutableFixPolicy.ReadPackage(settings, path));
    }

    [Fact]
    public void FalseZipDirectoryLengthCannotBypassExpandedByteValidation()
    {
        var settings = new SettingsService(new AppSettings { ExecutableGameFixesEnabled = true });
        var path = Package("fix.dll", new byte[4096]);
        var bytes = File.ReadAllBytes(path);
        int central = -1;
        for (int i = 0; i < bytes.Length - 28; i++)
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x02014b50)
            { central = i; break; }
        Assert.True(central >= 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24, 4), 1);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => ExecutableFixPolicy.ReadPackage(settings, path));
    }
}
