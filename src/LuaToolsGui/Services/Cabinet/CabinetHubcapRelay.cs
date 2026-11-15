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
public sealed class CabinetHubcapRelay
{
    /// <summary>The source name in the providers list, the agent catalogue and the store-page plugin.</summary>
    public const string SourceName = "Cabinet";
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(10);
    /// <summary>How long a "relay enabled" answer is reused by source listings.</summary>
    public static readonly TimeSpan AvailabilityTtl = TimeSpan.FromSeconds(60);
    /// <summary>Consecutive failed polls tolerated before giving up on an unreachable server.</summary>
    internal const int MaxPollFailures = 5;

    private readonly ICabinetHubcapClient _client;
    private readonly Func<long, IProgress<DownloadProgress>?, CancellationToken, Task<CabinetLuaCache.Lookup>> _lookup;
    private readonly Func<CabinetLuaMeta, IProgress<DownloadProgress>?, CancellationToken, Task<CabinetCachedFile>> _download;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private (bool Enabled, DateTimeOffset Until)? _availability;

    public CabinetHubcapRelay(ICabinetHubcapClient client, CabinetLuaCache cache, ILogger<CabinetHubcapRelay> log)
        : this(client, (appId, progress, ct) => cache.LookupAsync(appId, progress, ct, ignoreBackoff: true), cache.DownloadExactAsync, log) { }

    internal CabinetHubcapRelay(ICabinetHubcapClient client,
        Func<long, IProgress<DownloadProgress>?, CancellationToken, Task<CabinetLuaCache.Lookup>> lookup,
        Func<CabinetLuaMeta, IProgress<DownloadProgress>?, CancellationToken, Task<CabinetCachedFile>> download, ILogger? log = null)
    {
        _client = client;
        _lookup = lookup;
        _download = download;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Injectable clock and delay, so polling and the timeout can be tested without waiting.</summary>
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// Asks the user whether to install the server's cached copy although Hubcap has nothing newer
    /// (arguments: app id, game title, the copy). Null (headless) declines. Set by the app at startup.
    /// </summary>
    public Func<long, string, CabinetLuaMeta, CancellationToken, Task<bool>>? ConfirmOlderCopy { get; set; }

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

    /// <summary>Forget the cached availability (after a failure that says the relay is off).</summary>
    public void InvalidateAvailability() { lock (_gate) _availability = null; }

    /// <summary>
    /// The package to install for <paramref name="appId"/>. <paramref name="report"/> receives the localized
    /// queue state (null once the bytes are downloading). Throws <see cref="DownloadAbortedException"/> with
    /// a localized message for every expected failure, including a declined older copy (as a cancellation).
    /// Without <paramref name="interactive"/> (an agent-started add) nobody is asked and an older copy is declined.
    /// </summary>
    public async Task<CabinetCachedFile> FetchAsync(long appId, string title, bool interactive, Action<string?> report,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!_client.IsConnected) throw new DownloadAbortedException(Resources.Strings.Cabinet_Relay_Err_NotConnected);
        var started = UtcNow();

        // 1. A current cached copy needs no relay at all.
        var lookup = await _lookup(appId, progress, ct).ConfigureAwait(false);
        if (lookup.File is { } current)
        {
            _log.LogInformation("Cabinet Hubcap relay: {AppId} is current in the shared cache", appId);
            return current;
        }
        // Without knowing whether a copy exists, "nothing cached" would ask the server for the wrong thing.
        if (lookup.ServerFailed && lookup.StaleSha256 is null) throw new DownloadAbortedException(Resources.Strings.Cabinet_Relay_Err_Server);

        // 2. Ask the server; 3. poll while queued or running.
        CabinetRelayStatus status;
        try { status = await _client.RequestRelayAsync(appId, lookup.StaleSha256, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("Cabinet Hubcap relay: request for {AppId} failed ({Error})", appId, ex.GetType().Name);
            throw new DownloadAbortedException(Resources.Strings.Cabinet_Relay_Err_Server);
        }
        _log.LogInformation("Cabinet Hubcap relay: {AppId} requested (stale copy: {Stale}) → {State}", appId, lookup.StaleSha256 is not null, status.State);
        int failures = 0;
        while (status.IsPending)
        {
            report(StatusText(status));
            if (UtcNow() - started >= OverallTimeout) throw new DownloadAbortedException(Resources.Strings.Cabinet_Relay_Err_Timeout);
            await Delay(PollInterval, ct).ConfigureAwait(false);
            try
            {
                status = await _client.GetRelayStatusAsync(appId, ct).ConfigureAwait(false)
                    ?? throw new DownloadAbortedException(Resources.Strings.Cabinet_Relay_Err_Server);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (DownloadAbortedException) { throw; }
            catch (Exception ex) when (++failures < MaxPollFailures)
            {
                _log.LogInformation("Cabinet Hubcap relay: status of {AppId} unavailable ({Error}); retrying", appId, ex.GetType().Name);
            }
            catch (Exception) { throw new DownloadAbortedException(Resources.Strings.Cabinet_Relay_Err_Server); }
        }
        report(null);
        _log.LogInformation("Cabinet Hubcap relay: {AppId} finished as {State}{Error}", appId, status.State,
            status.Error is null ? "" : " (" + status.Error + ")");

        // 4./5. Download exactly the version the server named, or report why not.
        switch (status.State)
        {
            case CabinetRelayStatus.Ready:
                return await DownloadAsync(status.Meta!, progress, ct).ConfigureAwait(false);
            case CabinetRelayStatus.NotNewer:
                var confirm = interactive ? ConfirmOlderCopy : null;
                if (confirm is null || !await confirm(appId, title, status.Meta!, ct).ConfigureAwait(false))
                    throw new DownloadAbortedException(Resources.Strings.Cabinet_Relay_NotNewer_Declined, isCancellation: true);
                return await DownloadAsync(status.Meta!, progress, ct).ConfigureAwait(false);
            default:
                if (status.Error is "relay_disabled" or "quota_exhausted" or "key_expired" or "key_rejected") InvalidateAvailability();
                throw new DownloadAbortedException(ErrorMessage(status.Error));
        }
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
