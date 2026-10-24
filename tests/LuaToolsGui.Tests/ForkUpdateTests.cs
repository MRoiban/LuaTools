using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class ForkUpdateTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LuaTools-update-tests-" + Guid.NewGuid().ToString("N"));
    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();
    private static readonly Version Old = new(1, 4, 1);
    public void Dispose() { _key.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private (byte[] Json, byte[] Sig, byte[] Zip) Release(string entry = "LuaTools.exe", string repo = UpdateService.Repository, string version = "1.4.2", long? size = null, bool includeRoot = false, int attributes = 0)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeRoot) { using var root = zip.CreateEntry("LuaTools.exe").Open(); root.WriteByte(0); }
            var item = zip.CreateEntry(entry); item.ExternalAttributes = attributes;
            using var stream = item.Open(); stream.Write(Encoding.UTF8.GetBytes("test inert data, never executed"));
        }
        byte[] package = memory.ToArray();
        var m = new UpdateService.Manifest(repo, version, new string('a', 40), $"LuaTools-{version}-win-x64.zip", size ?? package.Length, Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());
        var json = JsonSerializer.SerializeToUtf8Bytes(m);
        return (json, _key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), package);
    }

    private string Save((byte[] Json, byte[] Sig, byte[] Zip) release)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "release.json"), release.Json);
        File.WriteAllBytes(Path.Combine(_root, "release.sig"), release.Sig);
        File.WriteAllBytes(Path.Combine(_root, "package.zip"), release.Zip);
        return _root;
    }

    [Fact]
    public void SignedPackageExtractsExactAuthenticatedBytes()
    {
        var release = Release();
        string directory = Save(release);
        var destination = Path.Combine(_root, "extracted");
        UpdateService.ExtractVerified(directory, destination, PublicKey, Old);
        Assert.Equal("test inert data, never executed", File.ReadAllText(Path.Combine(destination, "LuaTools.exe")));
    }

    [Fact]
    public void RejectsUnsignedTamperedWrongKeyAndOversizedMetadata()
    {
        var r = Release();
        var changed = (byte[])r.Json.Clone(); changed[10] ^= 1;
        Assert.Throws<CryptographicException>(() => UpdateService.VerifyManifest(changed, r.Sig, PublicKey, Old));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => UpdateService.VerifyManifest(r.Json, r.Sig, other.ExportSubjectPublicKeyInfoPem(), Old));
        Assert.Throws<InvalidDataException>(() => UpdateService.VerifyManifest(r.Json, [], PublicKey, Old));
        Assert.Throws<InvalidDataException>(() => UpdateService.VerifyManifest(new byte[16385], r.Sig, PublicKey, Old));
    }

    [Theory]
    [InlineData("upstream/LuaTools", "1.4.2", 1)]
    [InlineData(UpdateService.Repository, "1.4.1", 1)]
    [InlineData(UpdateService.Repository, "1.4.0", 1)]
    [InlineData(UpdateService.Repository, "2.0.0", 1)]
    [InlineData(UpdateService.Repository, "1.4.2", 0)]
    [InlineData(UpdateService.Repository, "1.4.2", 268435457)]
    public void RejectsSignedWrongRepoRollbackAndInvalidSize(string repo, string version, long size)
    {
        var r = Release(repo: repo, version: version, size: size);
        Assert.Throws<InvalidDataException>(() => UpdateService.VerifyManifest(r.Json, r.Sig, PublicKey, Old));
    }

    [Theory]
    [InlineData("../LuaTools.exe")]
    [InlineData("/LuaTools.exe")]
    [InlineData("C:/LuaTools.exe")]
    [InlineData("dir/../../LuaTools.exe")]
    [InlineData("LuaTools.exe:evil")]
    [InlineData("CON.exe")]
    [InlineData("dir./LuaTools.exe")]
    [InlineData("dir//LuaTools.exe")]
    public void RejectsSignedArchiveTraversalAndWindowsAliases(string entry)
    {
        Save(Release(entry, includeRoot: true));
        var destination = Path.Combine(_root, "extracted");
        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractVerified(_root, destination, PublicKey, Old));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void RejectsPackageMutationAndSignedLengthMismatch()
    {
        var r = Release(); Save(r);
        r.Zip[^1] ^= 1; File.WriteAllBytes(Path.Combine(_root, "package.zip"), r.Zip);
        Assert.Throws<CryptographicException>(() => UpdateService.VerifyPackage(_root, PublicKey, Old));
        Save(Release(size: 3));
        Assert.Throws<CryptographicException>(() => UpdateService.VerifyPackage(_root, PublicKey, Old));
    }

    private sealed class Feed((byte[] Json, byte[] Sig, byte[] Zip) release) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            var bytes = url.EndsWith("release.json") ? release.Json : url.EndsWith("release.sig") ? release.Sig : release.Zip;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    [Fact]
    public async Task OwnFeedStagesButDoesNotApproveOrExecute()
    {
        using var feed = new Feed(Release());
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), feed);
        var updater = new UpdateService(http, _root, Old, PublicKey);
        await updater.CheckAndStageAsync();
        Assert.True(updater.HasStagedUpdate);
        Assert.Equal(new[] {
            "https://github.com/MRoiban/LuaTools/releases/latest/download/release.json",
            "https://github.com/MRoiban/LuaTools/releases/latest/download/release.sig",
            "https://github.com/MRoiban/LuaTools/releases/download/v1.4.2/LuaTools-1.4.2-win-x64.zip" }, feed.Requests);
        Assert.False(File.Exists(Path.Combine(_root, "approved.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "runs")));
    }

    [Fact]
    public async Task RecheckWithAStagedUpdateRepromptsWithoutDownloadingOrApproving()
    {
        using var feed = new Feed(Release());
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), feed);
        var updater = new UpdateService(http, _root, Old, PublicKey);
        int prompts = 0;
        updater.UpdateReady += () => prompts++;
        await updater.CheckAndStageAsync();
        await updater.CheckAndStageAsync(); // e.g. the plugin's "Check for updates" after the toast was dismissed
        Assert.Equal(2, prompts);
        Assert.Equal(3, feed.Requests.Count); // the second check never touched the network
        Assert.Single(Directory.EnumerateDirectories(_root));
        Assert.False(File.Exists(Path.Combine(_root, "approved.txt")));
    }

    [Fact]
    public async Task HostileFeedCannotTriggerPackageDownloadOrStaging()
    {
        var r = Release(); r.Sig[0] ^= 1;
        using var feed = new Feed(r);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), feed);
        var updater = new UpdateService(http, _root, Old, PublicKey);
        await updater.CheckAndStageAsync();
        Assert.False(updater.HasStagedUpdate);
        Assert.Equal(2, feed.Requests.Count);
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    [Fact]
    public void ExistingExtractionTargetCannotBeUsedAsExecutableCache()
    {
        Save(Release());
        var destination = Path.Combine(_root, "extracted"); Directory.CreateDirectory(destination);
        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractVerified(_root, destination, PublicKey, Old));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(-1610612736)]
    public void SignedArchiveLinksAreRejected(int attributes)
    {
        Save(Release("linked.dll", includeRoot: true, attributes: attributes));
        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractVerified(_root, Path.Combine(_root, "extracted"), PublicKey, Old));
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("oversized")]
    public async Task HostileTransportCannotEscapeOrExceedMetadataBudget(string attack)
    {
        using var handler = new HostileTransport(attack);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), handler);
        var updater = new UpdateService(http, _root, Old, PublicKey);
        await updater.CheckAndStageAsync();
        Assert.False(updater.HasStagedUpdate);
        Assert.Equal(1, handler.Requests);
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    private sealed class HostileTransport(string attack) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(attack == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK);
            if (attack == "redirect") response.Headers.Location = new Uri("https://evil.example/release.json");
            else response.Content = new ByteArrayContent(new byte[16385]);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task RememberedApprovedVersionRejectsServerRollback()
    {
        Save(Release(version: "1.4.4"));
        var id = Guid.NewGuid().ToString("N");
        var approved = Path.Combine(_root, id); Directory.CreateDirectory(approved);
        foreach (var name in new[] { "release.json", "release.sig", "package.zip" })
            File.Move(Path.Combine(_root, name), Path.Combine(approved, name));
        File.WriteAllText(Path.Combine(_root, "approved.txt"), id);
        using var feed = new Feed(Release(version: "1.4.3"));
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), feed);
        var updater = new UpdateService(http, _root, Old, PublicKey);
        await updater.CheckAndStageAsync();
        Assert.False(updater.HasStagedUpdate);
        Assert.Equal(2, feed.Requests.Count);
        Assert.Equal(id, File.ReadAllText(Path.Combine(_root, "approved.txt")));
    }

    [Fact]
    public async Task SourceBuildNeverContactsFeed()
    {
        using var feed = new Feed(Release());
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), feed);
        var updater = new UpdateService(http, _root, new Version(1, 4, 0), PublicKey);
        await updater.CheckAndStageAsync();
        Assert.Empty(feed.Requests);
        Assert.False(updater.HasStagedUpdate);
    }
}
