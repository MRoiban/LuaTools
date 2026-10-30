using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

public sealed record PluginStatus(bool FrontendInstalled, bool DllInstalled, bool DllMatches,
    string? InstalledTag, string? LatestTag, bool UpdateAvailable, bool MillenniumPresent, bool Offline, bool Port8080Busy);

/// <summary>Explicit installation of the source-owned plugin shipped in this application.</summary>
public class PluginInstallerService(SteamService steam, SettingsService settings, GithubProxy gh, CefInjectorService injector)
{
    private const string LoaderName = "winmm.dll";
    private const string RealName = "winmm_real.dll";
    private const string ConfigurationName = "luatools-fork-loader.path";
    private const string CdpMarkerName = ".cef-enable-remote-debugging";
    private const string CdpMarkerJunctionTarget = @"C:\LuaToolsGui-CdpMarker-Target-Does-Not-Exist";
    private const string LegacyCdpMarkerTarget = @"C:\fuckass\folder\that\shall\never\exist\die\millennium";
    private string? SteamDir => steam.EffectivePath;
    public Func<Task>? ActivatePlugin { get; set; }
    public bool MillenniumPresent => SteamDir is { } s && File.Exists(Path.Combine(s, "millennium", "lib", "millennium.dll"));

    // Kept for callers using the previous API shape. No release request is made.
    public Task<GithubRelease?> FetchLatestAsync(bool force, CancellationToken ct = default)
    {
        _ = gh; // Compatibility for existing DI and callers; plugin acquisition has no network path.
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<GithubRelease?>(new GithubRelease { TagName = BundledPlugin.Version });
    }

    public bool IsInstalledLocally() => BundledPlugin.IsActivated;

    public Task<PluginStatus> GetStatusAsync(bool force = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var receipt = BundledPlugin.ReadReceipt(BundledPlugin.ReceiptPath);
        bool active = receipt?.ActivationEnabled == true;
        bool installed = active && SteamDir is { } s && File.Exists(Path.Combine(s, LoaderName));
        bool matches = false;
        try
        {
            matches = installed && receipt?.SteamDirectory == SteamDir && SteamDir is { } dir
                && HashMatches(Path.Combine(dir, LoaderName), BundledPlugin.LoaderSha256)
                && receipt?.DllShas is { } hashes && hashes.TryGetValue(RealName, out string? realHash)
                && HashMatches(Path.Combine(dir, RealName), realHash)
                && HashMatches(Path.Combine(dir, ConfigurationName), receipt.LoaderConfigurationSha)
                && receipt.LoaderConfigurationSha == BundledPlugin.Hash(BundledPlugin.LoaderConfiguration(Environment.ProcessPath ?? ""));
        }
        catch { /* A missing build resource or unsafe installed path is an unavailable component. */ }
        return Task.FromResult(new PluginStatus(active, installed, matches, receipt?.Tag, BundledPlugin.Version,
            active && !matches, MillenniumPresent, false, false));
    }

    private static bool HashMatches(string path, string? hash)
    {
        BundledPlugin.EnsureSafePath(path);
        return hash is not null && File.Exists(path) && AssetHash.OfFile(path) == hash;
    }

    public async Task<(bool ok, string? error)> InstallAsync(IProgress<double?>? progress, CancellationToken ct = default, bool reviewedInstallApproved = false)
    {
        if (!settings.IsExecutableWorkflowEnabled("LuaToolsPlugin")) return (false, "Enable the Steam plugin in Settings before installing it.");
        using var selectedInstallation = steam.PinSelectedInstallation();
        if (SteamDir is not { } steamDir) return (false, Resources.Strings.Plugin_Err_SteamNotFound);
        bool wasRunning = false;
        bool stopped = false;
        bool mutationStarted = false;
        var backups = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        bool createdMarker = false;
        bool markerInitiallyPresent = true;
        string marker = Path.Combine(steamDir, CdpMarkerName);
        try
        {
            // Fail a development/incomplete build before changing Steam or stopping any process.
            byte[] loader = BundledPlugin.ReadBytes(LoaderName);
            if (loader.Length < 64 || loader[0] != 'M' || loader[1] != 'Z') throw new InvalidDataException("The bundled plugin loader is missing or invalid.");
            string frontendHash = BundledPlugin.FrontendSha256;
            string launcherPath = Path.GetFullPath(Environment.ProcessPath ?? "");
            byte[] configuration = BundledPlugin.LoaderConfiguration(launcherPath);
            string systemLoader = Path.Combine(Environment.SystemDirectory, LoaderName);
            BundledPlugin.EnsureSafePath(systemLoader);
            using var realStream = File.OpenRead(systemLoader);
            byte[] real = ManifestContentPolicy.ReadBounded(realStream, 16 * 1024 * 1024);
            BundledPlugin.EnsureSafePath(steamDir);
            BundledPlugin.EnsureSafePath(BundledPlugin.ReceiptPath);
            if (File.Exists(Path.Combine(steamDir, ".luatools-dll-update-disabled"))) return (false, "Remove the loader update disabled marker before installing the plugin.");
            var previous = BundledPlugin.ReadReceipt(BundledPlugin.ReceiptPath);
            foreach (string path in new[] { Path.Combine(steamDir, LoaderName), Path.Combine(steamDir, RealName), Path.Combine(steamDir, ConfigurationName), BundledPlugin.ReceiptPath }.Concat(MillenniumConfigPaths()))
                Snapshot(path, backups);
            ValidateMarker(marker);
            markerInitiallyPresent = MarkerExists(marker);
            ct.ThrowIfCancellationRequested();
            UserAuthorizedDependencies.RequireEnabled("LuaToolsPlugin", settings.IsExecutableWorkflowEnabled);
            wasRunning = steam.IsSelectedSteamRunning();
            if (wasRunning) steam.StopSteam();
            stopped = wasRunning;
            await Task.Delay(1200, ct);
            UserAuthorizedDependencies.RequireEnabled("LuaToolsPlugin", settings.IsExecutableWorkflowEnabled);
            mutationStarted = true;
            SafeWrite(Path.Combine(steamDir, LoaderName), loader);
            SafeWrite(Path.Combine(steamDir, RealName), real);
            SafeWrite(Path.Combine(steamDir, ConfigurationName), configuration);
            var disabled = SetMillenniumLuatoolsEnabled(false);
            if (previous?.DisabledMillenniumEntries is { } prior)
                foreach (var (path, entries) in prior)
                    disabled[path] = entries.Concat(disabled.GetValueOrDefault(path) ?? []).Distinct().ToList();
            createdMarker = CreateCdpMarkerJunction(marker);
            var receipt = new PluginInstallationReceipt
            {
                ActivationEnabled = true, Tag = BundledPlugin.Version, FrontendSha = frontendHash,
                SteamDirectory = steamDir, LauncherPath = launcherPath,
                DllShas = new() { [LoaderName] = BundledPlugin.Hash(loader), [RealName] = BundledPlugin.Hash(real) },
                LoaderConfigurationSha = BundledPlugin.Hash(configuration),
                OwnsCdpMarker = createdMarker || previous?.OwnsCdpMarker == true,
                DisabledMillenniumEntries = disabled,
            };
            ct.ThrowIfCancellationRequested();
            UserAuthorizedDependencies.RequireEnabled("LuaToolsPlugin", settings.IsExecutableWorkflowEnabled);
            SafeWrite(BundledPlugin.ReceiptPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt)));
            await injector.ReloadPluginFilesAsync();
            if (ActivatePlugin is { } activate) await activate();
            progress?.Report(1);
            return (true, null);
        }
        catch (Exception ex)
        {
            var rollbackErrors = new List<string>();
            foreach (var (path, bytes) in mutationStarted ? backups.Reverse() : [])
            {
                try { if (bytes is not null) SafeWrite(path, bytes); else { BundledPlugin.EnsureSafePath(path); File.Delete(path); } }
                catch (Exception restoreError) { rollbackErrors.Add(restoreError.Message); }
            }
            if (createdMarker || (mutationStarted && !markerInitiallyPresent))
                try { if (MarkerExists(marker) && IsOwnedMarker(marker)) RemoveOwnedCdpMarker(marker); }
                catch (Exception restoreError) { rollbackErrors.Add(restoreError.Message); }
            try { await injector.ReloadPluginFilesAsync(); } catch { }
            return (false, ex.Message + (rollbackErrors.Count == 0 ? "" : " Rollback incomplete: " + string.Join("; ", rollbackErrors)));
        }
        finally { if (stopped && wasRunning) steam.StartSteam(); }
    }

    public Task<bool> AutoUpdateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public async Task<(bool ok, string? error)> UninstallAsync(CancellationToken ct = default)
    {
        using var selectedInstallation = steam.PinSelectedInstallation();
        bool wasRunning = false;
        bool stopped = false;
        try
        {
            var receipt = BundledPlugin.ReadReceipt(BundledPlugin.ReceiptPath);
            if (receipt is null) return (true, null);
            if (receipt.SteamDirectory is { } installedAt && !string.Equals(Path.GetFullPath(installedAt), steam.EffectivePath is { } selected ? Path.GetFullPath(selected) : null, StringComparison.OrdinalIgnoreCase))
                return (false, Resources.Strings.Agent_System_PluginSelection);

            BundledPlugin.EnsureSafePath(BundledPlugin.ReceiptPath);
            // Disable injection even if an installed path has since become unsafe to remove.
            receipt.ActivationEnabled = false;
            SafeWrite(BundledPlugin.ReceiptPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt)));
            await injector.ReloadPluginFilesAsync();
            string? steamDir = receipt.SteamDirectory;
            if (steamDir is not null)
            {
                BundledPlugin.EnsureSafePath(steamDir);
                foreach (string name in new[] { LoaderName, RealName, ConfigurationName }) BundledPlugin.EnsureSafePath(Path.Combine(steamDir, name));
            }
            ct.ThrowIfCancellationRequested();
            wasRunning = steam.IsSelectedSteamRunning();
            if (wasRunning) steam.StopSteam();
            stopped = wasRunning;
            await Task.Delay(1200, ct);
            var unfamiliar = new List<string>();
            if (steamDir is not null)
            {
                foreach (string name in new[] { LoaderName, RealName })
                    if (!BundledPlugin.DeleteOwnedFile(Path.Combine(steamDir, name), receipt.DllShas?.GetValueOrDefault(name))) unfamiliar.Add(name);
                if (!BundledPlugin.DeleteOwnedFile(Path.Combine(steamDir, ConfigurationName), receipt.LoaderConfigurationSha)) unfamiliar.Add(ConfigurationName);
                if (receipt.OwnsCdpMarker) RemoveOwnedCdpMarker(Path.Combine(steamDir, CdpMarkerName));
            }
            SetMillenniumLuatoolsEnabled(true, receipt.DisabledMillenniumEntries);
            // Keep the disabled ownership receipt if unfamiliar files need manual review.
            if (unfamiliar.Count > 0) return (false, "Plugin deactivated. Preserved changed or unowned files: " + string.Join(", ", unfamiliar));
            BundledPlugin.EnsureSafePath(BundledPlugin.ReceiptPath);
            File.Delete(BundledPlugin.ReceiptPath);
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { if (stopped && wasRunning) steam.StartSteam(); }
    }

    private static void Snapshot(string path, Dictionary<string, byte[]?> backups)
    {
        BundledPlugin.EnsureSafePath(path);
        if (backups.ContainsKey(path)) return;
        if (!File.Exists(path)) { backups[path] = null; return; }
        using var input = File.OpenRead(path);
        backups[path] = ManifestContentPolicy.ReadBounded(input, 16 * 1024 * 1024);
    }

    private static void SafeWrite(string path, byte[] bytes)
    {
        BundledPlugin.EnsureSafePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BundledPlugin.EnsureSafePath(path);
        File.WriteAllBytes(path, bytes);
    }

    private static bool MarkerExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void ValidateMarker(string path)
    {
        BundledPlugin.EnsureSafePath(Path.GetDirectoryName(path)!);
        if (!MarkerExists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
            throw new IOException("The CDP marker is an unexpected directory; it has been preserved.");
        if ((attributes & FileAttributes.ReparsePoint) != 0 && !IsOwnedMarker(path)
            && !string.Equals(new DirectoryInfo(path).LinkTarget?.TrimEnd('\\'), LegacyCdpMarkerTarget, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The CDP marker is an unfamiliar reparse point; it has been preserved.");
    }

    private static bool IsOwnedMarker(string path) =>
        string.Equals(new DirectoryInfo(path).LinkTarget?.TrimEnd('\\'), CdpMarkerJunctionTarget, StringComparison.OrdinalIgnoreCase);

    private static bool CreateCdpMarkerJunction(string path)
    {
        ValidateMarker(path);
        if (MarkerExists(path)) return false;
        // cmd's percent expansion is active even within quotes. Refuse such paths.
        if (path.IndexOfAny(['%', '!', '"', '\r', '\n']) >= 0) throw new IOException("Unsupported CDP marker path.");
        if (Directory.Exists(CdpMarkerJunctionTarget) || File.Exists(CdpMarkerJunctionTarget)) throw new IOException("The reserved CDP marker target already exists.");
        var psi = new ProcessStartInfo("cmd.exe", $"/d /c mklink /j \"{path}\" \"{CdpMarkerJunctionTarget}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(psi) ?? throw new IOException("Could not create the CDP marker.");
        if (!process.WaitForExit(5000) || process.ExitCode != 0 || !IsOwnedMarker(path)) throw new IOException("Could not create the CDP marker junction.");
        return true;
    }

    private static void RemoveOwnedCdpMarker(string path)
    {
        BundledPlugin.EnsureSafePath(Path.GetDirectoryName(path)!);
        if (!MarkerExists(path)) return;
        if (!IsOwnedMarker(path)) throw new IOException("An unfamiliar CDP marker has been preserved.");
        Directory.Delete(path, recursive: false);
    }

    // -- Millennium coexistence: disable its luatools plugin via config (reversible), not folder-rename --

    private const string MillenniumPluginName = "luatools";
    private const string MillenniumDisabledName = MillenniumPluginName + ".disabled-by-luatools";

    /// <summary>Options for rewriting Millennium's config. The <see cref="DefaultJsonTypeInfoResolver"/> is
    /// REQUIRED, not cosmetic: re-enabling adds CLR-backed <see cref="JsonValue"/> nodes (from plain strings),
    /// and serializing those through custom options without a resolver throws
    /// "JsonSerializerOptions instance must specify a TypeInfoResolver". Removal-only writes happen to work
    /// without it (all surviving nodes are JsonElement-backed from Parse), so the failure would only ever
    /// surface on uninstall. Silently, inside the best-effort catch.</summary>
    private static readonly JsonSerializerOptions ConfigWriteOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Every place Millennium might keep its config.json, newest first. Legacy Millennium used
    /// <c>&lt;steam&gt;\ext\config.json</c>; current uses <c>&lt;steam&gt;\millennium\config\config.json</c>;
    /// an intermediate build used <c>&lt;steam&gt;\millennium\config.json</c>. A <c>MILLENNIUM__CONFIG_PATH</c>
    /// env var overrides the directory. Both real files can coexist, so callers edit *every* one present.</summary>
    private IEnumerable<string> MillenniumConfigPaths()
    {
        if (SteamDir is not { } s) yield break;
        yield return Path.Combine(s, "millennium", "config", "config.json");
        yield return Path.Combine(s, "millennium", "config.json");
        yield return Path.Combine(s, "ext", "config.json");
        var env = Environment.GetEnvironmentVariable("MILLENNIUM__CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            yield return Path.Combine(env, "config.json");
    }

    /// <summary>True if an enabledPlugins entry refers to our plugin. The entry's leading space-delimited
    /// token is the plugin id; Millennium versions store it as <c>luatools</c>, <c>luatools LUA</c>
    /// (name + backend), or (from the old rename) <c>luatools.disabled-by-luatools[ LUA]</c>.</summary>
    private static bool IsLuatoolsEntry(string entry)
    {
        string token = entry.Split(' ', 2)[0];
        return token.Equals(MillenniumPluginName, StringComparison.OrdinalIgnoreCase)
            || token.Equals(MillenniumDisabledName, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray? NestedEnabled(JsonObject root) =>
        root["plugins"] is JsonObject p ? p["enabledPlugins"] as JsonArray : null;
    private static JsonArray? FlatEnabled(JsonObject root) =>
        root["plugins.enabledPlugins"] as JsonArray; // stray dotted key some Millennium builds also write

    /// <summary>Remove (enable=false) or restore (enable=true) our plugin from <c>plugins.enabledPlugins</c>
    /// in every Millennium config that exists. On disable returns configPath ? the exact entry strings
    /// removed, so uninstall can restore them verbatim (passed back in as <paramref name="restore"/>).
    /// Best-effort per file; edits a JSON DOM so all unrelated keys/plugins are preserved.</summary>
    private Dictionary<string, List<string>> SetMillenniumLuatoolsEnabled(
        bool enable, IReadOnlyDictionary<string, List<string>>? restore = null)
    {
        var removedMap = new Dictionary<string, List<string>>();
        foreach (string path in MillenniumConfigPaths().Concat(restore?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                BundledPlugin.EnsureSafePath(path);
                if (!File.Exists(path)) continue;
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) continue;
                bool changed = false;

                if (!enable)
                {
                    // Record only the nested (real) array's removals for restore; the flat dotted key is a
                    // stray duplicate some builds write. Scrub it, but never restore into it.
                    var removed = new List<string>();
                    if (NestedEnabled(root) is { } nested)
                        for (int i = nested.Count - 1; i >= 0; i--)
                            if (nested[i]?.GetValue<string>() is { } v && IsLuatoolsEntry(v))
                            {
                                removed.Add(v);
                                nested.RemoveAt(i);
                                changed = true;
                            }
                    if (FlatEnabled(root) is { } flat)
                        for (int i = flat.Count - 1; i >= 0; i--)
                            if (flat[i]?.GetValue<string>() is { } v && IsLuatoolsEntry(v))
                            {
                                flat.RemoveAt(i);
                                changed = true;
                            }
                    if (removed.Count > 0)
                    {
                        removed.Reverse(); // preserve original order
                        removedMap[path] = removed;
                    }
                }
                else
                {
                    var arr = NestedEnabled(root); // restore only into the real nested array
                    if (arr is not null && !arr.Any(n => n?.GetValue<string>() is { } v && IsLuatoolsEntry(v)))
                    {
                        var toAdd = restore is not null && restore.TryGetValue(path, out var r) && r.Count > 0
                            ? r
                            : new List<string>();
                        foreach (string e in toAdd) arr.Add(e);
                        changed = true;
                    }
                }

                if (changed) SafeWrite(path, Encoding.UTF8.GetBytes(root.ToJsonString(ConfigWriteOpts)));
            }
            catch { /* best effort per file. A locked/invalid config must not fail install/uninstall */ }
        }
        return removedMap;
    }



}
