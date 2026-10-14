using System.IO;
using System.Net.Http;

namespace LuaToolsGui.Services.Downloads;

/// <summary>
/// The shared response→file copy loop for manifest and Denuvo-fix downloads, plus the interim staging
/// folder they land in.
/// </summary>
/// <remarks>
/// This body previously existed twice, byte for byte: <c>LuaToolsApiClient.SaveResponseAsync</c> and
/// <c>HubcapService.SaveResponseAsync</c>, each with its own private copy of the staging path constant.
/// Both now delegate here, so the progress contract and the filename sanitising live in one place.
///
/// Staging is under %TEMP% so downloads never pollute the user's Downloads folder. Files are deleted by
/// the caller once installed; <see cref="SweepStale"/> catches whatever a crash or a declined overwrite
/// confirmation left behind.
/// </remarks>
internal static class HttpFileDownloader
{
    /// <summary>Interim staging destination. Downloads land here, then are deleted once installed.</summary>
    public static readonly string StagingFolder =
        Path.Combine(Path.GetTempPath(), AppConfig.DataDirectoryName, "downloads");

    /// <summary>Stream the response body to a staged file, reporting byte counts as it goes.</summary>
    public static async Task<DownloadedFile> SaveResponseAsync(
        HttpResponseMessage res, string fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        const long maxBytes = 512L * 1024 * 1024;
        string fileName = res.Content.Headers.ContentDisposition?.FileNameStar
            ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? fallbackName;
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".."
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains('/') || fileName.Contains('\\') || Path.IsPathRooted(fileName)
            || fileName.EndsWith('.') || fileName.EndsWith(' '))
            throw new InvalidDataException("Invalid download filename.");

        ManifestContentPolicy.ValidateRelativePath(fileName);
        ManifestContentPolicy.EnsureNoReparsePoints(StagingFolder);
        Directory.CreateDirectory(StagingFolder);
        // Per-download directory prevents one response from overwriting another staged file.
        string directory = Path.Combine(StagingFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, fileName);
        ManifestContentPolicy.EnsureNoReparsePoints(filePath);
        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        ct = bodyTimeout.Token;
        try
        {
            long? total = res.Content.Headers.ContentLength;
            if (total > maxBytes) throw new InvalidDataException("Download exceeds the size limit.");
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > maxBytes) throw new InvalidDataException("Download exceeds the size limit.");
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                progress?.Report(new DownloadProgress(written, total));
            }
            if (total.HasValue && total.Value != written) throw new InvalidDataException("Incomplete download.");
            progress?.Report(new DownloadProgress(written, total ?? written));
            return new DownloadedFile(filePath, fileName);
        }
        catch
        {
            try { File.Delete(filePath); Directory.Delete(directory); } catch { }
            throw;
        }
        finally { res.Dispose(); }
    }

    /// <summary>
    /// Best-effort delete of staged files older than a day. Called once at startup: a download that was
    /// interrupted by a crash, or whose overwrite confirmation was never answered, leaves its zip behind.
    /// </summary>
    public static void SweepStale()
    {
        try
        {
            if (!Directory.Exists(StagingFolder)) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string path in Directory.EnumerateFiles(StagingFolder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch { /* in use or gone. Next startup gets it */ }
            }
        }
        catch { /* best effort */ }
    }
}
