using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamAutoCrackCompatibilityTests
{
    [Theory]
    [InlineData("v1", "v2", true)]
    [InlineData("v1", "v1", false)]
    [InlineData("v1", "", false)]
    public async Task LatestReleaseStatusReadsOriginalMetadataWithoutArtifactAcquisition(string installed, string latest, bool expected)
    {
        var handler = new SacHandler(latest);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        Assert.Equal(expected, await SteamAutoCrackService.ProbeLatestReleaseAsync(new GithubProxy(http), installed, default));
        Assert.Equal(new[] { $"https://api.github.com/repos/{AppConfig.SteamAutoCrackRepo}/releases/latest" }, handler.Requests);
    }

    [Fact]
    public async Task UnreviewedArtifactAcquisitionOccursBeforeAnyRuntimeRequirement()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sac-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var handler = new SacHandler("v2");
            using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
            bool runtimeChecked = false;
            await Assert.ThrowsAsync<InvalidDataException>(() => ManifestJobFactory.PrepareSteamAutoCrackAsync(
                () => DependencyQuarantine.PrepareToolAsync(new GithubProxy(http), "SteamAutoCrack", AppConfig.SteamAutoCrackRepo,
                    Path.Combine(directory, "installed"), Path.Combine(directory, "installed", "SteamAutoCrack.exe"),
                    a => a.Name.EndsWith(".zip"), null, default, directory, enabled: _ => true, explicitUserAction: true),
                () => { runtimeChecked = true; return Task.FromResult(SacPrepareResult.RuntimeFailed); }, true));
            Assert.False(runtimeChecked);
            Assert.Single(Directory.GetFiles(directory, "*.download", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(directory, "installed")));
            Assert.Equal(2, handler.Requests.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ReviewedToolStillRequiresAnExistingRuntimeBeforeLaunchCanProceed()
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<DownloadAbortedException>(() => ManifestJobFactory.PrepareSteamAutoCrackAsync(
            () => { calls.Add("tool"); return Task.FromResult<string?>("reviewed-local-tool"); },
            () => { calls.Add("runtime"); return Task.FromResult(SacPrepareResult.RuntimeFailed); }, true));
        Assert.Equal(new[] { "tool", "runtime" }, calls);
    }

    [Fact]
    public async Task BackgroundJobCannotInvokeAcquisitionOrRuntimeDelegates()
    {
        bool acquired = false, runtimeChecked = false;
        await Assert.ThrowsAsync<DownloadAbortedException>(() => ManifestJobFactory.PrepareSteamAutoCrackAsync(
            () => { acquired = true; return Task.FromResult<string?>("tool"); },
            () => { runtimeChecked = true; return Task.FromResult(SacPrepareResult.Ready); }, false));
        Assert.False(acquired);
        Assert.False(runtimeChecked);
    }

    private sealed class SacHandler(string latest) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (url == $"https://api.github.com/repos/{AppConfig.SteamAutoCrackRepo}/releases/latest")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { tag_name = latest, assets = new[] {
                        new { name = "SteamAutoCrack.zip", size = 3,
                            browser_download_url = $"https://github.com/{AppConfig.SteamAutoCrackRepo}/releases/download/v2/SteamAutoCrack.zip" } } }))
                });
            if (url == $"https://github.com/{AppConfig.SteamAutoCrackRepo}/releases/download/v2/SteamAutoCrack.zip")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            throw new InvalidOperationException("Unexpected test request.");
        }
    }
}
