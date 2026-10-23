using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.Services.OnlineFixMe;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class OnlineFixMeJobTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlineFixMeJobTests", Guid.NewGuid().ToString("N"));
    private readonly CountingHandler _handler = new();
    private readonly HttpClient _http;
    private const string ArchiveName = "Game_Fix_Repair.rar";

    public OnlineFixMeJobTests()
    {
        Directory.CreateDirectory(_directory);
        _http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), _handler);
    }

    private DownloadJob Job(bool enabled, long appId = 620)
    {
        var settings = new SettingsService(new AppSettings
        {
            ExecutableGameFixesEnabled = enabled, SteamPathOverride = _directory,
        });
        var library = new SteamLibraryService(new SteamService(settings));
        var covers = new CoverCache(_handler, Path.Combine(_directory, "covers"));
        var jobs = new ManifestJobFactory(null!, null!, settings, null!, library, covers,
            new ToastService(), null!, null!, null!, null!, new OnlineFixMeClient(_http));
        return jobs.CreateOnlineFixMeJob(OnlineFixMeListing.FolderFromName("Game")!, ArchiveName, appId, "Game");
    }

    [Fact]
    public async Task DisabledConsentRejectsTheActualDownloadStepBeforeAnyHttpRequest()
    {
        var job = Job(enabled: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.DownloadAsync(null!, null!, default));
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task MissingInstallRejectsBeforeAnyHttpRequestEvenWithConsent()
    {
        var job = Job(enabled: true);
        var error = await Assert.ThrowsAsync<DownloadAbortedException>(() => job.DownloadAsync(null!, null!, default));
        Assert.Equal(string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, "Game"), error.Message);
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public void QueueKeyIncludesTheGameAndStableFixId()
    {
        var job = Job(enabled: true);
        Assert.Equal(DownloadKind.DenuvoFix, job.Kind);
        Assert.Equal($"onlinefixme:620:{OnlineFixMeFixIds.ForArchive(ArchiveName)}", job.DedupeKey);
        Assert.NotEqual(job.DedupeKey, Job(enabled: true, appId: 621).DedupeKey);
    }

    [Fact]
    public void ConvertedZipStaysInsideTheDownloadedFilesOwnStagingDirectory()
    {
        string staging = Path.Combine(_directory, "staging", "per-download");
        string converted = ManifestJobFactory.GetOnlineFixMeConvertedZipPath(Path.Combine(staging, ArchiveName));
        Assert.Equal(staging, Path.GetDirectoryName(converted));
        Assert.Equal("converted.zip", Path.GetFileName(converted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionFailureOrRevokedConsentCleansBothStagedFiles(bool validZip)
    {
        string path = Path.Combine(_directory, ArchiveName);
        if (validZip)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using var entry = zip.CreateEntry("fix.dll").Open();
            entry.Write([0x4d, 0x5a, 0, 0]); // Inert bytes, never executed.
        }
        else File.WriteAllText(path, "corrupt archive");

        // Consent may be revoked between queue phases; the unchanged apply path rechecks it.
        var result = await Job(enabled: false).InstallAsync(new DownloadedFile(path, ArchiveName), null!, default);
        Assert.False(result.Ok);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(ManifestJobFactory.GetOnlineFixMeConvertedZipPath(path)));
        Assert.False(Directory.Exists(Path.Combine(_directory, ManifestJobFactory.FixRecordDir)));
    }

    [Fact]
    public async Task CancelledConversionCleansStagingAndPropagatesCancellation()
    {
        string path = Path.Combine(_directory, ArchiveName);
        File.WriteAllText(path, "unused");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Job(enabled: true)
            .InstallAsync(new DownloadedFile(path, ArchiveName), null!, cancellation.Token));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(ManifestJobFactory.GetOnlineFixMeConvertedZipPath(path)));
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
}
