using System.Diagnostics;

namespace LuaToolsGui.Services;

/// <summary>Shared application lifecycle and signed-update orchestration; verification stays in UpdateService.</summary>
public class ApplicationCoordinator(UpdateService updates)
{
    public virtual Task<UpdateCheckResult> CheckAsync() => updates.CheckAndStageAsync(announceStaged: false);
    public virtual void Apply() => updates.ApplyAndRestart();
    public static void Restart(bool minimized = false)
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                // The existing startup wait releases the owner mutex before normal startup/verification.
                start.ArgumentList.Add("--fork-update-wait");
                start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (minimized) start.ArgumentList.Add("--minimized");
                Process.Start(start);
            }
        }
        finally { System.Windows.Application.Current?.Shutdown(); }
    }
}
