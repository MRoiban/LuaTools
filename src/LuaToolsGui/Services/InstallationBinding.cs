using LuaToolsGui.Services.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services;

/// <summary>The admitted Steam root, independent of later UI selection and inherited async scopes.</summary>
internal sealed class InstallationBinding(SteamService steam, string? path)
{
    public string Identity { get; } = MutationCoordinator.Hash((path ?? "").ToUpperInvariant());
    public bool IsCurrent => path is not null && string.Equals(path, steam.SelectedPath, StringComparison.OrdinalIgnoreCase);
    public IDisposable Pin() => steam.PinInstallation(path);
    public DownloadJob Bind(DownloadJob job) => job with
    {
        DedupeKey = job.DedupeKey + ":steam:" + Identity,
        DownloadAsync = async (item, progress, ct) =>
        {
            if (!IsCurrent) throw new DownloadAbortedException(Resources.Strings.Agent_Add_GateChanged);
            using var selected = Pin();
            return await job.DownloadAsync(item, progress, ct).ConfigureAwait(false);
        },
        ConfirmAsync = async (file, item, ct) => IsCurrent && (job.ConfirmAsync is null || await job.ConfirmAsync(file, item, ct).ConfigureAwait(false)) && IsCurrent,
        InstallAsync = async (file, item, ct) =>
        {
            if (!IsCurrent) return new JobResult(false, Resources.Strings.Agent_Add_GateChanged);
            using var selected = Pin();
            return await job.InstallAsync(file, item, ct).ConfigureAwait(false);
        },
    };
}
