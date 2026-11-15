using System.IO;
using System.Net.Http;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;
using LuaToolsGui.Services.Downloads;
using Xunit;
using Strings = LuaToolsGui.Resources.Strings;

namespace LuaToolsGui.Tests;

/// <summary>The Cabinet provider: cache first, then the server's Hubcap relay, polled until it answers.</summary>
public sealed class CabinetHubcapRelayTests
{
    private const long App = 570;
    private static readonly string Stale = new('a', 64), Fresh = new('b', 64);
    private static CabinetLuaMeta Meta(string sha) => new(App, sha, 10, new Dictionary<long, string>(), "Hubcap (Cabinet)", DateTimeOffset.UnixEpoch, "Cabinet Cloud");
    private static readonly CabinetCachedFile Staged = new("staged.zip", "570.zip");

    /// <summary>A fake Cabinet server: the POST answer, then one GET answer (or exception) per poll.</summary>
    private sealed class FakeServer : ICabinetHubcapClient
    {
        public bool IsConnected { get; set; } = true;
        public bool Enabled = true;
        public Exception? EnabledFailure;
        public int EnabledCalls, StatusCalls;
        public CabinetRelayStatus Answer = new(CabinetRelayStatus.Queued, 1);
        public Exception? RequestFailure;
        public readonly Queue<object?> Polls = new();
        public readonly List<string?> Requests = [];

        public Task<bool> GetRelayEnabledAsync(CancellationToken ct)
        {
            EnabledCalls++;
            return EnabledFailure is null ? Task.FromResult(Enabled) : Task.FromException<bool>(EnabledFailure);
        }
        public Task<CabinetRelayStatus> RequestRelayAsync(long appId, string? staleSha256, CancellationToken ct)
        {
            Requests.Add(staleSha256);
            return RequestFailure is null ? Task.FromResult(Answer) : Task.FromException<CabinetRelayStatus>(RequestFailure);
        }
        public Task<CabinetRelayStatus?> GetRelayStatusAsync(long appId, CancellationToken ct)
        {
            StatusCalls++;
            object? next = Polls.Count > 0 ? Polls.Dequeue() : throw new InvalidOperationException("unexpected poll");
            return next is Exception ex ? Task.FromException<CabinetRelayStatus?>(ex) : Task.FromResult((CabinetRelayStatus?)next);
        }
    }

    private sealed class Harness
    {
        public readonly FakeServer Server = new();
        public CabinetLuaCache.Lookup Lookup = new(null, null, false);
        public readonly List<string> Downloads = [];
        public Exception? DownloadFailure;
        public readonly List<string?> Reports = [];
        public readonly List<TimeSpan> Delays = [];
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public Func<CancellationToken, Task>? OnDelay;
        public CabinetHubcapRelay Relay = null!;

        public Harness()
        {
            Relay = new CabinetHubcapRelay(Server, (_, _, _) => Task.FromResult(Lookup), (meta, _, _) =>
            {
                Downloads.Add(meta.Sha256);
                return DownloadFailure is null ? Task.FromResult(Staged) : Task.FromException<CabinetCachedFile>(DownloadFailure);
            })
            {
                UtcNow = () => Now,
                Delay = async (span, ct) => { Delays.Add(span); Now += span; if (OnDelay is not null) await OnDelay(ct); ct.ThrowIfCancellationRequested(); },
            };
        }

        public Task<CabinetCachedFile> Fetch(bool interactive = true, CancellationToken ct = default) =>
            Relay.FetchAsync(App, "Dota 2", interactive, Reports.Add, null, ct);
    }

    [Fact]
    public async Task ACurrentCachedCopyIsInstalledWithoutAskingTheRelay()
    {
        var h = new Harness { Lookup = new(Staged, null, false) };
        Assert.Same(Staged, await h.Fetch());
        Assert.Empty(h.Server.Requests);
        Assert.Empty(h.Downloads);
    }

    [Fact]
    public async Task AStaleCopyIsNamedAndTheQueueIsPolledUntilReady()
    {
        var h = new Harness { Lookup = new(null, Stale, false) };
        h.Server.Answer = new(CabinetRelayStatus.Queued, 2);
        h.Server.Polls.Enqueue(new CabinetRelayStatus(CabinetRelayStatus.Queued, 1));
        h.Server.Polls.Enqueue(new CabinetRelayStatus(CabinetRelayStatus.Running));
        h.Server.Polls.Enqueue(new CabinetRelayStatus(CabinetRelayStatus.Ready, Meta: Meta(Fresh)));
        Assert.Same(Staged, await h.Fetch());
        Assert.Equal(new string?[] { Stale }, h.Server.Requests);
        Assert.Equal(new[] { Fresh }, h.Downloads); // exactly the version the server named
        Assert.Equal(new string?[] { string.Format(Strings.Cabinet_Relay_Queued, 2), string.Format(Strings.Cabinet_Relay_Queued, 1), Strings.Cabinet_Relay_Fetching, null }, h.Reports);
        Assert.All(h.Delays, d => Assert.Equal(CabinetHubcapRelay.PollInterval, d));
        Assert.Equal(3, h.Delays.Count);
    }

    [Fact]
    public async Task NothingCachedSendsNoStaleShaAndAnImmediateReadyNeedsNoPolling()
    {
        var h = new Harness();
        h.Server.Answer = new(CabinetRelayStatus.Ready, Meta: Meta(Fresh));
        await h.Fetch();
        Assert.Equal(new string?[] { null }, h.Server.Requests);
        Assert.Equal(0, h.Server.StatusCalls);
        Assert.Equal(new[] { Fresh }, h.Downloads);
    }

    [Fact]
    public async Task AnUnknownCacheStateDoesNotAskTheRelay()
    {
        // "Nothing cached" would ask the server for a different thing than "my copy is stale".
        var h = new Harness { Lookup = new(null, null, true) };
        var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch());
        Assert.Equal(Strings.Cabinet_Relay_Err_Server, ex.Message);
        Assert.Empty(h.Server.Requests);
        // A stale copy whose bytes failed to download is still a known stale copy.
        h.Lookup = new(null, Stale, true);
        h.Server.Answer = new(CabinetRelayStatus.Ready, Meta: Meta(Fresh));
        await h.Fetch();
        Assert.Equal(new string?[] { Stale }, h.Server.Requests);
    }

    [Fact]
    public async Task NotConnectedFailsBeforeAnything()
    {
        var h = new Harness();
        h.Server.IsConnected = false;
        var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch());
        Assert.Equal(Strings.Cabinet_Relay_Err_NotConnected, ex.Message);
        Assert.False(await h.Relay.IsAvailableAsync(CancellationToken.None));
        Assert.Equal(0, h.Server.EnabledCalls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task NothingNewerOnHubcapAsksBeforeInstallingTheCachedCopy(bool answer, bool installs)
    {
        var h = new Harness { Lookup = new(null, Stale, false) };
        h.Server.Answer = new(CabinetRelayStatus.NotNewer, Meta: Meta(Stale));
        CabinetLuaMeta? asked = null;
        h.Relay.ConfirmOlderCopy = (appId, title, meta, _) => { asked = meta; Assert.Equal((App, "Dota 2"), (appId, title)); return Task.FromResult(answer); };
        if (installs)
        {
            Assert.Same(Staged, await h.Fetch());
            Assert.Equal(new[] { Stale }, h.Downloads);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch());
            Assert.True(ex.IsCancellation);
            Assert.Equal(Strings.Cabinet_Relay_NotNewer_Declined, ex.Message);
            Assert.Empty(h.Downloads);
        }
        Assert.Equal(Stale, asked!.Sha256);
    }

    [Fact]
    public async Task AnAgentStartedAddIsNeverPromptedAndDeclinesAnOlderCopy()
    {
        var h = new Harness();
        h.Server.Answer = new(CabinetRelayStatus.NotNewer, Meta: Meta(Stale));
        bool asked = false;
        h.Relay.ConfirmOlderCopy = (_, _, _, _) => { asked = true; return Task.FromResult(true); };
        var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch(interactive: false));
        Assert.True(ex.IsCancellation);
        Assert.False(asked);
        Assert.Empty(h.Downloads);
    }

    public static TheoryData<string, string> Failures => new()
    {
        { "no_manifest", Strings.Cabinet_Relay_Err_NoManifest },
        { "quota_exhausted", Strings.Cabinet_Relay_Err_Quota },
        { "key_expired", Strings.Cabinet_Relay_Err_KeyExpired },
        { "key_rejected", Strings.Cabinet_Relay_Err_KeyRejected },
        { "relay_disabled", Strings.Cabinet_Relay_Err_Disabled },
        { "hubcap_unavailable", Strings.Cabinet_Relay_Err_HubcapUnavailable },
        { "invalid_package", Strings.Cabinet_Relay_Err_InvalidPackage },
        { "something_new", string.Format(Strings.Cabinet_Relay_Err_Failed, "something_new") },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task ServerFailuresBecomeClearMessages(string code, string message)
    {
        var h = new Harness();
        h.Server.Polls.Enqueue(new CabinetRelayStatus(CabinetRelayStatus.Failed, Error: code));
        var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch());
        Assert.Equal(message, ex.Message);
        Assert.False(ex.IsCancellation);
        Assert.Empty(h.Downloads);
    }

    [Fact]
    public async Task APoolFailureMakesTheNextListingAskAgain()
    {
        var h = new Harness();
        Assert.True(await h.Relay.IsAvailableAsync(CancellationToken.None));
        Assert.True(await h.Relay.IsAvailableAsync(CancellationToken.None));
        Assert.Equal(1, h.Server.EnabledCalls); // reused within the TTL
        h.Server.Answer = new(CabinetRelayStatus.Failed, Error: "quota_exhausted");
        await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch());
        h.Server.Enabled = false;
        Assert.False(await h.Relay.IsAvailableAsync(CancellationToken.None));
        Assert.Equal(2, h.Server.EnabledCalls);
    }

    [Fact]
    public async Task AvailabilityExpiresAndAnUnreachableServerHidesTheSource()
    {
        var h = new Harness();
        h.Server.EnabledFailure = new HttpRequestException("down");
        Assert.False(await h.Relay.IsAvailableAsync(CancellationToken.None));
        h.Server.EnabledFailure = null;
        Assert.False(await h.Relay.IsAvailableAsync(CancellationToken.None)); // remembered for the TTL
        h.Now += CabinetHubcapRelay.AvailabilityTtl;
        Assert.True(await h.Relay.IsAvailableAsync(CancellationToken.None));
        Assert.Equal(2, h.Server.EnabledCalls);
    }

    [Fact]
    public async Task TheClientGivesUpAfterTheOverallTimeout()
    {
        var h = new Harness();
        for (int i = 0; i < 400; i++) h.Server.Polls.Enqueue(new CabinetRelayStatus(CabinetRelayStatus.Running));
        var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch());
        Assert.Equal(Strings.Cabinet_Relay_Err_Timeout, ex.Message);
        Assert.Equal(CabinetHubcapRelay.OverallTimeout, h.Now - DateTimeOffset.UnixEpoch);
        Assert.Equal((int)(CabinetHubcapRelay.OverallTimeout / CabinetHubcapRelay.PollInterval), h.Server.StatusCalls);
    }

    [Fact]
    public async Task CancellingStopsPollingAndLeavesTheServerJobAlone()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Server.Polls.Enqueue(new CabinetRelayStatus(CabinetRelayStatus.Queued, 1));
        h.OnDelay = _ => { if (h.Delays.Count == 2) cts.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Fetch(ct: cts.Token));
        // The client only stops asking; there is no call that could cancel the job for other users.
        Assert.Equal(1, h.Server.StatusCalls);
        Assert.Single(h.Server.Requests);
        Assert.Empty(h.Downloads);
    }

    [Fact]
    public async Task ShortOutagesWhilePollingAreRidden()
    {
        var h = new Harness();
        h.Server.Polls.Enqueue(new HttpRequestException("blip"));
        h.Server.Polls.Enqueue(new TimeoutException());
        h.Server.Polls.Enqueue(new CabinetRelayStatus(CabinetRelayStatus.Ready, Meta: Meta(Fresh)));
        await h.Fetch();
        Assert.Equal(new[] { Fresh }, h.Downloads);

        var down = new Harness();
        for (int i = 0; i < CabinetHubcapRelay.MaxPollFailures; i++) down.Server.Polls.Enqueue(new HttpRequestException("down"));
        var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => down.Fetch());
        Assert.Equal(Strings.Cabinet_Relay_Err_Server, ex.Message);
        Assert.Equal(CabinetHubcapRelay.MaxPollFailures, down.Server.StatusCalls);
    }

    [Fact]
    public async Task AFailedRequestOrAnUnverifiableCopyIsReported()
    {
        var h = new Harness();
        h.Server.RequestFailure = new HttpRequestException("down");
        Assert.Equal(Strings.Cabinet_Relay_Err_Server, (await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch())).Message);
        h.Server.RequestFailure = null;
        h.Server.Answer = new(CabinetRelayStatus.Ready, Meta: Meta(Fresh));
        h.DownloadFailure = new InvalidDataException("sha mismatch");
        Assert.Equal(Strings.Cabinet_Relay_Err_CopyUnusable, (await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch())).Message);
    }

    [Fact]
    public void TheCabinetSourceNeedsNoUserKeyAndIsNeverShared()
    {
        Assert.False(LuaToolsGui.Models.SourceMeta.Get(CabinetHubcapRelay.SourceName).RequiresUserKey);
        Assert.False(CabinetLuaCachePolicy.UsesPersonalKey(CabinetHubcapRelay.SourceName, needsKey: false));
        // Its downloads are CabinetCachedFiles: never uploaded back.
        Assert.False(CabinetLuaCachePolicy.ShouldUpload(connected: true, usesPersonalKey: false, fromCabinet: Staged is CabinetCachedFile));
        Assert.True(AcquisitionCoordinator.IsCabinet("Cabinet"));
    }

    [Theory]
    [InlineData(true, true, "available", new string[0])]
    [InlineData(true, false, "unavailable", new[] { "SOURCE_UNAVAILABLE", "CABINET_RELAY_DISABLED" })]
    [InlineData(false, true, "unavailable", new[] { "SOURCE_UNAVAILABLE", "CABINET_NOT_CONNECTED" })]
    public async Task TheSourceRowFollowsTheConnectionAndTheRelayWithoutLuaToolsGates(bool connected, bool enabled, string status, string[] reasons)
    {
        var h = new Harness();
        h.Server.IsConnected = connected;
        h.Server.Enabled = enabled;
        string directory = Path.Combine(Path.GetTempPath(), "CabinetHubcapRelayTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var http = new HttpClient();
            // A guest with no Hubcap key: neither lua.tools sign-in nor a key may lock this row.
            var auth = new AuthService(http, Path.Combine(directory, "auth.dat"));
            var coordinator = new AcquisitionCoordinator(null!, null!, new SettingsService(new AppSettings()), auth, null!, null!, null, h.Relay);
            var row = new PluginAddService.SourceRow { Name = AcquisitionCoordinator.CabinetSource, DisplayName = "Cabinet", Status = "available" };
            await coordinator.RefreshUsageAsync([row]);
            Assert.Equal(status, row.Status);
            Assert.Equal(reasons, row.DisabledReasons);
            Assert.False(row.Locked);
            Assert.False(row.NeedsKey);
            Assert.Equal(connected && enabled, row.CanDownload);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
