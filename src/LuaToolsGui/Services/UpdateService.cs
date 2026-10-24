using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>Fork-only portable updates. Signed bytes authorize execution, never server metadata.</summary>
public sealed class UpdateService
{
    internal const string Repository = "MRoiban/LuaTools";
    internal const long MaxPackageBytes = 256L * 1024 * 1024;
    private static readonly string DefaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConfig.DataDirectoryName, "signed-updates");
    private static readonly HttpClient DefaultHttp = AppHttp.Create(TimeSpan.FromMinutes(5), responseBudget: HttpResponseBudget.Download);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly Version _version;
    private readonly string? _testKey;
    public UpdateService() : this(DefaultHttp, DefaultRoot, CurrentVersion, null) { }
    internal UpdateService(HttpClient http, string root, Version version, string? testKey)
    { _http = http; _root = root; _version = version; _testKey = testKey; }
    private string TrustKey => _testKey ?? PublicKey();
    private string? _pending;
    public event Action? UpdateReady;
    public bool HasStagedUpdate => _pending is not null;
    internal static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
    internal sealed record Manifest(string Repository, string Version, string Commit, string Asset, long Size, string Sha256);

    private static string PublicKey()
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("LuaToolsGui.release-signing-public.pem")
            ?? throw new InvalidDataException("No release trust key.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static Manifest VerifyManifest(byte[] bytes, byte[] signature, string publicKey, Version minimum)
    {
        if (bytes.Length is 0 or > 16384 || signature.Length != 64) throw new InvalidDataException("Invalid signed manifest size.");
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKey);
        if (key.KeySize != 256 || !key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Untrusted release signature.");
        var m = JsonSerializer.Deserialize<Manifest>(bytes) ?? throw new InvalidDataException("Missing manifest.");
        if (m.Repository != Repository || !Version.TryParse(m.Version, out var v)
            || v.Major != 1 || v.Minor != 4 || v.Build < 1 || v.Revision != -1
            || v <= minimum || m.Asset != $"LuaTools-{v}-win-x64.zip" || m.Size is <= 0 or > MaxPackageBytes
            || m.Commit is null || !System.Text.RegularExpressions.Regex.IsMatch(m.Commit, "\\A[0-9a-f]{40}\\z")
            || m.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(m.Sha256, "\\A[0-9a-f]{64}\\z"))
            throw new InvalidDataException("Wrong repository, invalid release, or rollback.");
        return m;
    }

    internal static Manifest VerifyPackage(string directory, string publicKey, Version minimum)
    {
        CatalogDataPolicy.CheckLocal(directory);
        var m = VerifyManifest(ReadBounded(Path.Combine(directory, "release.json"), 16384),
            ReadBounded(Path.Combine(directory, "release.sig"), 64), publicKey, minimum);
        CatalogDataPolicy.CheckLocal(Path.Combine(directory, "package.zip"));
        using var zip = new FileStream(Path.Combine(directory, "package.zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (zip.Length != m.Size || !Convert.ToHexString(SHA256.HashData(zip)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Release package hash/size mismatch.");
        return m;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        CatalogDataPolicy.CheckLocal(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Oversized release metadata.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    internal static void ExtractVerified(string directory, string destination, string publicKey, Version minimum)
    {
        CatalogDataPolicy.CheckLocal(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new InvalidDataException("Extraction target already exists.");
        var m = VerifyPackage(directory, publicKey, minimum);
        CatalogDataPolicy.CheckLocal(Path.Combine(directory, "package.zip"));
        // Hash and extraction share a write-locked handle: cached ZIP replacement cannot alter extracted bytes.
        using var file = new FileStream(Path.Combine(directory, "package.zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != m.Size || !Convert.ToHexString(SHA256.HashData(file)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Release changed before extraction.");
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > 4096) throw new InvalidDataException("Too many release files.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Contains("//", StringComparison.Ordinal)
                || name.TrimEnd('/').Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')
                    || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || System.Text.RegularExpressions.Regex.IsMatch(p, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])([.]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                || !seen.Add(name.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe release archive path.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 1024L * 1024 * 1024) throw new InvalidDataException("Expanded release too large.");
        }
        if (!seen.Contains("LuaTools.exe")) throw new InvalidDataException("Release executable missing.");
        CatalogDataPolicy.CheckLocal(destination);
        Directory.CreateDirectory(destination);
        zip.ExtractToDirectory(destination, overwriteFiles: false);
    }

    /// <summary>How often a long-running (tray) instance looks for a newer signed release. A release
    /// lands on every merge, so this stays short; each check is two sub-kilobyte downloads.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    /// <summary>Version of the staged (verified, not yet approved) update, if any.</summary>
    public Version? StagedVersion { get; private set; }
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    /// <summary>Check unless one finished within <paramref name="maxAge"/> (e.g. on opening the window).</summary>
    public Task CheckIfDueAsync(TimeSpan maxAge) =>
        DateTimeOffset.UtcNow - _lastCheck < maxAge ? Task.CompletedTask : CheckAndStageAsync(announceStaged: false);

    /// <summary>Stage the latest signed release when it is newer than this build, the approved package
    /// and any already-staged update. <paramref name="announceStaged"/> re-raises the prompt when the
    /// staged update is still the latest; a newly staged release is always announced.</summary>
    public async Task<UpdateCheckResult> CheckAndStageAsync(bool announceStaged = true)
    {
        if (_version.Major != 1 || _version.Minor != 4 || _version.Build < 1) return UpdateCheckResult.Unsupported;
        await _gate.WaitAsync();
        string? temporary = null;
        try
        {
            CatalogDataPolicy.CheckLocal(_root);
            Directory.CreateDirectory(_root);
            CleanupRuns();
            CleanupPackages();
            temporary = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            const string latest = "https://github.com/" + Repository + "/releases/latest/download/";
            await Download(latest + "release.json", Path.Combine(temporary, "release.json"), 16384);
            await Download(latest + "release.sig", Path.Combine(temporary, "release.sig"), 64);
            var minimum = _version;
            var active = ActiveDirectory();
            if (active is not null)
            {
                var approved = VerifyPackage(active, TrustKey, new Version(1, 3));
                if (Version.Parse(approved.Version) > minimum) minimum = Version.Parse(approved.Version);
            }
            // Signature and format first; versions are compared here so "nothing newer" is not a failure.
            var m = VerifyManifest(ReadBounded(Path.Combine(temporary, "release.json"), 16384), ReadBounded(Path.Combine(temporary, "release.sig"), 64), TrustKey, new Version(1, 4, 0));
            var offered = Version.Parse(m.Version);
            if (StagedVersion is not null && offered <= StagedVersion)
            {
                if (announceStaged) UpdateReady?.Invoke();
                return UpdateCheckResult.Staged;
            }
            if (offered <= minimum) return UpdateCheckResult.UpToDate;
            await Download($"https://github.com/{Repository}/releases/download/v{m.Version}/{m.Asset}", Path.Combine(temporary, "package.zip"), m.Size);
            VerifyPackage(temporary, TrustKey, minimum);
            // A newer release supersedes an older staged one; CleanupPackages removes the old folder later.
            _pending = temporary;
            StagedVersion = offered;
            temporary = null;
            UpdateReady?.Invoke();
            return UpdateCheckResult.Staged;
        }
        catch { return UpdateCheckResult.Unavailable; /* Offline, unsigned and stale releases leave the current app untouched. */ }
        finally
        {
            _lastCheck = DateTimeOffset.UtcNow;
            if (temporary is not null) { try { Directory.Delete(temporary, true); } catch { } }
            _gate.Release();
        }
    }

    private async Task Download(string url, string path, long maximum)
    {
        CatalogDataPolicy.CheckLocal(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        new HttpResponseBudget(maximum, TimeSpan.FromMinutes(5)).Apply(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[65536];
        long count = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) != 0)
        {
            count += read;
            if (count > maximum) throw new InvalidDataException("Oversized release response.");
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private string? ActiveDirectory()
    {
        var pointer = Path.Combine(_root, "approved.txt");
        CatalogDataPolicy.CheckLocal(pointer);
        if (!File.Exists(pointer)) return null;
        var id = System.Text.Encoding.UTF8.GetString(ReadBounded(pointer, 64)).Trim();
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid update pointer.");
        return Path.Combine(_root, id);
    }

    private bool Launch(string directory, bool waitForCurrent)
    {
        var destination = Path.Combine(_root, "runs", Guid.NewGuid().ToString("N"));
        ExtractVerified(directory, destination, TrustKey, _version);
        var start = new ProcessStartInfo(Path.Combine(destination, "LuaTools.exe")) { UseShellExecute = false, WorkingDirectory = destination };
        if (!waitForCurrent && Program.StartMinimized) start.ArgumentList.Add("--minimized");
        if (waitForCurrent)
        {
            start.ArgumentList.Add("--fork-update-wait");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Process.Start(start) is not null;
    }

    public void ApplyAndRestart(string[]? restartArgs = null)
    {
        if (_pending is null || Cabinet.CabinetService.HasActiveHelpers) return;
        var pointer = Path.Combine(_root, "approved.txt");
        CatalogDataPolicy.CheckLocal(pointer);
        var previous = File.Exists(pointer) ? ReadBounded(pointer, 64) : null;
        WritePointer(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(_pending)));
        try
        {
            if (!Launch(_pending, waitForCurrent: true)) throw new IOException("Update launch failed.");
        }
        catch
        {
            if (previous is null) File.Delete(pointer); else WritePointer(previous);
            throw;
        }
        System.Windows.Application.Current?.Shutdown();
    }

    private void WritePointer(byte[] bytes)
    {
        var pointer = Path.Combine(_root, "approved.txt");
        var temporary = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        CatalogDataPolicy.CheckLocal(pointer);
        CatalogDataPolicy.CheckLocal(temporary);
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(flushToDisk: true); }
        File.Move(temporary, pointer, overwrite: true);
    }

    private void CleanupRuns() => CleanupDirectories(Path.Combine(_root, "runs"), 2,
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), BundledPlugin.LauncherDirectory);
    private void CleanupPackages() => CleanupDirectories(_root, 2, ActiveDirectory(), _pending);

    private static void CleanupDirectories(string root, int retain, params string?[] keep)
    {
        CatalogDataPolicy.CheckLocal(root);
        if (!Directory.Exists(root)) return;
        var candidates = new DirectoryInfo(root).EnumerateDirectories()
            .Where(d => Guid.TryParseExact(d.Name, "N", out _))
            .OrderByDescending(d => d.LastWriteTimeUtc).ToArray();
        foreach (var candidate in candidates.Skip(retain))
        {
            if (keep.Any(k => string.Equals(k, candidate.FullName, StringComparison.OrdinalIgnoreCase))) continue;
            try { DeleteTree(candidate.FullName); } catch { /* Active executable files stay for the next launch. */ }
        }
    }

    private static void DeleteTree(string path)
    {
        CatalogDataPolicy.CheckLocal(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            CatalogDataPolicy.CheckLocal(entry);
            if (Directory.Exists(entry)) DeleteTree(entry); else File.Delete(entry);
        }
        Directory.Delete(path);
    }

    public void ApplyOnExit() { }

    internal static bool TryLaunchApproved() => new UpdateService().TryLaunchApprovedCore();

    private bool TryLaunchApprovedCore()
    {
        try
        {
            CleanupRuns();
            var active = ActiveDirectory();
            return active is not null && Launch(active, waitForCurrent: false);
        }
        catch { return false; }
    }
}

public enum UpdateCheckResult { UpToDate, Staged, Unavailable, Unsupported }
