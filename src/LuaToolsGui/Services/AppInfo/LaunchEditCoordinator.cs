namespace LuaToolsGui.Services.AppInfo;

/// <summary>Shared editor semantics; observation never rebases and failed restoration retains recovery data.</summary>
public sealed class LaunchEditCoordinator(LaunchOptionsService launch)
{
    public LaunchState? Inspect(int appId) => launch.Inspect(appId);
    public IReadOnlyList<int> FindDrifted() => launch.FindDrifted();
    public ApplyResult Reapply(IReadOnlyList<int> ids) => launch.Reapply(ids);
    public ApplyResult Restore(int appId) => launch.Serialized(() =>
    {
        // Rebase only as part of the explicitly authorized mutation, as the GUI editor does.
        if (launch.Read(appId) is null) return new(false);
        if (launch.StageRestore(appId) is not { } original) return new(false);
        var result = launch.Apply(new Dictionary<int, IReadOnlyList<LaunchOption>> { [appId] = original }, true);
        if (result.Ok) launch.Store.Remove(appId);
        return result;
    });
    public static LaunchOption Normalize(LaunchOption option)
    {
        var result = option.Clone();
        result.Executable = result.Executable.Trim(); result.Arguments = result.Arguments.Trim();
        result.WorkingDir = result.WorkingDir.Trim(); result.Description = result.Description.Trim();
        result.Type = result.Type.Trim(); result.OsList = result.OsList.Trim(); result.OsArch = result.OsArch.Trim();
        result.BetaKey = result.BetaKey.Trim(); result.OwnsDlc = result.OwnsDlc.Trim();
        return result;
    }
}
