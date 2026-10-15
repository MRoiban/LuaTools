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
class Playtime
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    class Entry { public double PendingSec; public int InflightMin; public int Expected; public long AppliedAt; public long LastPlayed; public long PendingCloud, InflightCloud, CreditedCloud; public bool ApplyIntent; }

    readonly string path;
    readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    readonly HashSet<string> cloudReceipts = new HashSet<string>(StringComparer.Ordinal);
    Dictionary<string, string> gameDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // game root -> appid
    DateTime lastScan = DateTime.MinValue, lastTick = DateTime.UtcNow;
    public readonly Action<string> Log;

    public Playtime(string path, Action<string> log) { this.path = path; Log = log; Load(); }

    public static long Unix(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1)).TotalSeconds; }

    internal static bool SteamRunning()
    {
        return CabinetPaths.Current.SteamRunning();
    }

    static string LocalConfig(string account) { return Path.Combine(Syncer.SteamPath(), "userdata", account, "config", "localconfig.vdf"); }

    internal static IEnumerable<string> Libraries()
    {
        var steam = Syncer.SteamPath();
        CabinetFiles.EnsureSafePath(steam);
        yield return steam;
        var lf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        CabinetFiles.EnsureSafePath(lf);
        if (!File.Exists(lf)) yield break;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(CabinetFiles.ReadText(lf), "\"path\"\\s+\"([^\"]+)\""))
        {
            var p = m.Groups[1].Value.Replace(@"\\", @"\");
            if (!string.Equals(Path.GetFullPath(p).TrimEnd('\\'), Path.GetFullPath(steam).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) yield return p;
        }
    }

    // Maps each game folder containing an OnlineFix.ini to its RealAppId.
    void ScanGames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in Libraries())
        {
            var common = Path.Combine(lib, @"steamapps\common");
            if (!Directory.Exists(common)) continue;
            foreach (var game in Directory.GetDirectories(common))
            {
                var ini = FindIni(game, 0);
                if (ini == null) continue;
                var parsed = Syncer.ReadIni(ini);
                if (parsed.TryGetValue("Main", out var main) && main.TryGetValue("RealAppId", out var real))
                {
                    // Reject a malformed full value; never accept a numeric prefix of a path.
                    if (uint.TryParse(real, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && id != 480 && real == id.ToString(CultureInfo.InvariantCulture))
                        map[game.TrimEnd('\\') + "\\"] = real;
                }
            }
        }
        gameDirs = map;
    }

    static string FindIni(string dir, int depth)
    {
        try
        {
            CabinetFiles.EnsureSafePath(dir);
            var f = Path.Combine(dir, "OnlineFix.ini");
            CabinetFiles.EnsureSafePath(f);
            if (File.Exists(f)) return f;
            if (depth >= 4) return null;
            foreach (var d in Directory.GetDirectories(dir)) { var r = FindIni(d, depth + 1); if (r != null) return r; }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return null;
    }

    static string ImagePath(Process p)
    {
        IntPtr h = OpenProcess(0x1000, false, p.Id);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // Steam marks apps it is tracking itself; don't double count those.
    internal static bool SteamCounting(string appId)
    {
        CabinetPaths.Numeric(appId);
        if (CabinetPaths.Current.SteamCounting is { } probe) return probe(appId);
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps\" + appId))
        {
            object v = k == null ? null : k.GetValue("Running");
            return v is int && (int)v == 1;
        }
    }

    public void RefreshGames() { ScanGames(); lastScan = DateTime.UtcNow; }

    public IEnumerable<string> GameDirs() { return gameDirs.Select(g => g.Value + "  " + g.Key); }

    // Process start times disambiguate launches seen in the same five-second tick.
    public string LatestRunningApp(HashSet<string> running)
    {
        if (running.Count == 0) return null;
        string latest = null; DateTime start = DateTime.MinValue;
        foreach (var p in Process.GetProcesses()) using (p)
        {
            try
            {
                string image = ImagePath(p);
                if (image == null) continue;
                foreach (var game in gameDirs)
                    if (running.Contains(game.Value) && image.StartsWith(game.Key, StringComparison.OrdinalIgnoreCase) && p.StartTime.ToUniversalTime() > start)
                    { latest = game.Value; start = p.StartTime.ToUniversalTime(); }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return latest;
    }

    public HashSet<string> RunningApps()
    {
        if (CabinetPaths.Current.RunningApps is { } probe) return probe();
        var res = new HashSet<string>();
        if (gameDirs.Count == 0) return res;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var img = ImagePath(p);
                if (img == null) continue;
                foreach (var g in gameDirs)
                    if (img.StartsWith(g.Key, StringComparison.OrdinalIgnoreCase)) { res.Add(g.Value); break; }
            }
        }
        return res;
    }

    Entry Get(string appId)
    {
        Entry e;
        if (!entries.TryGetValue(appId, out e)) { e = new Entry(); entries[appId] = e; }
        return e;
    }

    // Receipt and pending provenance are committed together; failed writes are retryable.
    public bool QueueCloudMinutes(string receiptId, string appId, int minutes)
    {
        if (string.IsNullOrEmpty(receiptId) || receiptId.IndexOfAny(new[] { '|', '\r', '\n' }) >= 0 ||
            string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit) || minutes <= 0) return false;
        if (cloudReceipts.Contains(receiptId)) return true;
        var e = Get(appId);
        double before = e.PendingSec; long cloudBefore = e.PendingCloud;
        try
        {
            e.PendingSec += (double)minutes * 60; e.PendingCloud = checked(e.PendingCloud + minutes);
            cloudReceipts.Add(receiptId); Save(); return true;
        }
        catch (Exception ex)
        {
            e.PendingSec = before; e.PendingCloud = cloudBefore; cloudReceipts.Remove(receiptId);
            Log("cloud ledger deferred: " + ex.Message); return false;
        }
    }

    public long CloudMinutesInSteam(string appId)
    {
        Entry e;
        return entries.TryGetValue(appId, out e) ? e.CreditedCloud + e.InflightCloud : 0;
    }

    // Recover an interrupted VDF write before a cloud snapshot can inspect provenance.
    void RecoverApply(string account) { RecoverApplyConfig(LocalConfig(account)); }
    void RecoverApplyConfig(string cfg)
    {
        if (!entries.Values.Any(e => e.ApplyIntent)) return;
        CabinetFiles.EnsureSafePath(cfg);
        if (!File.Exists(cfg)) return;
        var vdf = new VdfText(CabinetFiles.ReadText(cfg, new UTF8Encoding(false)));
        vdf.Validate();
        foreach (var kv in entries.Where(k => k.Value.ApplyIntent))
        {
            int current; int.TryParse(vdf.GetAppValue(kv.Key, "Playtime"), out current);
            var e = kv.Value;
            if (current < e.Expected)
            {
                e.PendingSec += (double)e.InflightMin * 60; e.PendingCloud += e.InflightCloud;
                e.InflightMin = 0; e.InflightCloud = 0;
            }
            e.ApplyIntent = false;
        }
        Save();
    }

    // Called every few seconds. Returns a message to show the user, or null.
    public List<string> LastAdded { get; } = new();
    public string Tick(string account)
    {
        LastAdded.Clear();
        var now = DateTime.UtcNow;
        double delta = Math.Min((now - lastTick).TotalSeconds, 30);   // cap: sleep/hibernate gaps don't count
        lastTick = now;
        if (now - lastScan > TimeSpan.FromMinutes(10)) { ScanGames(); lastScan = now; }

        bool dirty = false;
        foreach (var appId in RunningApps())
        {
            if (SteamCounting(appId)) continue;
            var e = Get(appId);
            e.PendingSec += delta;
            e.LastPlayed = Unix(now);
            dirty = true;
        }

        string msg = null;
        if (account != null)
        {
            RecoverApply(account);
            if (!SteamRunning()) msg = Apply(account);
            else Verify(account);
        }
        if (dirty || msg != null) Save();
        return msg;
    }

    // Adds pending minutes to localconfig.vdf. Only call while Steam is closed.
    string Apply(string account)
    {
        return ApplyConfig(LocalConfig(account), Path.Combine(CabinetPaths.Current.DataRoot, "backup", "localconfig_" + account + ".vdf"));
    }
    string ApplyConfig(string cfg, string bak)
    {
        LastAdded.Clear();
        var pending = entries.Where(kv => kv.Value.PendingSec >= 60 && kv.Value.InflightMin == 0 && !kv.Value.ApplyIntent).ToList();
        if (pending.Count == 0) return null;
        CabinetFiles.EnsureSafePath(cfg);
        if (!File.Exists(cfg)) return null;

        CabinetFiles.EnsureSafePath(bak);
        Directory.CreateDirectory(Path.GetDirectoryName(bak));
        if (!File.Exists(bak)) File.Copy(cfg, bak);
        byte[] original = CabinetFiles.ReadBytes(cfg);

        var vdf = new VdfText(CabinetFiles.ReadText(cfg, new UTF8Encoding(false)));
        vdf.Validate();
        var added = new List<string>();
        foreach (var kv in pending)
        {
            var e = kv.Value;
            int minutes = (int)Math.Min(int.MaxValue, e.PendingSec / 60);
            int cur, cur2;
            int.TryParse(vdf.GetAppValue(kv.Key, "Playtime"), out cur);
            int.TryParse(vdf.GetAppValue(kv.Key, "Playtime2wks"), out cur2);
            long lp;
            long.TryParse(vdf.GetAppValue(kv.Key, "LastPlayed"), out lp);
            minutes = Math.Min(minutes, int.MaxValue - Math.Max(0, cur));
            if (minutes <= 0) continue;
            if (!vdf.SetAppValue(kv.Key, "Playtime", (cur + minutes).ToString())) return null;
            vdf.SetAppValue(kv.Key, "Playtime2wks", Math.Min(int.MaxValue, (long)cur2 + minutes).ToString());
            if (e.LastPlayed > lp) vdf.SetAppValue(kv.Key, "LastPlayed", e.LastPlayed.ToString());
            e.PendingSec -= (double)minutes * 60;
            e.InflightMin = minutes;
            e.InflightCloud = Math.Min(e.PendingCloud, minutes); e.PendingCloud -= e.InflightCloud;
            e.ApplyIntent = true;
            e.Expected = cur + minutes;
            e.AppliedAt = Unix(DateTime.UtcNow);
            LastAdded.Add(kv.Key + "|" + minutes);
            added.Add(kv.Key + ": +" + FormatMin(minutes));
            Log("playtime " + kv.Key + ": " + cur + " -> " + (cur + minutes) + " min");
        }
        var tmp = cfg + "." + Guid.NewGuid().ToString("N") + ".tmp";
        CabinetFiles.EnsureSafePath(tmp);
        // Persist intent first: a crash on either side of the VDF replacement is recoverable.
        try
        {
            Save();
            if (SteamRunning()) throw new IOException("Steam restarted during playtime preparation");
            File.WriteAllText(tmp, vdf.Text, new UTF8Encoding(false));
            if (SteamRunning()) throw new IOException("Steam restarted before playtime commit");
            CabinetFiles.EnsureSafePath(cfg);
            if (!CabinetFiles.ReadBytes(cfg).SequenceEqual(original)) throw new IOException("Steam changed playtime before commit");
            File.Replace(tmp, cfg, null);
        }
        catch { RecoverApplyConfig(cfg); throw; }
        foreach (var kv in pending) kv.Value.ApplyIntent = false;
        Save();
        return L.T("Playtime added (shows when Steam starts):\n", "Temps de jeu ajouté (visible au démarrage de Steam) :\n") + string.Join("\n", added);
    }

    // After Steam has restarted and saved its config, make sure our minutes survived; re-queue if not.
    void Verify(string account) { VerifyConfig(LocalConfig(account)); }
    void VerifyConfig(string cfg)
    {
        CabinetFiles.EnsureSafePath(cfg);
        if (!File.Exists(cfg)) return;
        long mtime = Unix(File.GetLastWriteTimeUtc(cfg));
        VdfText vdf = null;
        foreach (var kv in entries)
        {
            var e = kv.Value;
            if (e.InflightMin == 0 || mtime < e.AppliedAt + 120) continue;
            if (vdf == null) vdf = new VdfText(CabinetFiles.ReadText(cfg, new UTF8Encoding(false)));
            int cur;
            int.TryParse(vdf.GetAppValue(kv.Key, "Playtime"), out cur);
            if (cur < e.Expected) { e.PendingSec += (double)e.InflightMin * 60; e.PendingCloud += e.InflightCloud; Log("playtime " + kv.Key + ": Steam dropped " + e.InflightMin + " min, re-queued"); }
            else e.CreditedCloud += e.InflightCloud;
            e.InflightMin = 0; e.InflightCloud = 0;
            Save();
        }
    }

    public int PendingMinutes(string id) => entries.TryGetValue(id, out var entry) ? (int)Math.Min(int.MaxValue, entry.PendingSec / 60) : 0;

    public int PendingMinutes() { return (int)Math.Min(int.MaxValue, entries.Values.Sum(e => e.PendingSec) / 60); }

    public static string FormatMin(int m) { return m >= 60 ? (m / 60) + "h " + (m % 60) + "m" : m + "m"; }

    void Load()
    {
        CabinetFiles.EnsureSafePath(path);
        if (!File.Exists(path)) return;
        foreach (var l in CabinetFiles.ReadLines(path))
        {
            var p = l.Split('|');
            if (p.Length == 2 && p[0] == "#cloud-receipt") { cloudReceipts.Add(p[1]); continue; }
            if (p.Length < 6) continue;
            CabinetPaths.Numeric(p[0]);
            if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pending) || !double.IsFinite(pending) || pending < 0) throw new InvalidDataException("Invalid playtime ledger duration");
            entries[p[0]] = new Entry
            {
                PendingSec = double.Parse(p[1], CultureInfo.InvariantCulture),
                InflightMin = int.Parse(p[2]),
                Expected = int.Parse(p[3]),
                AppliedAt = long.Parse(p[4]),
                LastPlayed = long.Parse(p[5]),
                PendingCloud = p.Length > 6 ? long.Parse(p[6]) : 0,
                InflightCloud = p.Length > 7 ? long.Parse(p[7]) : 0,
                CreditedCloud = p.Length > 8 ? long.Parse(p[8]) : 0,
                ApplyIntent = p.Length > 9 && p[9] == "1"
            };
        }
    }

    void Save()
    {
        var lines = entries.Select(kv => string.Join("|", kv.Key,
            kv.Value.PendingSec.ToString(CultureInfo.InvariantCulture), kv.Value.InflightMin, kv.Value.Expected,
            kv.Value.AppliedAt, kv.Value.LastPlayed, kv.Value.PendingCloud, kv.Value.InflightCloud,
            kv.Value.CreditedCloud, kv.Value.ApplyIntent ? "1" : "0"))
            .Concat(cloudReceipts.Select(r => "#cloud-receipt|" + r));
        WorkshopEngine.AtomicWrite(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }
}

// ---------- Idle schema discovery ----------
