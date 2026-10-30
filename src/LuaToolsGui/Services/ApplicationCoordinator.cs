using System.Diagnostics;

namespace LuaToolsGui.Services;

/// <summary>Shared application lifecycle and signed-update orchestration; verification stays in UpdateService.</summary>
public class ApplicationCoordinator(UpdateService updates)
{
    public virtual Task<UpdateCheckResult> CheckAsync() => updates.CheckAndStageAsync(announceStaged: false);
    public virtual void Apply() => updates.ApplyAndRestart();
    public static void Restart()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var start = new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"" )
                { CreateNoWindow = true, UseShellExecute = false };
                Process.Start(start);
            }
        }
        finally { System.Windows.Application.Current?.Shutdown(); }
    }
}
