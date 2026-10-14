using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class UserAuthorizedDependenciesTests
{
    [Fact]
    public void ExplicitEnabledOperationStagesCompleteToolAndConsentReceipt()
    {
        using var temp = new TempFolder();
        string zip = CreateZip(temp.Path, ("tool.exe", "synthetic executable"), ("plugins/sidecar.dll", "synthetic dll"));
        string installed = System.IO.Path.Combine(temp.Path, "installed");
        UserAuthorizedDependencies.InstallArchive("Steamless", zip, installed, "tool.exe", _ => true, true);
        UserAuthorizedDependencies.RequireDirectory("Steamless", installed, _ => true);
        Assert.Equal("synthetic executable", System.Text.Encoding.UTF8.GetString(
            UserAuthorizedDependencies.ReadVerifiedBytes("Steamless", System.IO.Path.Combine(installed, "tool.exe"), _ => true)));
        Assert.True(File.Exists(System.IO.Path.Combine(installed, "plugins", "sidecar.dll")));
        Assert.False(DependencyTrustPolicy.IsTrustedFile("Steamless", System.IO.Path.Combine(installed, "tool.exe")));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ToggleAndExplicitActionAreBothRequired(bool enabled, bool action)
    {
        using var temp = new TempFolder();
        string zip = CreateZip(temp.Path, ("tool.exe", "synthetic"));
        string installed = System.IO.Path.Combine(temp.Path, "installed");
        Assert.Throws<InvalidOperationException>(() => UserAuthorizedDependencies.InstallArchive(
            "Steamless", zip, installed, "tool.exe", _ => enabled, action));
        Assert.False(Directory.Exists(installed));
    }

    [Fact]
    public async Task DefaultOffPreparationCannotUseCacheOrReachTransport()
    {
        using var temp = new TempFolder();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DependencyQuarantine.PrepareToolAsync(null!, "Steamless",
            AppConfig.SteamlessRepo, temp.Path, System.IO.Path.Combine(temp.Path, "tool.exe"), _ => true, null, default,
            temp.Path, explicitUserAction: true));
    }

    [Fact]
    public void DisablingBlocksPreviouslyAuthorizedBundleAndOtherComponent()
    {
        using var temp = new TempFolder();
        string zip = CreateZip(temp.Path, ("tool.exe", "synthetic"));
        string installed = System.IO.Path.Combine(temp.Path, "installed");
        UserAuthorizedDependencies.InstallArchive("Steamless", zip, installed, "tool.exe", c => c == "Steamless", true);
        Assert.Throws<InvalidOperationException>(() => UserAuthorizedDependencies.RequireDirectory("Steamless", installed, _ => false));
        Assert.Throws<InvalidOperationException>(() => UserAuthorizedDependencies.RequireDirectory("DepotDownloader", installed, c => c == "Steamless"));
        Assert.Throws<InvalidDataException>(() => UserAuthorizedDependencies.RequireDirectory("DepotDownloader", installed, _ => true));
    }

    [Theory]
    [InlineData("modify")]
    [InlineData("extra")]
    [InlineData("missing")]
    public void ChangedEntireBundleCannotReachExecutionVerification(string change)
    {
        using var temp = new TempFolder();
        string zip = CreateZip(temp.Path, ("tool.exe", "synthetic"), ("sidecar.dll", "dll"));
        string installed = System.IO.Path.Combine(temp.Path, "installed");
        UserAuthorizedDependencies.InstallArchive("Steamless", zip, installed, "tool.exe", _ => true, true);
        if (change == "modify") File.WriteAllText(System.IO.Path.Combine(installed, "sidecar.dll"), "substituted");
        if (change == "extra") File.WriteAllText(System.IO.Path.Combine(installed, "extra.dll"), "unexpected");
        if (change == "missing") File.Delete(System.IO.Path.Combine(installed, "sidecar.dll"));
        Assert.Throws<InvalidDataException>(() => UserAuthorizedDependencies.RequireFile("Steamless",
            System.IO.Path.Combine(installed, "tool.exe"), _ => true));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("tool.exe:stream")]
    [InlineData("NUL.exe")]
    [InlineData(UserAuthorizedDependencies.ReceiptFileName)]
    public void HostileArchiveCannotWriteIntoOrEscapeAnInstalledBundle(string entry)
    {
        using var temp = new TempFolder();
        string zip = CreateZip(temp.Path, ("tool.exe", "synthetic"), (entry, "hostile"));
        string installed = System.IO.Path.Combine(temp.Path, "installed");
        Assert.Throws<InvalidDataException>(() => UserAuthorizedDependencies.InstallArchive("Steamless", zip, installed,
            "tool.exe", _ => true, true));
        Assert.False(Directory.Exists(installed));
        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, "escape.exe")));
    }

    [Fact]
    public void DuplicateArchiveEntriesFailBeforePromotion()
    {
        using var temp = new TempFolder();
        string zip = CreateZip(temp.Path, ("tool.exe", "one"), ("TOOL.EXE", "two"));
        string installed = System.IO.Path.Combine(temp.Path, "installed");
        Assert.Throws<InvalidDataException>(() => UserAuthorizedDependencies.InstallArchive("Steamless", zip, installed,
            "tool.exe", _ => true, true));
        Assert.False(Directory.Exists(installed));
    }

    [Fact]
    public void ScatteredFileReceiptDoesNotApproveOtherTargetOrComponent()
    {
        using var temp = new TempFolder();
        string path = System.IO.Path.Combine(temp.Path, "loader.dll");
        File.WriteAllText(path, "synthetic");
        string receipt = System.IO.Path.Combine(temp.Path, "consent.json");
        var files = new Dictionary<string, string> { ["loader.dll"] = path };
        UserAuthorizedDependencies.RecordFiles("LuaToolsPlugin", receipt, files, _ => true, true);
        UserAuthorizedDependencies.RequireFiles("LuaToolsPlugin", receipt, files, _ => true);
        Assert.Throws<InvalidDataException>(() => UserAuthorizedDependencies.RequireFiles("OpenSteamTools", receipt, files, _ => true));
        File.WriteAllText(path, "changed");
        Assert.Throws<InvalidDataException>(() => UserAuthorizedDependencies.RequireFiles("LuaToolsPlugin", receipt, files, _ => true));
    }

    [Theory]
    [InlineData("Steamless", AppConfig.SteamlessRepo)]
    [InlineData("DepotDownloader", AppConfig.DepotDownloaderRepo)]
    [InlineData("SteamAutoCrack", AppConfig.SteamAutoCrackRepo)]
    [InlineData("CloudRedirect", AppConfig.CloudRedirectRepo)]
    public async Task EnabledUserPreparationAcquiresOriginalArtifactInstallsAndRejectsLaterDisable(string component, string repo)
    {
        using var temp = new TempFolder();
        string entryName = component switch
        {
            "Steamless" => "Steamless.CLI.exe", "DepotDownloader" => "DepotDownloaderMod.exe",
            "SteamAutoCrack" => "SteamAutoCrack.exe", _ => "CloudRedirect.exe",
        };
        string zip = CreateZip(temp.Path, (entryName, "synthetic executable"), ("sidecar.dll", "synthetic dll"));
        bool archive = component != "CloudRedirect";
        byte[] bytes = archive ? File.ReadAllBytes(zip) : [0x4d, 0x5a, 1, 2, 3];
        string assetName = archive ? "tool.zip" : "CloudRedirect.exe";
        var handler = new ToolHandler(repo, bytes, assetName);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        string installed = System.IO.Path.Combine(temp.Path, "installed");
        string entry = System.IO.Path.Combine(installed, entryName);
        bool enabled = true;
        string? prepared = await DependencyQuarantine.PrepareToolAsync(new GithubProxy(http), component, repo,
            installed, entry, a => a.Name == assetName, null, default,
            System.IO.Path.Combine(temp.Path, "quarantine"), enabled: _ => enabled, explicitUserAction: true, archive: archive);
        Assert.Equal(entry, prepared);
        UserAuthorizedDependencies.RequireDirectory(component, installed, _ => enabled);
        Assert.Equal(2, handler.Requests.Count);
        enabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DependencyQuarantine.PrepareToolAsync(null!, component, repo,
            installed, entry, _ => true, null, default, enabled: _ => enabled, explicitUserAction: true));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void FalseCentralDirectoryLengthCannotPromoteTruncatedExecutable()
    {
        using var temp = new TempFolder();
        string zip = System.IO.Path.Combine(temp.Path, "malformed.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var output = archive.CreateEntry("tool.exe").Open())
            output.Write(new byte[4096]);
        byte[] bytes = File.ReadAllBytes(zip);
        int central = -1;
        for (int i = 0; i <= bytes.Length - 46; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x02014b50u)
            { central = i; break; }
        Assert.True(central >= 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24, 4), 1);
        File.WriteAllBytes(zip, bytes);
        string installed = System.IO.Path.Combine(temp.Path, "installed");
        Assert.Throws<InvalidDataException>(() => UserAuthorizedDependencies.InstallArchive("Steamless", zip, installed,
            "tool.exe", _ => true, true));
        Assert.False(Directory.Exists(installed));
        Assert.Empty(Directory.GetDirectories(temp.Path, "installed.staging-*"));
    }

    private sealed class ToolHandler(string repo, byte[] bytes, string assetName) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            string asset = $"https://github.com/{repo}/releases/download/v1/{assetName}";
            if (url == $"https://api.github.com/repos/{repo}/releases/latest")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    JsonSerializer.Serialize(new { tag_name = "v1", assets = new[] { new { name = assetName, size = bytes.Length,
                        browser_download_url = asset } } })) });
            if (url == asset) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            throw new InvalidOperationException("Unexpected synthetic transport request.");
        }
    }

    private static string CreateZip(string directory, params (string path, string content)[] entries)
    {
        string path = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry.path).Open());
            writer.Write(entry.content);
        }
        return path;
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "consent-tests-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
