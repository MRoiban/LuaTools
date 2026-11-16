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
        public CabinetLuaCache.Lookup Lookup = new(CabinetLuaCache.LookupOutcome.NotCached);
        public CabinetLuaCache.BuildCheck Build = CabinetLuaCache.BuildCheck.Current;
        public readonly List<string> Checked = [];
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
            }, (meta, _) => { Checked.Add(meta.Sha256); return Task.FromResult(Build); })
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
        var h = new Harness { Lookup = new(CabinetLuaCache.LookupOutcome.Current, Staged) };
        Assert.Same(Staged, await h.Fetch());
        Assert.Empty(h.Server.Requests);
        Assert.Empty(h.Downloads);
    }

    [Fact]
    public async Task AStaleCopyIsNamedAndTheQueueIsPolledUntilReady()
    {
        var h = new Harness { Lookup = StaleLookup };
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

    private static readonly CabinetLuaCache.Lookup StaleLookup = new(CabinetLuaCache.LookupOutcome.Stale, null, Meta(Stale));

    [Fact]
    public async Task AnUnknownCacheStateDoesNotAskTheRelay()
    {
        // "Nothing cached" would ask the server for a different thing than "my copy is stale".
        var h = new Harness { Lookup = new(CabinetLuaCache.LookupOutcome.Failed) };
        var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch());
        Assert.Equal(Strings.Cabinet_Relay_Err_Server, ex.Message);
        Assert.Empty(h.Server.Requests);
        // A current copy whose bytes failed to download is a server failure, never reported as stale.
        h.Lookup = new(CabinetLuaCache.LookupOutcome.Failed, null, Meta(Fresh));
        Assert.Equal(Strings.Cabinet_Relay_Err_Server, (await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch())).Message);
        Assert.Empty(h.Server.Requests);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task AnUnverifiableCopyIsOfferedWithoutAskingTheRelay(bool answer, bool installs)
    {
        // Steam's depot info is unavailable: no stale= refetch (shared quota), the user decides instead.
        var h = new Harness { Lookup = new(CabinetLuaCache.LookupOutcome.Unverifiable, null, Meta(Stale)) };
        CabinetCopyQuestion? asked = null;
        h.Relay.ConfirmCopy = (_, _, _, question, _) => { asked = question; return Task.FromResult(answer); };
        if (installs) Assert.Same(Staged, await h.Fetch());
        else
        {
            var ex = await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch());
            Assert.True(ex.IsCancellation);
            Assert.Equal(Strings.Cabinet_Relay_Unverified_Declined, ex.Message);
        }
        Assert.Equal(CabinetCopyQuestion.Unverified, asked);
        Assert.Empty(h.Server.Requests);
        Assert.Equal(installs ? new[] { Stale } : Array.Empty<string>(), h.Downloads);
        // Agent-started adds are never asked and don't install it.
        bool prompted = false;
        h.Relay.ConfirmCopy = (_, _, _, _, _) => { prompted = true; return Task.FromResult(true); };
        Assert.True((await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch(interactive: false))).IsCancellation);
        Assert.False(prompted);
    }

    [Theory]
    [InlineData(CabinetLuaCache.BuildCheck.Stale, CabinetCopyQuestion.NothingNewer)]
    [InlineData(CabinetLuaCache.BuildCheck.Unverifiable, CabinetCopyQuestion.Unverified)]
    public async Task AReadyCopyStillBehindSteamIsOnlyInstalledAfterAsking(CabinetLuaCache.BuildCheck build, CabinetCopyQuestion expected)
    {
        var h = new Harness { Build = build };
        h.Server.Answer = new(CabinetRelayStatus.Ready, Meta: Meta(Fresh));
        CabinetCopyQuestion? asked = null;
        h.Relay.ConfirmCopy = (_, _, _, question, _) => { asked = question; return Task.FromResult(false); };
        Assert.True((await Assert.ThrowsAsync<DownloadAbortedException>(() => h.Fetch())).IsCancellation);
        Assert.Equal(expected, asked);
        Assert.Equal(new[] { Fresh }, h.Checked);
        Assert.Empty(h.Downloads);
        // A ready copy matching Steam installs without a question.
        h.Build = CabinetLuaCache.BuildCheck.Current;
        asked = null;
        Assert.Same(Staged, await h.Fetch());
        Assert.Null(asked);
    }

    [Fact]
    public async Task CancellingWhileTheQuestionIsOpenIsACancelNotADecline()
    {
        var h = new Harness();
        h.Server.Answer = new(CabinetRelayStatus.NotNewer, Meta: Meta(Stale));
        using var cts = new CancellationTokenSource();
        // The app's dialog closes when the token fires and reports "no"; the download must end as cancelled.
        h.Relay.ConfirmCopy = (_, _, _, _, ct) => { cts.Cancel(); return Task.FromResult(!ct.IsCancellationRequested); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Fetch(ct: cts.Token));
        Assert.Empty(h.Downloads);
    }

    [Fact]
    public async Task ThePluginDownloadRouteAdmitsTheCabinetSourceOnlyWhileTheRelayIsOn()
    {
        var h = new Harness();
        Assert.True(await CabinetHubcapRelay.AdmitsAsync("Ryuu", null, CancellationToken.None));
        Assert.False(await CabinetHubcapRelay.AdmitsAsync("Cabinet", null, CancellationToken.None));
        h.Server.Enabled = false;
        Assert.False(await CabinetHubcapRelay.AdmitsAsync("cabinet", h.Relay, CancellationToken.None));
        h.Relay.InvalidateAvailability();
        h.Server.Enabled = true;
        Assert.True(await CabinetHubcapRelay.AdmitsAsync("Cabinet", h.Relay, CancellationToken.None));
        h.Server.IsConnected = false;
        h.Relay.InvalidateAvailability();
        Assert.False(await CabinetHubcapRelay.AdmitsAsync("Cabinet", h.Relay, CancellationToken.None));
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
        var h = new Harness { Lookup = StaleLookup };
        h.Server.Answer = new(CabinetRelayStatus.NotNewer, Meta: Meta(Stale));
        CabinetLuaMeta? asked = null;
        h.Relay.ConfirmCopy = (appId, title, meta, question, _) => { asked = meta; Assert.Equal((App, "Dota 2"), (appId, title)); return Task.FromResult(answer); };
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
        h.Relay.ConfirmCopy = (_, _, _, _, _) => { asked = true; return Task.FromResult(true); };
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
            Assert.Equal(!(connected && enabled), row.Locked); // not clickable while the relay is off
            Assert.False(row.NeedsKey);
            Assert.Equal(connected && enabled, row.CanDownload);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ── Cabinet first ────────────────────────────────────────────────

    private static PluginAddService.SourceRow Row(string name, bool needsKey = false, bool usable = true) =>
        new() { Name = name, DisplayName = name, NeedsKey = needsKey, Status = usable ? "available" : "unavailable" };

    [Fact]
    public void CabinetIsFirstInTheProviderOrderOnlyWhenListed()
    {
        // As CheckAsync assembles them: own key first, the catalogue's order, Cabinet appended.
        var rows = new[] { Row("Ryuu"), Row(AcquisitionCoordinator.HubcapSource, needsKey: true), Row("SteamML"), Row("Cabinet") };
        Assert.Equal(new[] { "Cabinet", AcquisitionCoordinator.HubcapSource, "Ryuu", "SteamML" },
            AcquisitionCoordinator.Order(rows).Select(r => r.Name));
        // Relay off: no Cabinet row, and exactly the order from before.
        Assert.Equal(new[] { AcquisitionCoordinator.HubcapSource, "Ryuu", "SteamML" },
            AcquisitionCoordinator.Order(rows.Where(r => r.Name != "Cabinet")).Select(r => r.Name));
    }

    [Fact]
    public void AnAutomaticCabinetFallsBackToTheOtherUsableSourcesInOrder()
    {
        var rows = AcquisitionCoordinator.Order([Row("Ryuu", usable: false), Row(AcquisitionCoordinator.HubcapSource, needsKey: true), Row("SteamML"), Row("Cabinet"), Row("DexLua")]);
        Assert.Equal(new[] { new ManifestJobFactory.FallbackSource(AcquisitionCoordinator.HubcapSource, true),
                new ManifestJobFactory.FallbackSource("SteamML", false), new ManifestJobFactory.FallbackSource("DexLua", false) },
            AcquisitionCoordinator.FallbacksFrom(rows));
    }

    [Fact]
    public void TheStorePluginsSourceListPutsCabinetFirstWhileItsRelayIsOn()
    {
        var statuses = new Dictionary<string, string> { ["Ryuu"] = "available", ["SteamML"] = "unavailable" };
        Assert.Equal(new[] { ("Cabinet", true), ("Ryuu", true), ("SteamML", false) }, HttpServerService.LegacySourceOrder(statuses, cabinet: true));
        Assert.Equal(new[] { ("Ryuu", true), ("SteamML", false) }, HttpServerService.LegacySourceOrder(statuses, cabinet: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FastFetchPutsCabinetBeforeTheUsersOwnHubcapKey(bool relayOn)
    {
        var h = new Harness();
        h.Server.Enabled = relayOn;
        string directory = Path.Combine(Path.GetTempPath(), "CabinetHubcapRelayTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var hubcapHttp = new HttpClient(new FakeHubcap()) { BaseAddress = new Uri(AppConfig.HubcapBaseUrl) };
            using var http = new HttpClient();
            var settings = new SettingsService(new AppSettings()) { HubcapApiKey = "smm_" + new string('a', 96) };
            var coordinator = new AcquisitionCoordinator(null!, new HubcapService(hubcapHttp), settings, new AuthService(http, Path.Combine(directory, "auth.dat")),
                null!, null!, null, h.Relay);
            // FastFetch with a usable own key takes its short path: Cabinet first, the own key as its fallback.
            var sources = (await coordinator.CheckAsync(App, fastFetch: true)).Sources;
            Assert.Equal(relayOn ? new[] { "Cabinet", AcquisitionCoordinator.HubcapSource } : new[] { AcquisitionCoordinator.HubcapSource },
                sources.Select(r => r.Name));
            Assert.All(sources, r => Assert.True(r.CanDownload));
            Assert.Equal(sources[0], sources.FirstOrDefault(r => r.CanDownload)); // what FastFetch and `--source auto` pick
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FakeHubcap : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("/status/")
                    ? "{\"status\":\"ok\",\"manifest_file_exists\":true}"
                    : "{\"daily_usage\":0,\"daily_limit\":25,\"can_make_requests\":true}"),
            });
    }

    [Fact]
    public async Task AnAutomaticAttemptNeverAsksAndStopsWaitingAfterTwoMinutes()
    {
        var h = new Harness();
        bool asked = false;
        h.Relay.ConfirmCopy = (_, _, _, _, _) => { asked = true; return Task.FromResult(true); };
        for (int i = 0; i < 400; i++) h.Server.Polls.Enqueue(new CabinetRelayStatus(CabinetRelayStatus.Running));
        var attempt = await h.Relay.TryFetchAsync(App, h.Reports.Add, null, CancellationToken.None);
        Assert.Null(attempt.File);
        Assert.Equal("timeout", attempt.Reason);
        Assert.Equal(CabinetHubcapRelay.AutomaticTimeout, h.Now - DateTimeOffset.UnixEpoch);
        Assert.Equal(TimeSpan.FromMinutes(2), CabinetHubcapRelay.AutomaticTimeout);
        Assert.Null(h.Reports.Last()); // the queue line is cleared before moving on
        // Nothing newer: the copy is kept for the last resort, still without a question.
        var notNewer = new Harness { Lookup = StaleLookup };
        notNewer.Server.Answer = new(CabinetRelayStatus.NotNewer, Meta: Meta(Stale));
        notNewer.Relay.ConfirmCopy = (_, _, _, _, _) => { asked = true; return Task.FromResult(true); };
        var kept = await notNewer.Relay.TryFetchAsync(App, _ => { }, null, CancellationToken.None);
        Assert.Equal((null, Stale, CabinetCopyQuestion.NoOtherSource, "upstream_not_newer"), (kept.File, kept.CachedCopy?.Sha256, kept.LastResortQuestion, kept.Reason));
        // Unverifiable keeps its own question for the last resort.
        var unverified = new Harness { Lookup = new(CabinetLuaCache.LookupOutcome.Unverifiable, null, Meta(Stale)) };
        Assert.Equal(CabinetCopyQuestion.Unverified, (await unverified.Relay.TryFetchAsync(App, _ => { }, null, CancellationToken.None)).LastResortQuestion);
        Assert.False(asked);
        Assert.Empty(unverified.Server.Requests);
    }

    [Fact]
    public async Task CancellingAnAutomaticAttemptIsACancelNotAFallback()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Server.Polls.Enqueue(new CabinetRelayStatus(CabinetRelayStatus.Queued, 1));
        h.OnDelay = _ => { cts.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Relay.TryFetchAsync(App, _ => { }, null, cts.Token));
    }
}
