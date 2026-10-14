using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class DependencyQuarantineTests
{
    [Theory]
    [InlineData("Steamless", AppConfig.SteamlessRepo, "Steamless.zip")]
    [InlineData("DepotDownloader", AppConfig.DepotDownloaderRepo, "DepotDownloaderMod.zip")]
    [InlineData("SteamAutoCrack", AppConfig.SteamAutoCrackRepo, "SteamAutoCrack.zip")]
    [InlineData("CloudRedirect", AppConfig.CloudRedirectRepo, "CloudRedirect.exe")]
    [InlineData("LuaToolsPlugin", "madoiscool/LTSP", "plugin.zip")]
    [InlineData("CloudRedirectAddon", AppConfig.CloudRedirectRepo, "cloud_redirect.dll")]
    public async Task OriginalReleaseAndAssetUrlsAcquireInertDataWithoutApproval(string component, string repo, string name)
    {
        using var temp = new TempFolder();
        byte[] fakeArtifact = [0x4d, 0x5a, 1, 2, 3]; // Synthetic bytes, never a real executable.
        string releaseUrl = $"https://api.github.com/repos/{repo}/releases/latest";
        string assetUrl = $"https://github.com/{repo}/releases/download/v1/{name}";
        var handler = new ReleaseHandler(repo, name, fakeArtifact);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        var gh = new GithubProxy(http);
        string? path = await DependencyQuarantine.AcquireLatestAsync(gh, component, repo,
            a => a.Name == name, null, default, temp.Path);
        Assert.NotNull(path);
        Assert.Equal(fakeArtifact, File.ReadAllBytes(path!));
        Assert.EndsWith(".download", path);
        Assert.DoesNotContain(name, System.IO.Path.GetFileName(path));
        Assert.Equal(new[] { releaseUrl, assetUrl }, handler.Requests);
        Assert.False(DependencyTrustPolicy.IsTrustedFile(component, path!, name));
        Assert.Throws<InvalidOperationException>(() => DependencyTrustPolicy.RequireInstallApproval(component, true));
        // A second user action may check metadata, but does not download the same intact artifact again.
        Assert.Equal(path, await DependencyQuarantine.AcquireLatestAsync(gh, component, repo,
            a => a.Name == name, null, default, temp.Path));
        Assert.Equal(1, handler.Requests.Count(url => url == assetUrl));
    }

    [Theory]
    [InlineData("../payload.zip")]
    [InlineData("folder/payload.zip")]
    [InlineData("folder\\payload.zip")]
    [InlineData("C:\\payload.zip")]
    [InlineData("payload.zip:stream")]
    [InlineData("NUL.zip")]
    [InlineData("payload.zip ")]
    public async Task HostileMetadataCannotChooseAFilePathOrTriggerAssetTransport(string name)
    {
        using var temp = new TempFolder();
        var handler = new ReleaseHandler(AppConfig.SteamlessRepo, name, [1]);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => DependencyQuarantine.AcquireLatestAsync(
            new GithubProxy(http), "Steamless", AppConfig.SteamlessRepo, _ => true, null, default, temp.Path));
        Assert.Single(handler.Requests);
        Assert.Empty(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("https://evil.example/payload.zip")]
    [InlineData("https://github.com/other/project/releases/download/v1/payload.zip")]
    [InlineData("https://github.com/atom0s/Steamless/releases/download/v1/different.zip")]
    [InlineData("http://github.com/atom0s/Steamless/releases/download/v1/payload.zip")]
    public async Task MetadataCannotSubstituteAnAssetOriginOrRepository(string url)
    {
        using var temp = new TempFolder();
        var handler = new ReleaseHandler(AppConfig.SteamlessRepo, "payload.zip", [1]) { AssetUrlOverride = url };
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => DependencyQuarantine.AcquireLatestAsync(
            new GithubProxy(http), "Steamless", AppConfig.SteamlessRepo, _ => true, null, default, temp.Path));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExistingOrDownloadedExecutableSubstitutionCannotReturnAnExecutionPath()
    {
        using var temp = new TempFolder();
        string installed = System.IO.Path.Combine(temp.Path, "tool");
        Directory.CreateDirectory(installed);
        string exe = System.IO.Path.Combine(installed, "Steamless.CLI.exe");
        byte[] substituted = [0x4d, 0x5a, 9];
        File.WriteAllBytes(exe, substituted);
        var handler = new ReleaseHandler(AppConfig.SteamlessRepo, "Steamless.zip", [1, 2, 3]);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => DependencyQuarantine.PrepareToolAsync(
            new GithubProxy(http), "Steamless", AppConfig.SteamlessRepo, installed, exe,
            a => a.Name.EndsWith(".zip"), null, default, System.IO.Path.Combine(temp.Path, "quarantine"),
            enabled: _ => true, explicitUserAction: true));
        Assert.Equal(substituted, File.ReadAllBytes(exe));
        Assert.Single(Directory.GetFiles(installed));
        string artifact = Assert.Single(Directory.GetFiles(temp.Path, "*.download", SearchOption.AllDirectories));
        // Publisher hash matches downloaded bytes; it still provides no independent execution approval.
        Assert.False(DependencyTrustPolicy.IsTrustedFile("Steamless", artifact, "archive.zip"));
    }

    [Fact]
    public async Task TamperedQuarantineCacheIsRedownloadedButNeverPromotedToTrustedExecutable()
    {
        using var temp = new TempFolder();
        var handler = new ReleaseHandler(AppConfig.CloudRedirectRepo, "CloudRedirect.exe", [1, 2, 3]);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        var gh = new GithubProxy(http);
        string path = (await DependencyQuarantine.AcquireLatestAsync(gh, "CloudRedirect", AppConfig.CloudRedirectRepo,
            _ => true, null, default, temp.Path))!;
        File.WriteAllBytes(path, [3, 2, 1]);
        await DependencyQuarantine.AcquireLatestAsync(gh, "CloudRedirect", AppConfig.CloudRedirectRepo,
            _ => true, null, default, temp.Path);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Equal(2, handler.Requests.Count(url => url.Contains("/releases/download/")));
        Assert.False(DependencyTrustPolicy.IsTrustedFile("CloudRedirect", path, "CloudRedirect.exe"));
    }

    private sealed class ReleaseHandler(string repo, string name, byte[] bytes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? AssetUrlOverride { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (url == $"https://api.github.com/repos/{repo}/releases/latest")
            {
                string json = JsonSerializer.Serialize(new
                {
                    tag_name = "v1",
                    assets = new[] { new { name, browser_download_url = AssetUrlOverride ??
                        $"https://github.com/{repo}/releases/download/v1/{name}",
                        size = bytes.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() } }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (url == $"https://github.com/{repo}/releases/download/v1/{name}")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            throw new InvalidOperationException("Unexpected test transport request.");
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dependency-tests-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
