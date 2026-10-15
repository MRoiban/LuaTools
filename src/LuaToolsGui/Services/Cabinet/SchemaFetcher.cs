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
sealed class SchemaFill : IDisposable
{
    Process child;
    EventWaitHandle stop;
    string appId;
    DateTime started, idleAfter = DateTime.UtcNow.AddMinutes(3);
    int found;
    bool batch;
    readonly Action<string> log;
    public SchemaFill(Action<string> logger) { log = logger; }
    public bool Active(string id) { return child != null && appId == id; }
    public bool HasChild { get { return child != null; } }
    public static string SchemaPath(string id) { return Path.Combine(Syncer.StatsDir(), "UserGameStatsSchema_" + id + ".bin"); }
    static string OutcomePath(string id) { return Path.Combine(CabinetPaths.Current.DataRoot, "schemas", id + ".txt"); }
    static bool Due(string id)
    {
        if (File.Exists(SchemaPath(id))) return false;
        var path = OutcomePath(id);
        if (!File.Exists(path)) return true;
        var p = CabinetFiles.ReadText(path).Split('|'); long ticks;
        if (p.Length > 1 && p[1] == "found") return false;
        return p.Length == 0 || !long.TryParse(p[0], out ticks) || DateTime.UtcNow.Ticks - ticks >= TimeSpan.FromDays(7).Ticks;
    }
    public int LastFound { get; private set; }
    public string Tick(bool playing, bool updating)
    {
        LastFound = 0;
        if (playing) idleAfter = DateTime.UtcNow.AddMinutes(3);
        if (child != null)
        {
            if (playing || !Playtime.SteamRunning()) stop.Set();
            if (!child.HasExited && DateTime.UtcNow - started > TimeSpan.FromSeconds(12)) { stop.Set(); child.Kill(); }
            if (!child.HasExited) return null;
            if (File.Exists(SchemaPath(appId))) found++;
            log("schema " + appId + ": helper exited code=" + child.ExitCode);
            child.Dispose(); stop.Dispose(); child = null; stop = null;
        }
        if (playing || updating || DateTime.UtcNow < idleAfter || !Playtime.SteamRunning() || Shadows.AnyHelper()) return null;
        var id = Syncer.OnlineFixAppIds().OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault(Due);
        if (id == null)
        {
            if (!batch) return null;
            batch = false; int count = found; found = 0; LastFound = count;
            return count > 0 ? L.T("Found achievement lists for " + count + " games.", "Listes de succès trouvées pour " + count + " jeux.") : null;
        }
        batch = true; appId = id;
        var name = "Local\\Cabinet-schema-" + Guid.NewGuid().ToString("N");
        stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using (var parent = Process.GetCurrentProcess())
        {
            var info = new ProcessStartInfo(Environment.ProcessPath, "--cabinet-schema " + id + " " + parent.Id + " " + parent.StartTime.ToUniversalTime().Ticks + " " + name);
            info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = ProcessWindowStyle.Hidden;
            started = DateTime.UtcNow;
            try { child = Process.Start(info); CabinetHelperActivity.Track(child); log("schema " + id + ": started idle request"); }
            catch { stop.Dispose(); stop = null; idleAfter = DateTime.UtcNow.AddMinutes(1); throw; }
        }
        return null;
    }
    public static void Fetch(string id, Func<bool> cancelled, Action<string> log)
    {
        uint parsed;
        if (!uint.TryParse(id, out parsed) || parsed == 0) throw new ArgumentException("Invalid app id");
        bool created;
        using (var mutex = new Mutex(true, "Local\\Cabinet-shadow-app-" + id, out created))
        {
            if (!created || cancelled() || !Playtime.SteamRunning()) return;
            string outcome = "timeout";
            // Persist before connection, so crashes cannot cause a rapid retry loop.
            WorkshopEngine.AtomicWrite(OutcomePath(id), DateTime.UtcNow.Ticks + "|attempting");
            try
            {
                Syncer.BackupSteamFiles(id, Syncer.AccountId());
                var end = DateTime.UtcNow.AddSeconds(8);
                using (var session = new SteamSession(id, log))
                {
                    while (DateTime.UtcNow < end && !cancelled() && Playtime.SteamRunning())
                    {
                        session.Pump();
                        if (File.Exists(SchemaPath(id)) || session.StatsReceivedResult != 0) break;
                        Thread.Sleep(100);
                    }
                    outcome = File.Exists(SchemaPath(id)) ? "found" : "no schema, callback=" + session.StatsReceivedResult;
                    if (cancelled() || !Playtime.SteamRunning()) outcome = "interrupted";
                }
            }
            catch (Exception ex) { outcome = "failed: " + ex.Message; }
            if (outcome == "interrupted") { CabinetFiles.EnsureSafePath(OutcomePath(id)); File.Delete(OutcomePath(id)); }
            else WorkshopEngine.AtomicWrite(OutcomePath(id), DateTime.UtcNow.Ticks + "|" + outcome);
            log("schema " + id + ": " + outcome);
        }
    }
    public void Dispose()
    {
        if (stop != null) { stop.Set(); stop.Dispose(); }
        if (child != null) { if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(5000); } child.Dispose(); child = null; }
    }
}

// ---------- Tray app ----------
