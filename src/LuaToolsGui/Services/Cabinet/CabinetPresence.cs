using System.Diagnostics;
using System.IO;

namespace LuaToolsGui.Services.Cabinet;

internal sealed class PresenceRelay : IDisposable
{
    internal string Path { get; }
    private readonly EventWaitHandle _stop;
    private Process? _process;
    internal PresenceRelay()
    {
        string nonce = Guid.NewGuid().ToString("N");
        string name = "Local\\Cabinet-presence-" + nonce;
        Path = SnapshotPath(nonce);
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        try
        {
            using var parent = Process.GetCurrentProcess();
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--cabinet-presence", "480", parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(), name }) info.ArgumentList.Add(arg);
            _process = Process.Start(info) ?? throw new IOException("Presence reader did not start");
            CabinetHelperActivity.Track(_process);
        }
        catch { Dispose(); throw; }
    }
    internal static string SnapshotPath(string nonce)
    {
        if (nonce.Length != 32 || !nonce.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid presence nonce");
        var path = System.IO.Path.Combine(CabinetPaths.Current.DataRoot, "presence-" + nonce + ".json");
        CabinetFiles.EnsureSafePath(path);
        return path;
    }
    internal static void Run(EventWaitHandle stop, Func<bool> cancelled)
    {
        // The caller validates the event prefix and nonce before loading the Valve DLL.
        string nonce = CurrentNonce!;
        var path = SnapshotPath(nonce);
        try
        {
            using var session = new SteamSession("480", PluginLog.Log);
            while (!cancelled() && Playtime.SteamRunning())
            {
                session.Pump();
                WorkshopEngine.AtomicWrite(path, session.PresenceSnapshot());
                if (stop.WaitOne(1000)) break;
            }
        }
        finally { File.Delete(path); }
    }
    internal static string? CurrentNonce;
    public void Dispose()
    {
        _stop.Set();
        if (_process is not null)
        {
            try { if (!_process.WaitForExit(3000)) { _process.Kill(); _process.WaitForExit(1000); } } catch (InvalidOperationException) { }
            _process.Dispose();
        }
        _stop.Dispose();
        try { File.Delete(Path); } catch (IOException) { }
    }
}
