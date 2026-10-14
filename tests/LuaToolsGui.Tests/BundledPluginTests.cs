using System.IO;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class BundledPluginTests
{
    [Fact]
    public void LoaderConfigurationContainsOnlyAnAbsoluteUtf16ExecutablePath()
    {
        using var fixture = new Fixture();
        string executable = Path.Combine(fixture.Directory, "Fork with spaces.exe");
        File.WriteAllBytes(executable, [0]);
        byte[] config = BundledPlugin.LoaderConfiguration(executable);
        Assert.Equal(Encoding.Unicode.GetPreamble(), config[..2]);
        Assert.Equal(executable, Encoding.Unicode.GetString(config[2..]));
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData("C:\\dotnet.exe")]
    [InlineData("C:\\fork.exe --silent")]
    [InlineData("C:\\fork.exe\n")]
    [InlineData("C:\\fork.dll")]
    [InlineData("\\\\server\\share\\fork.exe")]
    [InlineData("\\\\?\\C:\\fork.exe")]
    public void LoaderConfigurationRejectsHostsRelativePathsAndArguments(string executable) =>
        Assert.Throws<InvalidOperationException>(() => BundledPlugin.LoaderConfiguration(executable));

    [Fact]
    public void ReceiptRequiresExplicitActivationAndDoesNotActivateLegacyInstalls()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Directory, "installed.json");
        Assert.Null(BundledPlugin.ReadReceipt(path));
        File.WriteAllText(path, "{\"Tag\":\"upstream-release\",\"DllShas\":{\"winmm.dll\":\"old\"}}");
        Assert.False(BundledPlugin.ReadReceipt(path)!.ActivationEnabled);
        File.WriteAllText(path, JsonSerializer.Serialize(new PluginInstallationReceipt { ActivationEnabled = true, Tag = BundledPlugin.Version }));
        Assert.True(BundledPlugin.ReadReceipt(path)!.ActivationEnabled);
        File.WriteAllText(path, "{broken");
        Assert.Null(BundledPlugin.ReadReceipt(path));
    }

    [Fact]
    public void OnlyAnActiveReceiptWithAnExistingLocalExecutablePinsItsLauncherDirectory()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Directory, "installed.json");
        string launcher = Path.Combine(fixture.Directory, "fork.exe");
        File.WriteAllBytes(launcher, [0]);
        var receipt = new PluginInstallationReceipt { ActivationEnabled = true, LauncherPath = launcher };
        File.WriteAllText(path, JsonSerializer.Serialize(receipt));
        Assert.Equal(fixture.Directory, BundledPlugin.ReadLauncherDirectory(path));
        receipt.ActivationEnabled = false;
        File.WriteAllText(path, JsonSerializer.Serialize(receipt));
        Assert.Null(BundledPlugin.ReadLauncherDirectory(path));
        receipt.ActivationEnabled = true;
        foreach (string? invalid in new[] { null, "relative.exe", "\\\\server\\share\\fork.exe", launcher + " --minimized", Path.Combine(fixture.Directory, "missing.exe") })
        {
            receipt.LauncherPath = invalid;
            File.WriteAllText(path, JsonSerializer.Serialize(receipt));
            Assert.Null(BundledPlugin.ReadLauncherDirectory(path));
        }
        File.WriteAllText(path, "{broken");
        Assert.Null(BundledPlugin.ReadLauncherDirectory(path));
    }

    [Fact]
    public void UninstallPreservesUnknownAndModifiedFiles()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Directory, "winmm.dll");
        File.WriteAllText(path, "owned bytes");
        string hash = BundledPlugin.Hash(File.ReadAllBytes(path));
        Assert.False(BundledPlugin.DeleteOwnedFile(path, null));
        File.WriteAllText(path, "someone else's bytes");
        Assert.False(BundledPlugin.DeleteOwnedFile(path, hash));
        Assert.Equal("someone else's bytes", File.ReadAllText(path));
        Assert.True(BundledPlugin.DeleteOwnedFile(path, BundledPlugin.Hash(File.ReadAllBytes(path))));
        Assert.False(File.Exists(path));
        Assert.True(BundledPlugin.DeleteOwnedFile(path, hash));
    }

    [Fact]
    public void JunctionsCannotRedirectReceiptReadsConfigurationOrOwnedDeletion()
    {
        using var fixture = new Fixture();
        string target = Path.Combine(fixture.Directory, "target");
        string link = Path.Combine(fixture.Directory, "link");
        System.IO.Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "installed.json"), "{\"ActivationEnabled\":true}");
        File.WriteAllBytes(Path.Combine(target, "fork.exe"), [0]);
        string receiptPath = Path.Combine(fixture.Directory, "launcher-receipt.json");
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new PluginInstallationReceipt
        { ActivationEnabled = true, LauncherPath = Path.Combine(link, "fork.exe") }));
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /j \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Null(BundledPlugin.ReadReceipt(Path.Combine(link, "installed.json")));
            Assert.Throws<InvalidDataException>(() => BundledPlugin.LoaderConfiguration(Path.Combine(link, "fork.exe")));
            Assert.Throws<InvalidDataException>(() => BundledPlugin.DeleteOwnedFile(Path.Combine(link, "fork.exe"), null));
            Assert.Null(BundledPlugin.ReadLauncherDirectory(receiptPath));
            System.IO.Directory.Delete(target, true); // A dangling junction must still be rejected.
            Assert.Throws<InvalidDataException>(() => BundledPlugin.EnsureSafePath(Path.Combine(link, "new-file")));
        }
        finally { System.IO.Directory.Delete(link, false); }
    }

    [Fact]
    public void UnknownResourcesCannotReadOtherEmbeddedApplicationData() =>
        Assert.Throws<ArgumentException>(() => BundledPlugin.ReadBytes("../settings.json"));

    [Fact]
    public void OversizedReceiptDoesNotActivateTheBundledPlugin()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Directory, "installed.json");
        File.WriteAllText(path, "{\"ActivationEnabled\":true,\"Unknown\":\"" + new string('x', 1024 * 1024) + "\"}");
        Assert.Null(BundledPlugin.ReadReceipt(path));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "bundled-plugin-tests-" + Guid.NewGuid().ToString("N"));
        public Fixture() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
