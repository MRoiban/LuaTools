using System.IO;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Cabinet;

namespace LuaToolsGui.Services;

/// <summary>Shared mode/add-on policy. Installers still own quarantine, receipts and rollback.</summary>
public class ModeCoordinator(UnlockerService unlocker, SteamService steam, CloudRedirectService cloud, CabinetService? cabinet = null)
{
    private readonly SemaphoreSlim _write = new(1);
    private DateTimeOffset _lastRefresh;
    public void RequireInstall(UnlockerMode mode) => unlocker.RequireInstallAllowed(mode);
    public async Task<(ModeInstallResult Result, bool Started)> InstallAsync(UnlockerMode mode, IProgress<double?>? progress = null, CancellationToken ct = default, Action? revalidate = null)
    {
        await _write.WaitAsync(ct);
        try
        {
            // A denied or revoked workflow must never close Steam.
            revalidate?.Invoke();
            using var selected = steam.PinSelectedInstallation();
            RequireInstall(mode);
            ct.ThrowIfCancellationRequested();
            if (steam.IsSelectedSteamRunning()) await Task.Run(steam.StopSteam, ct);
            var result = await unlocker.InstallAsync(mode, progress, ct);
            // Preserve the GUI contract, including Custom and a returned install failure.
            bool started = await Task.Run(steam.StartSteam, ct);
            return (result, started);
        }
        finally { _write.Release(); }
    }
    public async Task<(UnlockerMode? Detected, ModeState[] States)> ReadAsync(bool refresh = false, bool adoptDetection = false, CancellationToken ct = default)
    {
        bool force = refresh && DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromSeconds(30);
        if (force) _lastRefresh = DateTimeOffset.UtcNow;
        var detected = unlocker.SelectedMode is null ? await unlocker.DetectActiveModeAsync(ct, adoptDetection) : null;
        var states = new List<ModeState>();
        foreach (var def in unlocker.Modes.Where(d => d.HiddenUnlessFile is null || unlocker.SelectedMode == d.Mode || steam.EffectivePath is { } root && File.Exists(Path.Combine(root, d.HiddenUnlessFile))))
            states.Add(def.Mode == unlocker.SelectedMode || def.Mode == UnlockerMode.Custom
                ? await unlocker.GetStateAsync(def.Mode, force, ct)
                : new(def.Mode, ModeStatus.NotInstalled, false, null));
        return (detected, states.ToArray());
    }
    public bool CloudUnlocked => unlocker.SelectedMode is UnlockerMode.Ost or UnlockerMode.Bst;
    public Task<CloudRedirectAddonState> ReadCloudAsync(bool refresh = false, CancellationToken ct = default) =>
        unlocker.GetCloudRedirectStateAsync(CloudUnlocked, refresh, ct);
    public async Task<ModeInstallResult> UpdateCloudAsync(bool? enabled, bool refresh, IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            if (!CloudUnlocked) return ModeInstallResult.Fail(Resources.Strings.Mode_CloudRedirect_Locked);
            var result = refresh ? await unlocker.UpdateCloudRedirectAsync(progress, ct)
                : enabled == true ? await unlocker.EnableCloudRedirectAsync(progress, ct) : unlocker.DisableCloudRedirect();
            if (result.Success && enabled == true && cabinet is not null)
                try { await cabinet.ConfigureCloudRedirectAsync(); } catch { /* Existing best-effort Cabinet configuration. */ }
            return result;
        }
        finally { _write.Release(); }
    }
    public Task<bool> ManageCloudAsync(IProgress<double?>? progress = null, CancellationToken ct = default) => cloud.LaunchAsync(progress, ct);
    public async Task EnableCloudForCabinetAsync()
    {
        var state = await ReadCloudAsync();
        if (!state.Enabled) await UpdateCloudAsync(true, false);
        else if (cabinet is not null) await cabinet.ConfigureCloudRedirectAsync();
    }
}
