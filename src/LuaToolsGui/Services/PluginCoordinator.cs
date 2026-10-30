namespace LuaToolsGui.Services;

/// <summary>Shared explicit plugin action, used by Home, Plugin and the agent facade.</summary>
public class PluginCoordinator(PluginInstallerService installer)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PluginInstallerService, PluginCoordinator> Shared = new();
    public static PluginCoordinator For(PluginInstallerService installer) => Shared.GetValue(installer, i => new(i));
    private readonly SemaphoreSlim _write = new(1);
    public Task<PluginStatus> ReadAsync(bool refresh = false, CancellationToken ct = default) => installer.GetStatusAsync(refresh, ct);
    public static bool ConfirmSteamRestart() => System.Windows.MessageBox.Show(
        Resources.Strings.Plugin_Confirm_RestartBody, Resources.Strings.Plugin_Confirm_RestartCaption,
        System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;
    public async Task<(bool ok, string? error)> ChangeAsync(bool install, IProgress<double?>? progress = null, CancellationToken ct = default, Action? revalidate = null)
    {
        var installation = installer.CaptureInstallation();
        await _write.WaitAsync(ct);
        try
        {
            revalidate?.Invoke();
            if (!installation.IsCurrent) throw new InvalidOperationException(Resources.Strings.Agent_System_TargetChanged);
            using var selected = installation.Pin();
            return install ? await installer.InstallAsync(progress, ct) : await installer.UninstallAsync(ct);
        }
        finally { _write.Release(); }
    }
}
