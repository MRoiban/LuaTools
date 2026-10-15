// Ported from the user-owned Cabinet engine. Tray, cloud and updater are excluded.
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
namespace LuaToolsGui.Services.Cabinet;
sealed class Shadows : IDisposable
{
    sealed class Child { public Process Process; public EventWaitHandle Stop; public DateTime Stopping; }
    readonly Dictionary<string, Child> children = new Dictionary<string, Child>();
    readonly Dictionary<string, DateTime> retry = new Dictionary<string, DateTime>();
    readonly Action<string> log;
    public Shadows(Action<string> logger) { log = logger; }
    public bool Active(string appId) { return children.ContainsKey(appId); }
    public bool HasChildren { get { return children.Count > 0; } }
    public static bool HelperActive(string id)
    {
        try { using (var m = Mutex.OpenExisting("Local\\Cabinet-shadow-app-" + id)) return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    public static bool AnyHelper()
    {
        try { using (var active = EventWaitHandle.OpenExisting("Local\\Cabinet-shadow-active")) return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    public IEnumerable<string> Tick(HashSet<string> running, string account)
    {
        var finished = new List<string>();
        foreach (var pair in children.ToArray())
        {
            var child = pair.Value;
            if (!running.Contains(pair.Key) && child.Stopping == DateTime.MinValue)
            {
                child.Stop.Set(); child.Stopping = DateTime.UtcNow;
            }
            if (!child.Process.HasExited && child.Stopping != DateTime.MinValue && DateTime.UtcNow - child.Stopping > TimeSpan.FromSeconds(20))
            {
                log("shadow " + pair.Key + ": final sync timed out; stopping helper");
                child.Process.Kill();
            }
            if (!child.Process.HasExited) continue;
            log("shadow " + pair.Key + ": exited code=" + child.Process.ExitCode);
            child.Process.Dispose(); child.Stop.Dispose(); children.Remove(pair.Key);
            retry[pair.Key] = DateTime.UtcNow.AddSeconds(60);
            finished.Add(pair.Key);
        }
        if (account == null || !Playtime.SteamRunning()) return finished;
        foreach (var id in running)
        {
            DateTime after;
            if (children.ContainsKey(id) || HelperActive(id) || Playtime.SteamCounting(id) || (retry.TryGetValue(id, out after) && DateTime.UtcNow < after)) continue;
            EventWaitHandle stop = null;
            try
            {
                Syncer.BackupSteamFiles(id, account);
                var name = "Local\\Cabinet-shadow-" + Guid.NewGuid().ToString("N");
                stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
                var parent = Process.GetCurrentProcess();
                var info = new ProcessStartInfo(Environment.ProcessPath, "--cabinet-helper " + id + " " + parent.Id + " " + parent.StartTime.ToUniversalTime().Ticks + " " + name);
                parent.Dispose();
                info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = ProcessWindowStyle.Hidden;
                children[id] = new Child { Process = Process.Start(info), Stop = stop };
                CabinetHelperActivity.Track(children[id].Process);
                log("shadow " + id + ": started hidden helper");
            }
            catch (Exception ex)
            {
                if (stop != null) stop.Dispose();
                retry[id] = DateTime.UtcNow.AddMinutes(1);
                log("shadow " + id + ": start failed: " + ex.Message);
            }
        }
        return finished;
    }
    public void Dispose()
    {
        foreach (var child in children.Values) child.Stop.Set();
        // Helpers also check their parent's lifetime, including tray crashes/upgrades.
        foreach (var child in children.Values) { try { if (!child.Process.WaitForExit(5000)) { child.Process.Kill(); child.Process.WaitForExit(5000); } } finally { child.Process.Dispose(); child.Stop.Dispose(); } }
        children.Clear();
    }
}

// ---------- Text VDF (localconfig.vdf) minimal editor ----------
// Edits values in place so the rest of Steam's file stays byte-identical.
