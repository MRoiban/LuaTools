using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Prepares and runs the original tool only after its separate opt-in and explicit user action.
/// Consent receipts detect bundle changes and do not establish an independent audit.</summary>
public class CloudRedirectService(GithubProxy gh, SettingsService? settings = null)
{
    private bool IsEnabled(string component) => settings?.IsExecutableWorkflowEnabled(component) == true;

    private static readonly string ToolDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirectoryName, "cloudredirect");
    private static string ExePath => Path.Combine(ToolDir, "CloudRedirect.exe");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Prepare the original dependency bundle only for an explicit action with this tool enabled.
    /// User consent does not establish an independent upstream audit.</summary>
    public async Task<string?> EnsureToolAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IProgress<double?>? sink = progress;
            return await DependencyQuarantine.PrepareToolAsync(gh, "CloudRedirect", AppConfig.CloudRedirectRepo,
                ToolDir, ExePath, a => a.Name.Equals("CloudRedirect.exe", StringComparison.OrdinalIgnoreCase), sink, ct, enabled: IsEnabled, explicitUserAction: true, archive: false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Download (if needed), and launch the CloudRedirect GUI. Fire-and-forget. Its window opens and
    /// we don't wait for it. Returns false if the tool couldn't be downloaded or started.</summary>
    public async Task<bool> LaunchAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        string? exe = await EnsureToolAsync(progress, ct);
        if (exe is null) return false;

        try
        {
            // GUI app: show its window (UseShellExecute) and don't block our app on it.
            UserAuthorizedDependencies.RequireDirectory("CloudRedirect", ToolDir, IsEnabled);
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = ToolDir,
            });
            return true;
        }
        catch { return false; }
    }
}
