using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

public class PluginAddCancellationTests
{
    private sealed class ControlledHubcap : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int StatusCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/status/"))
            {
                Interlocked.Increment(ref StatusCalls);
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
                return Json("{\"status\":\"success\",\"manifest_file_exists\":true}");
            }
            return Json("{\"daily_usage\":0,\"daily_limit\":25,\"can_make_requests\":true}");
        }
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "PluginAddCancellationTests-" + Guid.NewGuid().ToString("N"));
        internal readonly ControlledHubcap Transport = new();
        private readonly HttpClient http;
        internal readonly DownloadQueue Queue;
        internal readonly PluginAddService Service;
        internal Fixture()
        {
            http = new HttpClient(Transport);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "steam.exe"), "inert fixture");
            var settings = new SettingsService(new AppSettings { FastFetch = true, SteamPathOverride = directory });
            var steam = new SteamService(settings);
            var installer = new LuaInstaller(steam, settings, new CacheService(Path.Combine(directory, "install-cache.json")), new LuaVault(() => steam.StPlugInDir, Path.Combine(directory, "vault")));
            settings.HubcapApiKey = "smm_" + new string('a', 96);
            var auth = new AuthService(http, Path.Combine(directory, "auth.dat"));
            var hubcap = new HubcapService(http);
            var covers = new CoverCache(Transport, Path.Combine(directory, "covers"));
            Queue = new DownloadQueue(new CacheService(Path.Combine(directory, "cache.json")), NullLogger<DownloadQueue>.Instance);
            var jobs = new ManifestJobFactory(null!, hubcap, settings, installer, null!, covers, null!, null!, null!, null!, null!, null!);
            Service = new PluginAddService(null!, hubcap, settings, auth, Queue, jobs);
        }
        public void Dispose()
        {
            Transport.Release.TrySetResult();
            http.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelDuringAvailabilityPreventsLateFastFetchAndAllowsFreshGeneration()
    {
        using var f = new Fixture();
        Assert.True(f.Service.Start(620, "Portal 2"));
        var old = f.Service.GetState(620)!;
        await f.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Service.Start(620, "Duplicate"));
        Assert.Same(old, f.Service.GetState(620));
        Assert.True(f.Service.Cancel(620));
        Assert.False(f.Service.Cancel(620));
        Assert.True(old.Cancelled);
        Assert.False(old.Checking);
        Assert.True(f.Service.Start(620, "Retry"));
        var fresh = f.Service.GetState(620)!;
        Assert.NotSame(old, fresh);
        Assert.True(f.Service.Cancel(620));
        f.Transport.Release.TrySetResult();
        await Task.WhenAll(old.CheckTask, fresh.CheckTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Queue.Items);
        Assert.False(old.SourcesLoaded);
        Assert.Null(old.Error);
        Assert.Null(old.InstallStatus);
        Assert.False(old.InstallFailed);
        Assert.True(fresh.Cancelled);
    }

    [Fact]
    public async Task CancelQueuedDownloadReportsCancellationAndRetryUsesNewQueueItem()
    {
        using var f = new Fixture();
        f.Transport.Release.TrySetResult();
        Assert.True(f.Service.Start(620, "Portal 2"));
        var state = f.Service.GetState(620)!;
        await Until(() => state.QueueItem is not null);
        var item = state.QueueItem!;
        Assert.False(f.Service.Start(620));
        Assert.False(f.Service.Pick(620, state.Sources[0].Name));
        Assert.True(f.Service.Cancel(620));
        await state.CheckTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Cancelled, item.Status);
        Assert.True(state.Cancelled);
        Assert.False(state.Busy);
        Assert.False(state.InstallFailed);
        Assert.Null(state.Error);
        Assert.Null(state.InstallStatus);
        Assert.All(state.Sources, row => Assert.False(row.Downloading));
        Assert.True(f.Service.Start(620, "Retry"));
        var retry = f.Service.GetState(620)!;
        await Until(() => retry.QueueItem is not null);
        Assert.NotSame(item, retry.QueueItem);
        Assert.True(f.Service.Cancel(620));
        await retry.CheckTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PickerRejectsLockedUnavailableAndCancelledRows()
    {
        using var f = new Fixture();
        Assert.True(f.Service.Start(620, "Portal 2"));
        var state = f.Service.GetState(620)!;
        await f.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Service.Pick(620, "Sadie (Morrenus)")); // still checking
        Assert.True(f.Service.Cancel(620));
        f.Transport.Release.TrySetResult();
        await state.CheckTask.WaitAsync(TimeSpan.FromSeconds(5));
        // Prepare the public picker state without making an external availability request.
        state.Cancelled = false;
        state.FastFetch = false;
        state.SourcesLoaded = true;
        state.Sources = [new() { Name = "locked", Status = "available", Locked = true },
            new() { Name = "missing", Status = "unavailable" }];
        Assert.False(f.Service.Pick(620, "locked"));
        Assert.False(f.Service.Pick(620, "missing"));
        Assert.False(f.Service.Pick(620, "unknown"));
        state.Sources[0].Locked = false;
        Assert.True(f.Service.Cancel(620));
        Assert.False(f.Service.Pick(620, "locked"));
        Assert.Empty(f.Queue.Items);
    }

    [Fact]
    public async Task ConcurrentPicksAdmitOneJobAndQueueCancellationIsNotFailure()
    {
        using var f = new Fixture();
        Assert.True(f.Service.Start(620, "Portal 2"));
        var state = f.Service.GetState(620)!;
        await f.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Service.Cancel(620));
        f.Transport.Release.TrySetResult();
        await state.CheckTask.WaitAsync(TimeSpan.FromSeconds(5));
        state.Cancelled = false;
        state.FastFetch = false;
        state.SourcesLoaded = true;
        state.Sources = [new() { Name = "Ryuu", Status = "available" }];
        var accepted = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => f.Service.Pick(620, "Ryuu"))));
        Assert.Single(accepted, value => value);
        await Until(() => state.QueueItem is not null);
        Assert.Single(f.Queue.Items);
        f.Queue.Cancel(state.QueueItem!); // cancellation from the Downloads page
        await Until(() => !state.Busy);
        Assert.True(state.Cancelled);
        Assert.False(state.InstallFailed);
        Assert.Null(state.Error);
        Assert.Null(state.InstallStatus);
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
