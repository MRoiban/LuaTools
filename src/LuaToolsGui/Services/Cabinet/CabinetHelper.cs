using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace LuaToolsGui.Services.Cabinet;

internal static class CabinetHelper
{
    internal static bool IsHelperProcess { get; private set; }
    internal static bool Handle(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--cabinet-helper" or "--cabinet-schema" or "--cabinet-presence")) return false;
        // Reserved switches always terminate here, including malformed invocations.
        try
        {
            if (args.Length != 5) throw new ArgumentException("Invalid Cabinet helper arguments");
            CabinetPaths.Numeric(args[1]);
            if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0
                || !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start <= 0
                || !args[4].StartsWith("Local\\Cabinet-", StringComparison.Ordinal) || args[4].Length > 100)
                throw new ArgumentException("Invalid Cabinet helper parent/event");
            using var parent = Process.GetProcessById(parentId);
            using var stop = EventWaitHandle.OpenExisting(args[4]);
            bool Cancelled() => !ParentAlive(parent, start) || stop.WaitOne(0) || CabinetPaths.Current.StandaloneRunning();
            if (Cancelled() || CabinetPaths.Current.StandaloneRunning() || !Playtime.SteamRunning()) return true;
            IsHelperProcess = true;
            using var watchdog = new ParentWatchdog(parent, start, stop);
            if (args[0] == "--cabinet-presence")
            {
                const string prefix = "Local\\Cabinet-presence-";
                if (args[1] != "480" || !args[4].StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Invalid presence event");
                PresenceRelay.CurrentNonce = args[4][prefix.Length..];
                _ = PresenceRelay.SnapshotPath(PresenceRelay.CurrentNonce);
                PresenceRelay.Run(stop, Cancelled);
            }
            else if (args[0] == "--cabinet-schema") SchemaFill.Fetch(args[1], Cancelled, PluginLog.Log);
            else
            {
                using var single = new Mutex(true, "Local\\Cabinet-shadow-app-" + args[1], out var created);
                if (!created) return true;
                var account = Syncer.AccountId();
                if (account is null) return true;
                Syncer.BackupSteamFiles(args[1], account);
                PresenceRelay? relay = null;
                try { relay = new PresenceRelay(); } catch (Exception) { PluginLog.Log("Cabinet join presence reader unavailable"); }
                using var source = relay;
                using var session = new SteamSession(args[1], PluginLog.Log);
                var detector = new Playtime(Path.Combine(CabinetPaths.Current.DataRoot, "playtime.txt"), PluginLog.Log);
                detector.RefreshGames();
                while (!Cancelled() && Playtime.SteamRunning() && !CabinetPaths.Current.StandaloneRunning())
                {
                    session.Pump();
                    session.MirrorJoin(source is not null && detector.RunningApps().SetEquals(new[] { args[1] }) ? source.Path : null!);
                    try { session.Sync(false); }
                    catch (IOException ex) { PluginLog.Log("Cabinet helper read deferred: " + ex.Message); }
                    if (stop.WaitOne(1000)) break;
                }
                // Final OnlineFix flush is bounded and still tied to the parent's identity.
                if (ParentAlive(parent, start) && Playtime.SteamRunning() && !CabinetPaths.Current.StandaloneRunning())
                {
                    Thread.Sleep(1000);
                    if (ParentAlive(parent, start))
                    {
                        session.Sync(true);
                        for (var i = 0; i < 3 && ParentAlive(parent, start); i++) { session.Pump(); Thread.Sleep(250); }
                    }
                }
            }
        }
        catch (Exception ex) { PluginLog.Log("Cabinet helper refused/failed: " + ex.Message); Environment.ExitCode = 1; }
        return true;
    }
    internal static bool ParentAlive(Process parent, long start)
    {
        try { return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == start; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    // A blocked native Steam call cannot prevent retirement when the UI parent dies.
    private sealed class ParentWatchdog : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly Thread _thread;
        internal ParentWatchdog(Process parent, long start, EventWaitHandle stop)
        {
            _thread = new Thread(() =>
            {
                DateTime? stopping = null;
                while (!_cancel.IsCancellationRequested)
                {
                    if (!ParentAlive(parent, start) || CabinetPaths.Current.StandaloneRunning()) Environment.Exit(0);
                    if (stop.WaitOne(0)) stopping ??= DateTime.UtcNow;
                    if (stopping.HasValue && DateTime.UtcNow - stopping.Value > TimeSpan.FromSeconds(20)) Environment.Exit(0);
                    if (_cancel.Token.WaitHandle.WaitOne(250)) break;
                }
            }) { IsBackground = true, Name = "Cabinet helper parent watchdog" };
            _thread.Start();
        }
        public void Dispose()
        {
            _cancel.Cancel();
            _thread.Join();
            _cancel.Dispose();
        }
    }
}
