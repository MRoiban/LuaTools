using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services;

/// <summary>Acquires remote artifacts as inert data. Quarantine is never an execution/install path.
/// Cached receipts and publisher digests establish cache identity only, never approval.</summary>
internal static class DependencyQuarantine
{
    private const long MaxBytes = 512L * 1024 * 1024;
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirectoryName, "dependency-quarantine");

    public static async Task<string?> PrepareToolAsync(GithubProxy gh, string component, string repo,
        string toolDirectory, string entryPath, Func<GithubAsset, bool> select,
        IProgress<double?>? progress, CancellationToken ct, string? quarantineRoot = null,
        IProgress<DownloadProgress>? downloadProgress = null, Func<string, bool>? enabled = null,
        bool explicitUserAction = false, bool archive = true)
    {
        ct.ThrowIfCancellationRequested();
        enabled ??= _ => false;
        UserAuthorizedDependencies.RequireEnabled(component, enabled);
        if (!explicitUserAction) throw new InvalidOperationException($"{component} requires an explicit action.");
        if (File.Exists(Path.Combine(toolDirectory, UserAuthorizedDependencies.ReceiptFileName)))
        {
            // A changed recorded bundle is rejected; existing files cannot silently replace the
            // particular bytes the explicit enabled action authorized.
            UserAuthorizedDependencies.RequireDirectory(component, toolDirectory, enabled);
            UserAuthorizedDependencies.RequireFile(component, entryPath, enabled);
            return entryPath;
        }
        string? acquired = await AcquireLatestAsync(gh, component, repo, select, progress, ct, quarantineRoot, downloadProgress);
        if (acquired is null) return null;
        string entryRelative = Path.GetRelativePath(toolDirectory, entryPath);
        if (archive) UserAuthorizedDependencies.InstallArchive(component, acquired, toolDirectory, entryRelative, enabled, explicitUserAction);
        else UserAuthorizedDependencies.InstallFile(component, acquired, toolDirectory, entryRelative, enabled, explicitUserAction);
        UserAuthorizedDependencies.RequireDirectory(component, toolDirectory, enabled);
        return entryPath;
    }

    public static async Task<string?> AcquireLatestAsync(GithubProxy gh, string component, string repo,
        Func<GithubAsset, bool> select, IProgress<double?>? progress, CancellationToken ct, string? root = null,
        IProgress<DownloadProgress>? downloadProgress = null)
    {
        ValidateRepo(repo);
        using var response = await gh.SendAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        if (response is null || !response.IsSuccessStatusCode) return null;
        var release = JsonSerializer.Deserialize<GithubRelease>(await response.Content.ReadAsStringAsync(ct), JsonOptions);
        if (release?.Assets is null || release.Assets.Any(a => a is null || string.IsNullOrEmpty(a.Name)))
            throw new InvalidDataException("Invalid release metadata.");
        var asset = release.Assets.FirstOrDefault(select);
        if (asset is null) return null;
        IProgress<double?>? sink = downloadProgress is null ? progress : new ProgressRelay<double?>(fraction =>
            downloadProgress.Report(new DownloadProgress((long)((fraction ?? 0) * asset.Size), asset.Size > 0 ? asset.Size : null)));
        return await AcquireAssetAsync(gh, component, repo, asset, sink, ct, root);
    }

    public static async Task<string> AcquireAssetAsync(GithubProxy gh, string component, string repo,
        GithubAsset asset, IProgress<double?>? progress, CancellationToken ct, string? root = null)
    {
        ValidateRepo(repo);
        ValidateAsset(repo, asset);
        if (component.Length is 0 or > 80 || !component.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new InvalidDataException("Invalid dependency component.");
        await CacheGate.WaitAsync(ct);
        try
        {
            string directory = Path.Combine(root ?? DefaultRoot, component);
            ManifestContentPolicy.EnsureNoReparsePoints(directory);
            Directory.CreateDirectory(directory);
            // Remote names are validated but never used as filesystem paths. Non-executable suffix also
            // prevents the quarantine from looking like an installed tool to legacy File.Exists checks.
            string slot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asset.Name)));
            string path = Path.Combine(directory, slot + ".download");
            string receipt = path + ".receipt";
            ManifestContentPolicy.EnsureNoReparsePoints(path);
            ManifestContentPolicy.EnsureNoReparsePoints(receipt);
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                asset.DownloadUrl + "\n" + asset.Digest + "\n" + asset.Size)));
            if (File.Exists(path) && File.Exists(receipt) && new FileInfo(receipt).Length <= 256
                && new FileInfo(path).Length <= MaxBytes && (asset.Size == 0 || new FileInfo(path).Length == asset.Size)
                && ReadReceipt(receipt) == identity + "\n" + HashCachedFile(path))
                return path;

            TrimCache(directory, path, asset.Size > 0 ? asset.Size : MaxBytes);
            string partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                await gh.DownloadAsync(asset.DownloadUrl, partial, progress, ct);
                if (asset.Size > 0 && new FileInfo(partial).Length != asset.Size)
                    throw new InvalidDataException("Dependency download size does not match release metadata.");
                ManifestContentPolicy.EnsureNoReparsePoints(path);
                File.Move(partial, path, overwrite: true);
                ManifestContentPolicy.EnsureNoReparsePoints(receipt);
                File.WriteAllText(receipt, identity + "\n" + HashCachedFile(path));
                return path;
            }
            finally { try { File.Delete(partial); } catch { } }
        }
        finally { CacheGate.Release(); }
    }

    private static string ReadReceipt(string path)
    {
        using var input = File.OpenRead(path);
        return Encoding.UTF8.GetString(ManifestContentPolicy.ReadBounded(input, 256));
    }

    private static string HashCachedFile(string path)
    {
        using var input = File.OpenRead(path); // FileShare.Read prevents concurrent writes on Windows.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            readTotal += read;
            if (readTotal > MaxBytes) throw new InvalidDataException("Dependency cache exceeds the size limit.");
            hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void TrimCache(string directory, string retainedPath, long reserve)
    {
        var files = Directory.EnumerateFiles(directory, "*.download")
            .Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).ToList();
        long total = files.Where(file => file.FullName != retainedPath).Sum(file => file.Length);
        int count = files.Count;
        foreach (var file in files)
        {
            if (file.FullName == retainedPath) continue;
            if (total + reserve <= MaxBytes && count < 8) break;
            ManifestContentPolicy.EnsureNoReparsePoints(file.FullName);
            ManifestContentPolicy.EnsureNoReparsePoints(file.FullName + ".receipt");
            total -= file.Length;
            count--;
            File.Delete(file.FullName);
            File.Delete(file.FullName + ".receipt");
        }
    }

    public static void ValidateAsset(string repo, GithubAsset asset)
    {
        if (asset is null || string.IsNullOrEmpty(asset.Name) || asset.Name.Length > 200 || asset.Name is "." or ".."
            || asset.Name.Contains('/') || asset.Name.Contains('\\') || asset.Name.EndsWith('.') || asset.Name.EndsWith(' ')
            || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || asset.Size < 0 || asset.Size > MaxBytes)
            throw new InvalidDataException("Invalid release asset metadata.");
        ManifestContentPolicy.ValidateRelativePath(asset.Name);
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "github.com" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.StartsWith("/" + repo + "/releases/download/", StringComparison.OrdinalIgnoreCase)
            || Uri.UnescapeDataString(uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..]) != asset.Name)
            throw new InvalidDataException("Unexpected release asset origin or path.");
    }

    private static void ValidateRepo(string repo)
    {
        string[] parts = repo.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new InvalidDataException("Invalid dependency repository.");
    }
}
