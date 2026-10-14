using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class GithubProxySecurityTests
{
    [Fact]
    public async Task OriginalApiAndDownloadMirrorsRemainAvailableAfterDirectFailure()
    {
        string api = "https://api.github.com/repos/atom0s/Steamless/releases/latest";
        string asset = "https://github.com/atom0s/Steamless/releases/download/v1/Steamless.zip";
        var handler = new FallbackHandler(api, asset);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        var gh = new GithubProxy(http);
        using var response = await gh.SendAsync(api);
        Assert.NotNull(response);
        Assert.Equal("{}", await response!.Content.ReadAsStringAsync());
        string directory = Path.Combine(Path.GetTempPath(), "github-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "artifact.download");
        try
        {
            await gh.DownloadAsync(asset, path, null);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            Assert.Equal(new[] { api, AppConfig.GithubApiMirrors[0] + api,
                asset, AppConfig.GithubDownloadMirrors[0] + asset }, handler.Requests);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("https://github.com/example/repo/releases/download/v1/file.zip?token=secret")]
    [InlineData("https://objects.githubusercontent.com/file?X-Amz-Signature=secret")]
    [InlineData("https://api.github.com/repos/example/repo/releases/latest?access_token=secret")]
    public void SignedOrCredentialBearingQueriesAreNeverSentToMirrors(string url) =>
        Assert.Equal(new[] { url }, GithubProxy.Candidates(url));

    [Theory]
    [InlineData("https://user@github.com/example/repo")]
    [InlineData("https://github.com:444/example/repo")]
    [InlineData("https://github.com.evil.example/example/repo")]
    [InlineData("http://github.com/example/repo")]
    [InlineData("https://github.com/example/repo#fragment")]
    [InlineData("https://lua.tools/other")]
    public void CandidatesRejectNonGitHubOriginsAndHostConfusion(string url) =>
        Assert.Throws<HttpRequestException>(() => GithubProxy.Candidates(url).ToArray());

    [Fact]
    public void OriginalPublicReleasePaginationCanUseApiMirror()
    {
        const string url = "https://api.github.com/repos/mendy-tools/verynotsusdllsthataredefnotstrelated/releases?per_page=100";
        Assert.Equal(new[] { url, AppConfig.GithubApiMirrors[0] + url }, GithubProxy.Candidates(url));
    }

    private sealed class FallbackHandler(string api, string asset) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (url == api || url == asset)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) });
            if (url == AppConfig.GithubApiMirrors[0] + api)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            if (url == AppConfig.GithubDownloadMirrors[0] + asset)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            throw new InvalidOperationException("Unexpected test transport request.");
        }
    }
}
