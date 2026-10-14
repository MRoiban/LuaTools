using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;
using Velopack.Windows;

namespace LuaToolsGui.Services;

/// <summary>The outcome of making SteamAutoCrack runnable, so callers can say what actually happened.</summary>
public enum SacPrepareResult
{
    Ready,
    /// <summary>The user declined the .NET runtime installer's elevation prompt. Not an error.</summary>
    RuntimeDeclined,
    /// <summary>The runtime installed but Windows wants a reboot before it can be used.</summary>
    RuntimeNeedsRestart,
    RuntimeFailed,
}

/// <summary>Prepares and runs the original tool only after its separate opt-in and explicit user action.
/// Consent receipts detect bundle changes and do not establish an independent audit.</summary>
public class SteamAutoCrackService(
    GithubProxy gh,
    CacheService cache,
    ILogger<SteamAutoCrackService> log, SettingsService? settings = null)
{
    private bool IsEnabled(string component) => settings?.IsExecutableWorkflowEnabled(component) == true;

    private static readonly string ToolDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirectoryName, "steamautocrack");

    private static string ExePath => Path.Combine(ToolDir, "SteamAutoCrack.exe");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>How long an up-to-date check is trusted before we ask GitHub again.</summary>
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private static bool CheckedRecently(long lastMs) =>
        lastMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < (long)ToolCheckInterval.TotalMilliseconds;

    // ── Runtime ──────────────────────────────────────────────────────

    /// <summary>
    /// The .NET 10 Desktop runtime their exe needs, named for this machine's architecture.
    /// </summary>
    /// <remarks>
    /// Velopack's static fields stop at DOTNET8, but GetRuntimeByName parses the id generically, so
    /// "net10-x64-desktop" resolves fine on 1.2.0 without an upgrade. Their asset carries no RID in its
    /// name, so the OS architecture is the honest guess at which runtime it wants.
    /// </remarks>
    private static string RuntimeId => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X86 => "net10-x86-desktop",
        Architecture.Arm64 => "net10-arm64-desktop",
        _ => "net10-x64-desktop",
    };

    // Velopack marks Runtimes [Obsolete] ("no longer used by Velopack, and does not represent the
    // current supported runtimes" - docs.velopack.io/packaging/bootstrapping). It is deprecated because
    // Velopack now bootstraps runtimes through its own installer rather than at app runtime, which is
    // not what we need: this is an on-demand install for a THIRD-PARTY exe, long after our own setup ran.
    //
    // The API still works on 1.2.0 - verified live against .NET 10, which has no static field:
    //   GetRuntimeByName("net10-x64-desktop") -> ".NET 10 WindowsDesktop (x64)"
    //   CheckIsInstalled() -> true on a machine with WindowsDesktop.App 10.0.9
    //   GetDownloadUrl()   -> builds.dotnet.microsoft.com/.../windowsdesktop-runtime-10.0.11-win-x64.exe
    //
    // If a future Velopack drops these types, the replacement is doing it by hand: query
    // dotnetcli.blob.core.windows.net for the latest 10.0 release, download the desktop runtime exe and
    // run it with /install /quiet-style switches. Detection can fall back to parsing `dotnet
    // --list-runtimes` for a Microsoft.WindowsDesktop.App 10.x entry.
#pragma warning disable CS0618 // deliberate: see above

    /// <summary>
    /// Is the runtime their exe needs already present? Local and cheap - no network, no install.
    /// </summary>
    /// <remarks>
    /// Presence of the major version is all their exe needs; patch-level servicing is Windows Update's
    /// job, so this deliberately does not chase 10.0.x updates.
    /// </remarks>
    public async Task<bool> RuntimeInstalledAsync()
    {
        try
        {
            var runtime = Runtimes.GetRuntimeByName(RuntimeId);
            return runtime is not null && await runtime.CheckIsInstalled().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Checking for the .NET runtime failed");
            return false;
        }
    }

    /// <summary>Check for an already installed runtime; never download or invoke a runtime installer.</summary>
    public async Task<SacPrepareResult> EnsureRuntimeAsync(
        IProgress<double?>? progress, CancellationToken ct = default)
    {
        UserAuthorizedDependencies.RequireEnabled("SteamAutoCrack", IsEnabled);
        ct.ThrowIfCancellationRequested();
        if (await RuntimeInstalledAsync()) return SacPrepareResult.Ready;
        throw new InvalidOperationException("Runtime installation is unavailable in this build.");
    }
#pragma warning restore CS0618

    // ── Tool ─────────────────────────────────────────────────────────

    /// <summary>Prepare the original dependency bundle only for an explicit action with this tool enabled.
    /// User consent does not establish an independent upstream audit.</summary>
    public async Task<string?> EnsureToolAsync(
        IProgress<DownloadProgress>? progress, bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await DependencyQuarantine.PrepareToolAsync(gh, "SteamAutoCrack", AppConfig.SteamAutoCrackRepo,
                ToolDir, ExePath, a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), null, ct, downloadProgress: progress, enabled: IsEnabled, explicitUserAction: true);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Open it immediately when nothing needs downloading. False means the caller should run the full
    /// job (which installs the runtime and/or the tool).
    /// </summary>
    /// <remarks>
    /// This exists so a launch that transfers zero bytes never touches the queue: a queued job would
    /// flash a progress row and, worse, leave a permanent entry in the user's download history next to
    /// their real game downloads.
    ///
    /// The runtime check is not redundant with <c>File.Exists</c>. A framework-dependent exe with no
    /// runtime still STARTS, and then shows Windows' own "you must install .NET" dialog - so skipping it
    /// would hand the user that dialog instead of our install flow.
    /// </remarks>
    public async Task<bool> TryLaunchIfReadyAsync(CancellationToken ct = default)
    {
        if (!IsEnabled("SteamAutoCrack") || !File.Exists(ExePath)) return false;
        if (!await RuntimeInstalledAsync()) return false;
        return Launch();
    }

    /// <summary>Throttled latest-release metadata probe. Never downloads, installs or launches an artifact.</summary>
    public async Task<bool> IsUpdateAvailableAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ExePath)) return false;
        if (CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return false;

        await _gate.WaitAsync(ct);
        try
        {
            if (CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return false; // won the race

            void RecordAttempt() =>
                cache.SteamAutoCrackCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bool available = await ProbeLatestReleaseAsync(gh, cache.SteamAutoCrackVersion, ct);
            RecordAttempt();
            return available;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Checking for a SteamAutoCrack update failed");
            cache.SteamAutoCrackCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return false;
        }
        finally { _gate.Release(); }
    }

    internal static async Task<bool> ProbeLatestReleaseAsync(GithubProxy github, string? installedVersion, CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{AppConfig.SteamAutoCrackRepo}/releases/latest";
        using var response = await github.SendAsync(url, ct);
        if (response is null || !response.IsSuccessStatusCode) return false;
        var release = JsonSerializer.Deserialize<GithubRelease>(await response.Content.ReadAsStringAsync(ct), JsonOpts);
        return !string.IsNullOrEmpty(release?.TagName)
            && !string.Equals(release.TagName, installedVersion, StringComparison.Ordinal);
    }

    /// <summary>Open SteamAutoCrack's window. Fire-and-forget; we don't wait for it to exit.</summary>
    public bool Launch()
    {
        if (!File.Exists(ExePath)) return false;
        try
        {
            // WorkingDirectory matters: their exe looks for Goldberg/ and TEMP/ next to itself.
            UserAuthorizedDependencies.RequireDirectory("SteamAutoCrack", ToolDir, IsEnabled);
            if (!RuntimeInstalledAsync().GetAwaiter().GetResult()) return false;
            Process.Start(new ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = ToolDir,
            });
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Launching SteamAutoCrack failed");
            return false;
        }
    }
}
