using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuaToolsGui.Services.Cabinet;

/// <summary>A manifest package served from the Cabinet server's shared cache rather than the chosen source.</summary>
public sealed record CabinetCachedFile(string FilePath, string FileName) : DownloadedFile(FilePath, FileName);

/// <summary>
/// What a shared package claims about its build: the depot pins of its lua's ACTIVE
/// <c>setManifestid</c> lines and the <c>&lt;depot&gt;_&lt;manifest&gt;.manifest</c> files it carries.
/// </summary>
public sealed record CabinetPackage(IReadOnlyDictionary<long, string> Pins, IReadOnlyList<(long Depot, string Manifest)> ManifestFiles)
{
    // ManifestContentPolicy's own declaration grammar: one declaration per line, double or single quotes,
    // an optional size, an optional ';' and an optional trailing "-- comment" that is ignored. Unlike
    // LuaFileParser (which the Builds page needs to see commented-out pins too), nothing in a comment
    // counts here: a pin hidden after "--" must not be able to vouch for a different active pin.
    private static readonly Regex Pin = new(
        "^setManifestid[ \\t]*\\([ \\t]*([1-9][0-9]{0,9})[ \\t]*,[ \\t]*(?:\"([0-9]{1,20})\"|'([0-9]{1,20})')[ \\t]*(?:,[ \\t]*[0-9]{1,20}[ \\t]*)?\\)[ \\t]*;?[ \\t]*(?:--.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex AddApp = new("^addappid[ \\t]*\\([ \\t]*([1-9][0-9]{0,9})[ \\t]*[,)]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ManifestName = new("^([1-9][0-9]{0,9})_([0-9]{1,20})\\.manifest$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Reads a package after the normal install validation. Throws <see cref="InvalidDataException"/> for
    /// anything a shared copy may not be: not exactly one lua, no ACTIVE <c>addappid(appId)</c> (stricter
    /// than a normal source, because the content comes from other users), or a depot pinned twice.
    /// </summary>
    public static CabinetPackage Read(byte[] package, long appId)
    {
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        ManifestContentPolicy.ValidateArchive(archive, manifestPackage: true, expectedAppId: appId);
        var luas = archive.Entries.Where(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList();
        if (luas.Count != 1) throw new InvalidDataException("A shared package needs exactly one lua.");
        string text;
        using (var stream = luas[0].Open())
            text = new UTF8Encoding(false, true).GetString(ManifestContentPolicy.ReadBounded(stream, ManifestContentPolicy.MaxLuaBytes));
        var manifests = new List<(long, string)>();
        foreach (var entry in archive.Entries)
            if (ManifestName.Match(entry.Name) is { Success: true } m)
                manifests.Add((long.Parse(m.Groups[1].Value), m.Groups[2].Value));
        return new CabinetPackage(ReadPins(text, appId), manifests);
    }

    /// <summary>Active pins of a lua; see <see cref="Read"/> for what is refused.</summary>
    public static IReadOnlyDictionary<long, string> ReadPins(string text, long appId)
    {
        var pins = new Dictionary<long, string>();
        bool declared = false;
        foreach (string raw in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
            if (AddApp.Match(line) is { Success: true } app && long.Parse(app.Groups[1].Value) == appId) declared = true;
            if (Pin.Match(line) is not { Success: true } pin) continue;
            long depot = long.Parse(pin.Groups[1].Value);
            string manifest = pin.Groups[2].Success ? pin.Groups[2].Value : pin.Groups[3].Value;
            if (!pins.TryAdd(depot, manifest)) throw new InvalidDataException("A depot is pinned more than once.");
        }
        if (!declared) throw new InvalidDataException("The lua does not actively declare the app.");
        return pins;
    }

    /// <summary>
    /// Current when every pin AND every carried manifest file names what Steam ships now for depots
    /// Steam reports a public manifest for. A lua without pins follows Steam's latest build by design,
    /// so it is current unless one of its manifest files is stale. No depot info: not current.
    /// </summary>
    public bool IsCurrent(AppDepotInfo? depots) =>
        depots is not null
        && ManifestPins.AreCurrent(Pins, depots)
        && ManifestFiles.All(f => !ManifestPins.IsBehind(f.Manifest, depots.Depots.FirstOrDefault(d => d.Id == f.Depot)?.PublicManifestId));
}

/// <summary>The decisions of the Cabinet lua cache, kept free of I/O so they can be tested directly.</summary>
public static class CabinetLuaCachePolicy
{
    /// <summary>
    /// Whether a source is fetched with the user's own key: the Hubcap path (<paramref name="needsKey"/>)
    /// or a source the catalogue marks as needing one, whatever path requested it (the store-page plugin
    /// always passes needsKey false).
    /// </summary>
    public static bool UsesPersonalKey(string sourceName, bool needsKey) =>
        needsKey || SourceMeta.Get(sourceName).RequiresUserKey;

    /// <summary>
    /// Whether a successfully installed package may be offered to the Cabinet server. Uploading is
    /// automatic whenever Cabinet is connected, except for a package fetched with the user's own key
    /// (re-sharing those is a separate decision) and a package that came from the cache in the first
    /// place. The upload itself still checks the package is current (<see cref="CabinetPackage.IsCurrent"/>).
    /// </summary>
    public static bool ShouldUpload(bool connected, bool usesPersonalKey, bool fromCabinet) =>
        connected && !usesPersonalKey && !fromCabinet;

    /// <summary>
    /// Whether the cached package can stand in for the chosen source: the bytes are the ones the server
    /// described, and the package's own pins and manifest files match what Steam ships now.
    /// </summary>
    public static bool IsUsable(CabinetLuaMeta meta, string actualSha256, CabinetPackage package, AppDepotInfo? depots) =>
        string.Equals(meta.Sha256, actualSha256, StringComparison.Ordinal)
        && ManifestPins.AreCurrent(meta.Pins, depots)
        && package.IsCurrent(depots);

    /// <summary>
    /// How long a cached blob may take: a floor for slow starts plus at least 256 KiB/s for its size,
    /// capped so a stalled server cannot hold a download for more than two minutes.
    /// </summary>
    public static TimeSpan TransferDeadline(long size) =>
        TimeSpan.FromSeconds(Math.Clamp(10 + Math.Max(0, size) / (256 * 1024), 10, 120)); // whole seconds
}

/// <summary>
/// Cabinet's shared lua cache, as seen from a manifest download: look for a current cached copy before
/// the chosen source, and share what was installed from a normal source afterwards.
/// </summary>
/// <remarks>
/// Nothing here can fail a download. Every Cabinet problem (not connected, unreachable, nothing cached,
/// stale, wrong bytes, a package the install policy would refuse) means "use the normal source", and
/// upload failures are logged only. A cached copy comes from another user, so it gets no extra trust:
/// it is installed through exactly the same <c>InstallManifest</c> path and
/// <see cref="ManifestContentPolicy"/> checks as any other download. After the server fails or times
/// out, the cache is skipped for <see cref="FailureBackoff"/> so an unreachable server does not slow
/// every download.
/// </remarks>
public class CabinetLuaCache
{
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);
    /// <summary>How long a lookup waits for Steam's depot info before calling the copy unverifiable.</summary>
    public static readonly TimeSpan DepotInfoWait = TimeSpan.FromSeconds(5);

    private readonly ICabinetLuaCacheClient _client;
    private readonly Func<long, CancellationToken, Task<AppDepotInfo?>> _currentDepots;
    private readonly ILogger _log;
    private readonly string _stagingFolder;
    private long _skipUntilTicks;

    public CabinetLuaCache(ICabinetLuaCacheClient client, SteamDepotInfo depots, ILogger<CabinetLuaCache> log)
        : this(client, (appId, ct) =>
        {
            // "Current" means Steam's build NOW: a session-cached answer may predate an update.
            depots.Invalidate(appId);
            return depots.GetAsync(appId, ct);
        }, log, HttpFileDownloader.StagingFolder) { }

    internal CabinetLuaCache(ICabinetLuaCacheClient client, Func<long, CancellationToken, Task<AppDepotInfo?>> currentDepots,
        ILogger? log = null, string? stagingFolder = null)
    {
        _client = client;
        _currentDepots = currentDepots;
        _log = log ?? NullLogger.Instance;
        _stagingFolder = stagingFolder ?? HttpFileDownloader.StagingFolder;
    }

    /// <summary>Injectable clock for the failure back-off.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public bool IsConnected => _client.IsConnected;

    /// <summary>True while a recent server failure keeps the cache out of downloads.</summary>
    internal bool BackingOff => UtcNow().Ticks < Interlocked.Read(ref _skipUntilTicks);

    private void BackOff(long appId, Exception ex)
    {
        Interlocked.Exchange(ref _skipUntilTicks, (UtcNow() + FailureBackoff).Ticks);
        _log.LogInformation("Cabinet lua cache: server failed for {AppId} ({Error}); skipping the cache for {Minutes} minutes",
            appId, ex.GetType().Name, FailureBackoff.TotalMinutes);
    }

    /// <summary>
    /// A staged copy of the server's current package for this game, or null to use the normal source.
    /// Throws only <see cref="OperationCanceledException"/> for the caller's own cancellation.
    /// </summary>
    public async Task<CabinetCachedFile?> TryGetCurrentAsync(long appId, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        (await LookupAsync(appId, progress, ct).ConfigureAwait(false)).File;

    /// <summary>What a lookup concluded about the server's cached copy of a game.</summary>
    public enum LookupOutcome
    {
        /// <summary>A copy matching Steam's current build, staged in <see cref="Lookup.File"/>.</summary>
        Current,
        /// <summary>The server holds no copy.</summary>
        NotCached,
        /// <summary>The copy is behind Steam's current build, or not a package a shared copy may be.</summary>
        Stale,
        /// <summary>Steam's depot info is unavailable or slow, so the copy's build can't be checked either way.</summary>
        Unverifiable,
        /// <summary>The server could not be asked, or its copy could not be downloaded as described.</summary>
        Failed,
    }

    /// <summary>
    /// What a lookup found. <see cref="Meta"/> describes the cached copy whenever one was read (also for
    /// <see cref="LookupOutcome.Failed"/> after the meta was read).
    /// </summary>
    public sealed record Lookup(LookupOutcome Outcome, CabinetCachedFile? File = null, CabinetLuaMeta? Meta = null)
    {
        /// <summary>The copy's SHA-256 when it is verifiably stale: the only case a refetch may name it.</summary>
        public string? StaleSha256 => Outcome == LookupOutcome.Stale ? Meta?.Sha256 : null;
    }

    /// <summary>Whether a copy's pins match Steam's current build, by the lookup's rule.</summary>
    public enum BuildCheck { Current, Stale, Unverifiable }

    /// <summary>Checks a copy the relay named (from its meta's pins) against Steam's current build.</summary>
    public async Task<BuildCheck> CheckBuildAsync(CabinetLuaMeta meta, CancellationToken ct)
    {
        var depots = await DepotsAsync(meta.AppId, ct).ConfigureAwait(false);
        return depots is null ? BuildCheck.Unverifiable : ManifestPins.AreCurrent(meta.Pins, depots) ? BuildCheck.Current : BuildCheck.Stale;
    }

    /// <summary>
    /// <see cref="TryGetCurrentAsync"/> with the reason when there is no current copy. With
    /// <paramref name="ignoreBackoff"/> the server is asked even during the failure back-off (the Hubcap
    /// relay must know whether a copy exists, since "nothing cached" means a different request).
    /// Throws only <see cref="OperationCanceledException"/> for the caller's own cancellation.
    /// </summary>
    public async Task<Lookup> LookupAsync(long appId, IProgress<DownloadProgress>? progress, CancellationToken ct, bool ignoreBackoff = false)
    {
        if (!_client.IsConnected) return new(LookupOutcome.Failed);
        if (BackingOff && !ignoreBackoff) return new(LookupOutcome.Failed);
        // Two rounds: a copy replaced (and evicted) between reading its meta and its bytes is read again.
        for (int round = 0; round < 2; round++)
        {
            CabinetLuaMeta? meta;
            try { meta = await _client.GetMetaAsync(appId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { BackOff(appId, ex); return new(LookupOutcome.Failed); }
            if (meta is null) { _log.LogDebug("Cabinet lua cache: nothing cached for {AppId}", appId); return new(LookupOutcome.NotCached); }

            var depots = await DepotsAsync(appId, ct).ConfigureAwait(false);
            if (depots is null)
            {
                _log.LogInformation("Cabinet lua cache: copy of {AppId} can't be checked (Steam depot info unavailable); using the normal source", appId);
                return new(LookupOutcome.Unverifiable, null, meta);
            }
            if (!ManifestPins.AreCurrent(meta.Pins, depots))
            {
                _log.LogInformation("Cabinet lua cache: copy of {AppId} is not current (older build); using the normal source", appId);
                return new(LookupOutcome.Stale, null, meta);
            }

            progress?.Report(new DownloadProgress(0, meta.Size));
            byte[]? bytes;
            try { bytes = await _client.DownloadAsync(appId, meta.Sha256, meta.Size, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            // A copy that can't be downloaded says nothing about its build: it is not stale.
            catch (Exception ex) { BackOff(appId, ex); return new(LookupOutcome.Failed, null, meta); }
            if (bytes is null) continue; // replaced and evicted since the meta was read
            progress?.Report(new DownloadProgress(bytes.Length, bytes.Length));

            // The meta is the server's word; the bytes and their own contents are what get installed.
            if (!string.Equals(Sha256(bytes), meta.Sha256, StringComparison.Ordinal))
            {
                _log.LogWarning("Cabinet lua cache: copy of {AppId} does not match its description; using the normal source", appId);
                return new(LookupOutcome.Failed, null, meta);
            }
            try
            {
                if (!CabinetLuaCachePolicy.IsUsable(meta, meta.Sha256, CabinetPackage.Read(bytes, appId), depots))
                {
                    _log.LogInformation("Cabinet lua cache: copy of {AppId} carries an older manifest; using the normal source", appId);
                    return new(LookupOutcome.Stale, null, meta);
                }
                // A copy Steam can't download the game from (no key for its main depot, e.g. shared by an
                // older LuaTools from a public source) counts as stale, so the relay replaces it from Hubcap.
                if (!Coverage(bytes, depots).Usable)
                {
                    _log.LogInformation("Cabinet lua cache: copy of {AppId} has no key for the game's main depot; using the normal source", appId);
                    return new(LookupOutcome.Stale, null, meta);
                }
                var file = Stage(bytes, appId);
                _log.LogInformation("Cabinet lua cache: installing the shared copy of {AppId} from {Source}", appId, meta.Source);
                return new(LookupOutcome.Current, file, meta);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Cabinet lua cache: copy of {AppId} is unusable ({Error}); using the normal source", appId, ex.GetType().Name);
                return new(LookupOutcome.Stale, null, meta);
            }
        }
        return new(LookupOutcome.Failed);
    }

    /// <summary>
    /// Downloads exactly the version <paramref name="meta"/> describes (the Hubcap relay's answer) and stages
    /// it after the same checks as a lookup, except Steam's build: the relay already decided freshness, and
    /// the user confirms an older copy. Throws <see cref="InvalidDataException"/> when the version is gone,
    /// its bytes differ from the meta, or the package is not one a shared copy may be.
    /// </summary>
    public async Task<CabinetCachedFile> DownloadExactAsync(CabinetLuaMeta meta, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new DownloadProgress(0, meta.Size));
        byte[] bytes = await _client.DownloadAsync(meta.AppId, meta.Sha256, meta.Size, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Cabinet copy is no longer kept.");
        progress?.Report(new DownloadProgress(bytes.Length, bytes.Length));
        if (!string.Equals(Sha256(bytes), meta.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("The Cabinet copy does not match its description.");
        CabinetPackage.Read(bytes, meta.AppId);
        // Freshness is the relay's call, but a copy without the main depot's key is never worth offering.
        if (!Coverage(bytes, await DepotsAsync(meta.AppId, ct).ConfigureAwait(false)).Usable)
            throw new InvalidDataException("The Cabinet copy has no key for the game's main depot.");
        return Stage(bytes, meta.AppId);
    }

    /// <summary>A package's key coverage; a package without a readable lua covers nothing it can vouch for.</summary>
    private static KeyCoverage Coverage(byte[] package, AppDepotInfo? depots) =>
        LuaKeyCoverage.ReadLua(package) is { } lua ? LuaKeyCoverage.Check(lua, depots)
            : new KeyCoverage(KeyCoverageVerdict.MissingMain, null, []);

    /// <summary>Steam's current depot info, or null when it is unavailable or slow. Never cancels the shared fetch.</summary>
    private async Task<AppDepotInfo?> DepotsAsync(long appId, CancellationToken ct)
    {
        try { return await _currentDepots(appId, CancellationToken.None).WaitAsync(DepotInfoWait, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The bytes to share for a staged download, read BEFORE the install consumes the file. A zip is
    /// shared as downloaded; a bare lua is wrapped in a one-entry zip. Null when there is nothing a
    /// cache could serve (no single lua, too large, unreadable).
    /// </summary>
    public byte[]? ReadPackage(string stagedPath, long appId)
    {
        try
        {
            ManifestContentPolicy.EnsureNoReparsePoints(stagedPath);
            byte[] bytes;
            using (var stream = File.OpenRead(stagedPath))
                bytes = ManifestContentPolicy.ReadBounded(stream, CabinetLuaCacheClient.MaxPackageBytes);
            byte[] package = IsZip(bytes) ? bytes : WrapLua(bytes, LuaEntryName(Path.GetFileName(stagedPath), appId));
            return CountLua(package) == 1 ? package : null;
        }
        catch (Exception ex)
        {
            _log.LogInformation("Cabinet lua cache: {AppId} cannot be shared ({Error})", appId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Upload in the background. Failures are logged and never reach the download.</summary>
    public Task ShareInBackground(long appId, byte[] package, string source) =>
        Task.Run(() => ShareAsync(appId, package, source, CancellationToken.None));

    /// <summary>
    /// Share a package only when it is current by the same rule a lookup applies, so a source serving an
    /// older build cannot replace a newer shared copy. Without depot info nothing is shared.
    /// </summary>
    internal async Task ShareAsync(long appId, byte[] package, string source, CancellationToken ct)
    {
        try
        {
            if (!_client.IsConnected || BackingOff) return;
            CabinetPackage contents;
            try { contents = CabinetPackage.Read(package, appId); }
            catch (Exception ex)
            {
                _log.LogInformation("Cabinet lua cache: {AppId} is not shared ({Error})", appId, ex.GetType().Name);
                return;
            }
            var depots = await DepotsAsync(appId, ct).ConfigureAwait(false);
            if (!contents.IsCurrent(depots))
            {
                _log.LogInformation("Cabinet lua cache: {AppId} is not shared ({Reason})", appId,
                    depots is null ? "Steam depot info unavailable" : "not Steam's current build");
                return;
            }
            // Every user tries the cache first, so only a copy with a key for every depot Steam installs
            // is offered. A pin-free lua is always "current", which made this the only real gate for one.
            if (Coverage(package, depots) is { Shareable: false } coverage)
            {
                _log.LogInformation("Cabinet lua cache: {AppId} is not shared (keys missing for {Count} depot(s): {Depots})",
                    appId, coverage.Missing.Count, string.Join(", ", coverage.Missing.Take(5)));
                return;
            }
            await _client.UploadAsync(appId, package, Sha256(package), source, ct).ConfigureAwait(false);
            _log.LogInformation("Cabinet lua cache: shared {AppId} ({Bytes} bytes)", appId, package.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cabinet lua cache: sharing {AppId} failed ({Error}: {Message})", appId, ex.GetType().Name, ex.Message);
        }
    }

    private CabinetCachedFile Stage(byte[] package, long appId)
    {
        ManifestContentPolicy.EnsureNoReparsePoints(_stagingFolder);
        string directory = Path.Combine(_stagingFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string name = $"{appId}.zip";
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, package);
        return new CabinetCachedFile(path, name);
    }

    internal static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsZip(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4;

    private static int CountLua(byte[] package)
    {
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return archive.Entries.Count(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Keep a staged "&lt;appid&gt;_&lt;buildid&gt;.lua" name: the build id in it is what keeps the pins on
    /// install (see LuaInstaller.BuildIdFromFileName). Anything else becomes "&lt;appid&gt;.lua".
    /// </summary>
    internal static string LuaEntryName(string stagedName, long appId) =>
        Regex.IsMatch(stagedName, @"\A[0-9]{1,10}(_[0-9]{1,20})?\.lua\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? stagedName : $"{appId}.lua";

    /// <summary>A deterministic one-entry zip, so sharing the same lua twice uploads the same bytes.</summary>
    internal static byte[] WrapLua(byte[] lua, string entryName)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(lua);
        }
        return output.ToArray();
    }
}
