using System.IO;
using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;

/// <summary>Authenticated, bounded transport scratch. No client path is opened by the owner.</summary>
internal sealed class AgentInputStaging : IDisposable
{
    private static readonly SemaphoreSlim Uploads = new(2);
    private static readonly object BudgetGate = new();
    private static readonly Dictionary<string, long> Reserved = [];
    private readonly string _root;
    private readonly AgentRequest _request;
    private AgentInputStaging(string root, AgentRequest request) { _root = root; _request = request; }
    public static async Task<AgentInputStaging?> ReceiveAsync(Stream pipe, AgentRequest request, CancellationToken ct)
    {
        if (request.Files is not { Length: > 0 } files) return null;
        if (files.Length > FileSubmission.MaxFiles || files.Any(f => !FileSubmission.Valid(f)) || files.Sum(f => f.Length) > FileSubmission.MaxBytes
            || request.Command is not ("lua add" or "lua get" or "lua update" or "launch-option create" or "launch-option update" or "preset create")) throw new InvalidDataException();
        if (!await Uploads.WaitAsync(0, ct)) throw new InvalidDataException();
        string root = Path.Combine(DataPaths.Local, AppConfig.DataDirectoryName, "agent-input", Guid.NewGuid().ToString("N"));
        var staging = new AgentInputStaging(root, request);
        lock (BudgetGate)
        {
            long bytes = files.Sum(f => f.Length);
            if (Reserved.Values.Sum() + bytes > 2L * FileSubmission.MaxBytes) { Uploads.Release(); throw new InvalidDataException(); }
            Reserved.Add(root, bytes);
        }
        try
        {
            ManifestContentPolicy.EnsureNoReparsePoints(root);
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            var paths = new List<string>();
            for (int i = 0; i < files.Length; i++)
            {
                string folder = Path.Combine(root, i.ToString()); Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, files[i].Name);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    long remaining = files[i].Length;
                    while (remaining > 0)
                    {
                        using var frameDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); frameDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                        var chunk = await Protocol.ReadAsync<FileChunk>(pipe, Protocol.MaxRequest, frameDeadline.Token);
                        if (chunk.Bytes.Length <= 0 || chunk.Bytes.Length > FileSubmission.ChunkBytes || chunk.Bytes.Length > remaining) throw new InvalidDataException();
                        await output.WriteAsync(chunk.Bytes, ct); remaining -= chunk.Bytes.Length;
                    }
                }
                if (!string.Equals(LuaVault.HashFile(path), files[i].Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                paths.Add(path);
            }
            request.InputPaths = paths.ToArray(); return staging;
        }
        catch { staging.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (!_request.RetainInputs) { try { Directory.Delete(_root, true); } catch (IOException) { }
            lock (BudgetGate) Reserved.Remove(_root); }
        Uploads.Release();
    }
    public static void DeleteRetained(string[] paths)
    {
        if (paths.Length == 0) return;
        string root = Path.GetDirectoryName(Path.GetDirectoryName(paths[0]))!;
        try { Directory.Delete(root, true); } catch (IOException) { }
        lock (BudgetGate) Reserved.Remove(root);
    }
}
