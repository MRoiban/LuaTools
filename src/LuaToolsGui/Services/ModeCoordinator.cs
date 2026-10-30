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
        var installation = new InstallationBinding(steam, steam.EffectivePath);
        await _write.WaitAsync(ct);
        try
        {
            // A denied or revoked workflow must never close Steam.
            revalidate?.Invoke();
            if (!installation.IsCurrent) throw new InvalidOperationException(Resources.Strings.Agent_System_TargetChanged);
            using var selected = installation.Pin();
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
    public async Task<(UnlockerMode? Detected, ModeState[] States, CloudRedirectAddonState Cloud)> ReadAsync(bool refresh = false, bool adoptDetection = false, CancellationToken ct = default)
    {
        using var selected = steam.PinSelectedInstallation();
        bool force = refresh && DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromSeconds(30);
        if (force) _lastRefresh = DateTimeOffset.UtcNow;
        var detected = unlocker.SelectedMode is null ? await unlocker.DetectActiveModeAsync(ct, adoptDetection) : null;
        var states = new List<ModeState>();
        foreach (var def in unlocker.Modes.Where(d => d.HiddenUnlessFile is null || unlocker.SelectedMode == d.Mode || steam.EffectivePath is { } root && File.Exists(Path.Combine(root, d.HiddenUnlessFile))))
            states.Add(def.Mode == unlocker.SelectedMode || def.Mode == UnlockerMode.Custom
                ? await unlocker.GetStateAsync(def.Mode, force, ct)
                : new(def.Mode, ModeStatus.NotInstalled, false, null));
        return (detected, states.ToArray(), await ReadCloudAsync(force, ct));
    }
    public bool CloudUnlocked => unlocker.SelectedMode is UnlockerMode.Ost or UnlockerMode.Bst;
    public Task<CloudRedirectAddonState> ReadCloudAsync(bool refresh = false, CancellationToken ct = default) =>
        unlocker.GetCloudRedirectStateAsync(CloudUnlocked, refresh, ct);
    public virtual async Task<ModeInstallResult> UpdateCloudAsync(bool? enabled, bool refresh, IProgress<double?>? progress = null, CancellationToken ct = default, Action? revalidate = null)
    {
        var installation = new InstallationBinding(steam, steam.EffectivePath);
        await _write.WaitAsync(ct);
        try
        {
            revalidate?.Invoke();
            if (!installation.IsCurrent) throw new InvalidOperationException(Resources.Strings.Agent_System_TargetChanged);
            using var selected = installation.Pin();
            if (!CloudUnlocked) return ModeInstallResult.Fail(Resources.Strings.Mode_CloudRedirect_Locked);
            var result = refresh ? await unlocker.UpdateCloudRedirectAsync(progress, ct)
                : enabled == true ? await unlocker.EnableCloudRedirectAsync(progress, ct) : unlocker.DisableCloudRedirect();
            return result.Success && enabled == true && cabinet is not null ? await ConfigureCabinetAsync(result) : result;
        }
        finally { _write.Release(); }
    }
    public Task<bool> ManageCloudAsync(IProgress<double?>? progress = null, CancellationToken ct = default) => cloud.LaunchAsync(progress, ct);
    public virtual async Task<ModeInstallResult> EnrollCloudSavesAsync(CancellationToken ct = default, Action? revalidate = null)
    {
        revalidate?.Invoke();
        var result = await UpdateCloudAsync(true, false, ct: ct, revalidate: revalidate);
        if (!result.Success) return result;
        revalidate?.Invoke();
        if (cabinet is null) return ModeInstallResult.Fail(Resources.Strings.Agent_System_Failed);
        // Enrollment must report credential configuration failures, unlike best-effort add-on enabling.
        await cabinet.ConfigureCloudRedirectAsync();
        return result;
    }
    public async Task<ModeInstallResult> EnableCloudForCabinetAsync()
    {
        var state = await ReadCloudAsync();
        if (!state.Enabled) return await UpdateCloudAsync(true, false);
        return cabinet is null ? ModeInstallResult.Ok() : await ConfigureCabinetAsync(ModeInstallResult.Ok());
    }
    /// <summary>A failed configuration does not undo the add-on change, but the caller must report it.</summary>
    internal async Task<ModeInstallResult> ConfigureCabinetAsync(ModeInstallResult result)
    {
        try { await ConfigureCabinetCoreAsync(); return result; }
        catch (Exception ex)
        {
            AppEventLog.Shared.Write(Microsoft.Extensions.Logging.LogLevel.Warning, "Mode", "Cabinet configuration for CloudRedirect failed", ex);
            return result with { CabinetConfigFailed = true };
        }
    }
    protected virtual Task ConfigureCabinetCoreAsync() => cabinet!.ConfigureCloudRedirectAsync();
}
