using System.IO;
using System.Text.Json.Serialization;
namespace LuaToolsGui.Services;

public sealed record LocalInstallOutcome(string Name, bool Ok, bool Skipped, bool LuaInstalled, int ManifestCount, int FailedCount = 0, [property: JsonIgnore] string? Error = null);
/// <summary>Per-batch state and serialization shared by Home, Add and agent submissions.</summary>
public sealed class LocalInstallCoordinator(LuaInstaller installer, LuaOverwriteCoordinator overwrites)
{
    private readonly SemaphoreSlim _batches = new(1);
    public async Task<IReadOnlyList<LocalInstallOutcome>> InstallAsync(IEnumerable<string> paths,
        Func<string, OverwriteDiff, CancellationToken, Task<bool>> decide, CancellationToken ct = default)
    {
        await _batches.WaitAsync(ct);
        try
        {
            var results = new List<LocalInstallOutcome>();
            var versions = new Dictionary<long, string>();
            foreach (string path in paths)
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path), ext = Path.GetExtension(path).ToLowerInvariant();
                long? id = ext == ".lua" ? LuaInstaller.AppIdFromFileName(path) : ext == ".zip" ? LuaInstaller.AppIdForZip(path) : null;
                if (!LuaInstaller.IsInstallable(path) || ext != ".manifest" && id is null)
                { results.Add(new(name, true, true, false, 0)); continue; }
                if (id is { } appId)
                {
                    var diff = await overwrites.InspectAsync(path, appId, ct: ct);
                    versions[appId] = diff.Version;
                    if (diff.Overwrites && !await decide(path, diff, ct))
                    { results.Add(new(name, true, true, false, 0)); continue; }
                }
                InstallResult result;
                lock (LuaInstaller.InstallGate)
                {
                    // Compare again in the same install turn, including after a queued decision.
                    if (id is { } target && versions.GetValueOrDefault(target) is { } expected && overwrites.Version(target) != expected)
                    { results.Add(new(name, false, false, false, 0)); continue; }
                    result = ext == ".manifest" ? installer.InstallManifestFile(path) : ext == ".zip" ? installer.InstallZip(path, id!.Value) : installer.InstallLuaFile(path, id!.Value);
                }
                results.Add(new(name, !result.AnyFailed, false, result.LuaInstalled, result.ManifestCount, result.Failed.Count, result.Error));
            }
            return results;
        }
        finally { _batches.Release(); }
    }
}
