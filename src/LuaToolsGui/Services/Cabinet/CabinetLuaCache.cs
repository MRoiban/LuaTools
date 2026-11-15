using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuaToolsGui.Services.Cabinet;

/// <summary>A manifest package served from the Cabinet server's shared cache rather than the chosen source.</summary>
public sealed record CabinetCachedFile(string FilePath, string FileName) : DownloadedFile(FilePath, FileName);

/// <summary>The decisions of the Cabinet lua cache, kept free of I/O so they can be tested directly.</summary>
public static class CabinetLuaCachePolicy
{
    /// <summary>
    /// Whether a successfully installed package is shared with the Cabinet server. Uploading is
    /// automatic whenever Cabinet is connected, except for a package fetched with the user's own
    /// Hubcap key (<paramref name="needsKey"/>; re-sharing those is a separate decision) and a package
    /// that came from the cache in the first place.
    /// </summary>
    public static bool ShouldUpload(bool connected, bool needsKey, bool fromCabinet) =>
        connected && !needsKey && !fromCabinet;

    /// <summary>
    /// Whether the cached package can stand in for the chosen source: the bytes are the ones the server
    /// described and every pin matches what Steam ships now (see <see cref="ManifestPins.AreCurrent"/>).
    /// </summary>
    public static bool IsUsable(CabinetLuaMeta meta, string actualSha256, IReadOnlyDictionary<long, string> packagePins, AppDepotInfo? depots) =>
        string.Equals(meta.Sha256, actualSha256, StringComparison.Ordinal)
        && ManifestPins.AreCurrent(meta.Pins, depots)
        && ManifestPins.AreCurrent(packagePins, depots);
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
/// <see cref="ManifestContentPolicy"/> checks as any other download.
/// </remarks>
public class CabinetLuaCache
{
    private readonly ICabinetLuaCacheClient _client;
    private readonly Func<long, CancellationToken, Task<AppDepotInfo?>> _currentDepots;
    private readonly ILogger _log;
    private readonly string _stagingFolder;

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

    public bool IsConnected => _client.IsConnected;

    /// <summary>
    /// A staged copy of the server's current package for this game, or null to use the normal source.
    /// Throws only <see cref="OperationCanceledException"/> for the caller's own cancellation.
    /// </summary>
    public async Task<CabinetCachedFile?> TryGetCurrentAsync(long appId, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!_client.IsConnected) return null;
        try
        {
            var meta = await _client.GetMetaAsync(appId, ct).ConfigureAwait(false);
            if (meta is null) { _log.LogDebug("Cabinet lua cache: nothing cached for {AppId}", appId); return null; }

            var depots = await _currentDepots(appId, ct).ConfigureAwait(false);
            if (!ManifestPins.AreCurrent(meta.Pins, depots))
            {
                _log.LogInformation("Cabinet lua cache: copy of {AppId} is not current ({Reason}); using the normal source",
                    appId, depots is null ? "Steam depot info unavailable" : "older build");
                return null;
            }

            progress?.Report(new DownloadProgress(0, meta.Size));
            byte[]? package = await _client.DownloadAsync(appId, meta.Sha256, ct).ConfigureAwait(false);
            if (package is null) return null; // replaced and evicted since the meta was read
            progress?.Report(new DownloadProgress(package.Length, package.Length));

            // The meta is the server's word; the bytes and their own lua are what get installed.
            if (!CabinetLuaCachePolicy.IsUsable(meta, Sha256(package), PackagePins(package, appId), depots))
            {
                _log.LogWarning("Cabinet lua cache: copy of {AppId} does not match its description; using the normal source", appId);
                return null;
            }

            var file = Stage(package, appId);
            _log.LogInformation("Cabinet lua cache: installing the shared copy of {AppId} from {Source}", appId, meta.Source);
            return file;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogInformation("Cabinet lua cache: lookup for {AppId} failed ({Error}); using the normal source", appId, ex.GetType().Name);
            return null;
        }
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

    internal async Task ShareAsync(long appId, byte[] package, string source, CancellationToken ct)
    {
        try
        {
            if (!_client.IsConnected) return;
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

    /// <summary>The package's own pins, as LuaFileParser reads them. Invalid packages throw.</summary>
    internal static IReadOnlyDictionary<long, string> PackagePins(byte[] package, long appId)
    {
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        // The install repeats this; refusing here means an invalid copy falls back instead of failing.
        ManifestContentPolicy.ValidateArchive(archive, manifestPackage: true, expectedAppId: appId);
        var lua = archive.Entries.Single(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
        using var stream = lua.Open();
        string text = System.Text.Encoding.UTF8.GetString(ManifestContentPolicy.ReadBounded(stream, ManifestContentPolicy.MaxLuaBytes)).TrimStart('﻿');
        var parsed = LuaFileParser.ParseText(text, appId) ?? throw new InvalidDataException("Unreadable lua.");
        return ManifestPins.Of(parsed);
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
