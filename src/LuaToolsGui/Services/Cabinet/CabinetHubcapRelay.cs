using System.Globalization;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuaToolsGui.Services.Cabinet;

/// <summary>
/// The <b>Cabinet</b> provider: Hubcap Lua configs fetched by the connected Cabinet server with the server
/// owner's own Hubcap keys (docs/cloud-api.md in the Cabinet repo, "Hubcap relay"). The user needs no key.
/// </summary>
/// <remarks>
/// Built on the shared lua cache. A current cached copy is installed straight away. Otherwise the server is
/// asked to fetch the game (saying which cached copy was stale, if any), the request is polled while it is
/// queued or running, and the exact version the server names is downloaded and verified. What gets
/// installed goes through the unchanged install path and <see cref="ManifestContentPolicy"/>, and a copy
/// from Cabinet is never uploaded back (it is a <see cref="CabinetCachedFile"/>). Cancelling or timing out
/// only stops this client: the server finishes the job for whoever else asked.
/// </remarks>
/// <summary>Why a cached copy needs the user's go-ahead before it is installed.</summary>
public enum CabinetCopyQuestion
{
    /// <summary>Hubcap has nothing newer than the copy, which is behind Steam's current build.</summary>
    NothingNewer,
    /// <summary>Steam's depot info is unavailable, so the copy's build can't be checked.</summary>
    Unverified,
    /// <summary>An automatic add found no current copy anywhere; Cabinet's copy may be for an older build.</summary>
    NoOtherSource,
}

public sealed class CabinetHubcapRelay
{
    /// <summary>The source name in the providers list, the agent catalogue and the store-page plugin.</summary>
    public const string SourceName = "Cabinet";
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(10);
    /// <summary>
    /// How long an automatically chosen Cabinet waits before the add moves on to the next source. The server
    /// job keeps running for others (and fills the cache for the next add).
    /// </summary>
    public static readonly TimeSpan AutomaticTimeout = TimeSpan.FromMinutes(2);
    /// <summary>How long a "relay enabled" answer is reused by source listings.</summary>
    public static readonly TimeSpan AvailabilityTtl = TimeSpan.FromSeconds(60);
    /// <summary>Consecutive failed polls tolerated before giving up on an unreachable server.</summary>
    internal const int MaxPollFailures = 5;

    private readonly ICabinetHubcapClient _client;
    private readonly Func<long, IProgress<DownloadProgress>?, CancellationToken, Task<CabinetLuaCache.Lookup>> _lookup;
    private readonly Func<CabinetLuaMeta, IProgress<DownloadProgress>?, CancellationToken, Task<CabinetCachedFile>> _download;
    private readonly Func<CabinetLuaMeta, CancellationToken, Task<CabinetLuaCache.BuildCheck>> _checkBuild;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private (bool Enabled, DateTimeOffset Until)? _availability;

    public CabinetHubcapRelay(ICabinetHubcapClient client, CabinetLuaCache cache, ILogger<CabinetHubcapRelay> log)
        : this(client, (appId, progress, ct) => cache.LookupAsync(appId, progress, ct, ignoreBackoff: true), cache.DownloadExactAsync,
            cache.CheckBuildAsync, log) { }

    internal CabinetHubcapRelay(ICabinetHubcapClient client,
        Func<long, IProgress<DownloadProgress>?, CancellationToken, Task<CabinetLuaCache.Lookup>> lookup,
        Func<CabinetLuaMeta, IProgress<DownloadProgress>?, CancellationToken, Task<CabinetCachedFile>> download,
        Func<CabinetLuaMeta, CancellationToken, Task<CabinetLuaCache.BuildCheck>> checkBuild, ILogger? log = null)
    {
        _client = client;
        _lookup = lookup;
        _download = download;
        _checkBuild = checkBuild;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Injectable clock and delay, so polling and the timeout can be tested without waiting.</summary>
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// Asks the user whether to install a cached copy that is not known to be Steam's current build
    /// (arguments: app id, game title, the copy, why). Null (headless) declines. Set by the app at startup;
    /// the token is the download's own, so cancelling the download dismisses the question.
    /// </summary>
    public Func<long, string, CabinetLuaMeta, CabinetCopyQuestion, CancellationToken, Task<bool>>? ConfirmCopy { get; set; }

    public bool IsConnected => _client.IsConnected;

    /// <summary>
    /// Whether the provider should be listed: Cabinet Cloud is connected and the server says its relay can
    /// fetch now. The answer is reused for <see cref="AvailabilityTtl"/>; an unreachable server means no.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (!_client.IsConnected) return false;
        lock (_gate) if (_availability is { } known && UtcNow() < known.Until) return known.Enabled;
        bool enabled;
        try { enabled = await _client.GetRelayEnabledAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogInformation("Cabinet Hubcap relay: availability check failed ({Error})", ex.GetType().Name);
            enabled = false;
        }
        lock (_gate) _availability = (enabled, UtcNow() + AvailabilityTtl);
        return enabled;
    }

    /// <summary>
    /// Whether a download by source name may start: any other source is not this class's call, while the
    /// Cabinet source needs a connected server whose relay is enabled (the check the source lists apply).
    /// </summary>
    public static async Task<bool> AdmitsAsync(string source, CabinetHubcapRelay? relay, CancellationToken ct) =>
        !string.Equals(source, SourceName, StringComparison.OrdinalIgnoreCase)
        || (relay is not null && await relay.IsAvailableAsync(ct).ConfigureAwait(false));

    /// <summary>Forget the cached availability (after a failure that says the relay is off).</summary>
    public void InvalidateAvailability() { lock (_gate) _availability = null; }

    /// <summary>
    /// The package to install for <paramref name="appId"/> when the user picked the Cabinet source (or named it
    /// to the CLI): questions, no fallback. <paramref name="report"/> receives the localized queue state (null
    /// once the bytes are downloading). Throws <see cref="DownloadAbortedException"/> with a localized message for
    /// every expected failure, including a declined copy (as a cancellation). Without
    /// <paramref name="interactive"/> (an agent-started add) nobody is asked and such a copy is declined.
    /// </summary>
    public async Task<CabinetCachedFile> FetchAsync(long appId, string title, bool interactive, Action<string?> report,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var outcome = await RunAsync(appId, OverallTimeout, report, progress, ct).ConfigureAwait(false);
        return outcome switch
        {
            Delivered delivered => delivered.File,
            Offer offer => await OfferAsync(appId, title, interactive, offer.Meta, offer.Question, progress, ct).ConfigureAwait(false),
            Failure failure => throw new DownloadAbortedException(failure.Message),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>
    /// What an automatic Cabinet attempt produced: a verified-current copy (<see cref="File"/>), or why not, with
    /// the cached copy that could still be offered as a last resort (<see cref="CachedCopy"/>, may be null).
    /// </summary>
    public sealed record CabinetAttempt(CabinetCachedFile? File, CabinetLuaMeta? CachedCopy, CabinetCopyQuestion LastResortQuestion,
        string Reason, string Message);

    /// <summary>
    /// Cabinet as the first, automatically chosen source (FastFetch, the plugin's one-click add, a CLI add without
    /// a source): never asks anything and waits at most <see cref="AutomaticTimeout"/>, so the caller can move on
    /// to the next source. Throws only <see cref="OperationCanceledException"/>, for the caller's cancellation.
    /// </summary>
    public async Task<CabinetAttempt> TryFetchAsync(long appId, Action<string?> report, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var outcome = await RunAsync(appId, AutomaticTimeout, report, progress, ct).ConfigureAwait(false);
        return outcome switch
        {
            Delivered delivered => new(delivered.File, null, CabinetCopyQuestion.NoOtherSource, "delivered", ""),
            Offer offer => new(null, offer.Meta, offer.Question == CabinetCopyQuestion.Unverified ? CabinetCopyQuestion.Unverified : CabinetCopyQuestion.NoOtherSource,
                offer.Reason, DeclinedMessage(offer.Question)),
            Failure failure => new(null, failure.Cached, CabinetCopyQuestion.NoOtherSource, failure.Reason, failure.Message),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>Logged when an automatic add moves on from Cabinet (no toast: the item shows the source used).</summary>
    public void NoteFallback(long appId, string reason, string source) =>
        _log.LogInformation("Cabinet Hubcap relay: {AppId} not delivered by Cabinet ({Reason}); trying {Source}", appId, reason, source);
    public void NoteFallbackFailed(long appId, string source, Exception error) =>
        _log.LogInformation("Cabinet Hubcap relay: fallback {Source} failed for {AppId} ({Error})", source, appId, error.GetType().Name);

    private abstract record Outcome;
    private sealed record Delivered(CabinetCachedFile File) : Outcome;
    /// <summary>A copy exists that is not known to be current: installed only after the user agrees.</summary>
    private sealed record Offer(CabinetLuaMeta Meta, CabinetCopyQuestion Question, string Reason) : Outcome;
    private sealed record Failure(string Message, string Reason, CabinetLuaMeta? Cached) : Outcome;

    /// <summary>The Cabinet steps, without any question: cache first, then the relay, polled up to <paramref name="limit"/>.</summary>
    private async Task<Outcome> RunAsync(long appId, TimeSpan limit, Action<string?> report, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!_client.IsConnected) return new Failure(Resources.Strings.Cabinet_Relay_Err_NotConnected, "not_connected", null);
        var started = UtcNow();

        // 1. A current cached copy needs no relay at all.
        var lookup = await _lookup(appId, progress, ct).ConfigureAwait(false);
        switch (lookup.Outcome)
        {
            case CabinetLuaCache.LookupOutcome.Current:
                _log.LogInformation("Cabinet Hubcap relay: {AppId} is current in the shared cache", appId);
                return new Delivered(lookup.File!);
            // Without knowing what the server holds (or with a copy it can't deliver), any request would be a
            // guess: "nothing cached" or "stale" would ask Hubcap for something it may not need to fetch.
            case CabinetLuaCache.LookupOutcome.Failed:
                return new Failure(Resources.Strings.Cabinet_Relay_Err_Server, "server_unavailable", null);
            // A copy whose build can't be checked is not reported stale (a refetch would spend shared quota on
            // what may be current): it is only ever installed with the user's go-ahead.
            case CabinetLuaCache.LookupOutcome.Unverifiable:
                return new Offer(lookup.Meta!, CabinetCopyQuestion.Unverified, "unverifiable");
        }
        // A verifiably stale copy can still be offered if nothing better turns up, unless it has no key for
        // the main depot: Steam can't download the game from that one, so it is only ever replaced.
        var cached = lookup.Outcome == CabinetLuaCache.LookupOutcome.Stale && !lookup.KeysMissing ? lookup.Meta : null;
        bool StillKeyless(CabinetLuaMeta meta) => lookup.KeysMissing && meta.Sha256 == lookup.StaleSha256;

        // 2. Ask the server; 3. poll while queued or running.
        CabinetRelayStatus status;
        try { status = await _client.RequestRelayAsync(appId, lookup.StaleSha256, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("Cabinet Hubcap relay: request for {AppId} failed ({Error})", appId, ex.GetType().Name);
            return new Failure(Resources.Strings.Cabinet_Relay_Err_Server, "server_unavailable", cached);
        }
        _log.LogInformation("Cabinet Hubcap relay: {AppId} requested (stale copy: {Stale}) → {State}", appId, lookup.StaleSha256 is not null, status.State);
        int failures = 0;
        while (status.IsPending)
        {
            report(StatusText(status));
            if (UtcNow() - started >= limit)
            {
                report(null);
                return new Failure(Resources.Strings.Cabinet_Relay_Err_Timeout, "timeout", cached);
            }
            await Delay(PollInterval, ct).ConfigureAwait(false);
            CabinetRelayStatus? next;
            try { next = await _client.GetRelayStatusAsync(appId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (++failures < MaxPollFailures)
            {
                _log.LogInformation("Cabinet Hubcap relay: status of {AppId} unavailable ({Error}); retrying", appId, ex.GetType().Name);
                continue;
            }
            catch (Exception) { report(null); return new Failure(Resources.Strings.Cabinet_Relay_Err_Server, "server_unavailable", cached); }
            if (next is null) { report(null); return new Failure(Resources.Strings.Cabinet_Relay_Err_Server, "job_gone", cached); }
            status = next;
            failures = 0;
        }
        report(null);
        _log.LogInformation("Cabinet Hubcap relay: {AppId} finished as {State}{Error}", appId, status.State,
            status.Error is null ? "" : " (" + status.Error + ")");

        // 4./5. Download exactly the version the server named, or report why not.
        switch (status.State)
        {
            case CabinetRelayStatus.Ready:
                // "Ready" is the server's word that this is the newest copy it has, not that it matches Steam:
                // Hubcap may still be behind, or someone may have shared an older build.
                if (StillKeyless(status.Meta!)) return new Failure(Resources.Strings.Cabinet_Relay_Err_KeysMissing, "keys_missing", null);
                var build = await _checkBuild(status.Meta!, ct).ConfigureAwait(false);
                if (build != CabinetLuaCache.BuildCheck.Current)
                {
                    _log.LogInformation("Cabinet Hubcap relay: {AppId} ready but {Build}", appId, build);
                    return build == CabinetLuaCache.BuildCheck.Stale
                        ? new Offer(status.Meta!, CabinetCopyQuestion.NothingNewer, "ready_but_stale")
                        : new Offer(status.Meta!, CabinetCopyQuestion.Unverified, "unverifiable");
                }
                try { return new Delivered(await DownloadAsync(status.Meta!, progress, ct).ConfigureAwait(false)); }
                catch (DownloadAbortedException ex) { return new Failure(ex.Message, "copy_unusable", cached); }
            case CabinetRelayStatus.NotNewer:
                if (StillKeyless(status.Meta!)) return new Failure(Resources.Strings.Cabinet_Relay_Err_KeysMissing, "keys_missing", null);
                return new Offer(status.Meta!, CabinetCopyQuestion.NothingNewer, "upstream_not_newer");
            default:
                if (status.Error is "relay_disabled" or "quota_exhausted" or "key_expired" or "key_rejected") InvalidateAvailability();
                return new Failure(ErrorMessage(status.Error), status.Error ?? "failed", cached);
        }
    }

    private static string DeclinedMessage(CabinetCopyQuestion question) => question switch
    {
        CabinetCopyQuestion.Unverified => Resources.Strings.Cabinet_Relay_Unverified_Declined,
        CabinetCopyQuestion.NoOtherSource => Resources.Strings.Cabinet_Relay_LastResort_Declined,
        _ => Resources.Strings.Cabinet_Relay_NotNewer_Declined,
    };

    /// <summary>
    /// Installs a copy not known to be current only when the user agrees. Agent-started adds (not
    /// <paramref name="interactive"/>) are never asked and don't install; a declined copy is a cancellation.
    /// </summary>
    public async Task<CabinetCachedFile> OfferAsync(long appId, string title, bool interactive, CabinetLuaMeta meta,
        CabinetCopyQuestion question, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var confirm = interactive ? ConfirmCopy : null;
        bool accepted = confirm is not null && await confirm(appId, title, meta, question, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); // a question dismissed by cancelling is a cancel, not a "no"
        if (!accepted) throw new DownloadAbortedException(DeclinedMessage(question), isCancellation: true);
        return await DownloadAsync(meta, progress, ct).ConfigureAwait(false);
    }

    private async Task<CabinetCachedFile> DownloadAsync(CabinetLuaMeta meta, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        try { return await _download(meta, progress, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("Cabinet Hubcap relay: copy of {AppId} unusable ({Error})", meta.AppId, ex.GetType().Name);
            throw new DownloadAbortedException(Resources.Strings.Cabinet_Relay_Err_CopyUnusable);
        }
    }

    /// <summary>The download item's sub-line while the server works on the request.</summary>
    public static string StatusText(CabinetRelayStatus status) => status.State == CabinetRelayStatus.Running
        ? Resources.Strings.Cabinet_Relay_Fetching
        : status.Position is { } position
            ? string.Format(CultureInfo.CurrentCulture, Resources.Strings.Cabinet_Relay_Queued, position)
            : Resources.Strings.Cabinet_Relay_Waiting;

    /// <summary>A server error code as a message the user can act on.</summary>
    public static string ErrorMessage(string? code) => code switch
    {
        "no_manifest" => Resources.Strings.Cabinet_Relay_Err_NoManifest,
        "quota_exhausted" => Resources.Strings.Cabinet_Relay_Err_Quota,
        "key_expired" => Resources.Strings.Cabinet_Relay_Err_KeyExpired,
        "key_rejected" => Resources.Strings.Cabinet_Relay_Err_KeyRejected,
        "relay_disabled" => Resources.Strings.Cabinet_Relay_Err_Disabled,
        "hubcap_unavailable" => Resources.Strings.Cabinet_Relay_Err_HubcapUnavailable,
        "invalid_package" => Resources.Strings.Cabinet_Relay_Err_InvalidPackage,
        _ => string.Format(CultureInfo.CurrentCulture, Resources.Strings.Cabinet_Relay_Err_Failed, code ?? "unknown"),
    };
}
