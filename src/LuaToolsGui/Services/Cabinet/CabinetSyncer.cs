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
class SyncResult
{
    public string AppId;
    public string GameName;
    public int NewAchievements;
    public int StatsChanged;
    public string Skipped;   // reason, if nothing could be done
}

static class Syncer
{
    public static string OnlineFixDir => CabinetPaths.Current.OnlineFixRoot;

    public static string SteamPath()
    {
        if (CabinetPaths.Current.SteamRoot is { } root) return root;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            var p = k == null ? null : k.GetValue("SteamPath") as string;
            return string.IsNullOrEmpty(p) ? @"C:\Program Files (x86)\Steam" : p.Replace('/', '\\');
        }
    }

    public static string StatsDir() { return Path.Combine(SteamPath(), @"appcache\stats"); }

    public static void BackupSteamFiles(string appId, string account)
    {
        CabinetPaths.Numeric(appId); CabinetPaths.Numeric(account);
        var dir = Path.Combine(CabinetPaths.Current.DataRoot, "backup");
        CabinetFiles.EnsureSafePath(dir);
        Directory.CreateDirectory(dir);
        foreach (var file in new[] { Path.Combine(StatsDir(), "UserGameStats_" + account + "_" + appId + ".bin"),
            Path.Combine(SteamPath(), "userdata", account, "config", "localconfig.vdf") })
        {
            CabinetFiles.EnsureSafePath(file);
            if (!File.Exists(file)) continue;
            string name = Path.GetFileName(file) == "localconfig.vdf" ? "localconfig_" + account + ".vdf" : Path.GetFileName(file);
            var backup = Path.Combine(dir, name);
            if (!File.Exists(backup)) File.Copy(file, backup);
        }
    }

    // Steam account id (32-bit) of the logged-in user, falling back to the most recently used one.
    public static string AccountId()
    {
        if (CabinetPaths.Current.AccountId is { } probe) return probe();
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
        {
            object v = k == null ? null : k.GetValue("ActiveUser");
            if (v is int && (int)v != 0) return ((uint)(int)v).ToString();
        }
        if (!Directory.Exists(StatsDir())) return null;
        CabinetFiles.EnsureSafePath(StatsDir());
        var newest = new DirectoryInfo(StatsDir()).GetFiles("UserGameStats_*_*.bin")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        return newest == null ? null : newest.Name.Split('_')[1];
    }

    public static IEnumerable<string> OnlineFixAppIds()
    {
        CabinetFiles.EnsureSafePath(OnlineFixDir);
        if (!Directory.Exists(OnlineFixDir)) yield break;
        foreach (var d in Directory.GetDirectories(OnlineFixDir))
        {
            var name = Path.GetFileName(d);
            if (uint.TryParse(name, out var id) && id != 0 && name == id.ToString(CultureInfo.InvariantCulture) && Directory.Exists(Path.Combine(d, "Stats"))) { CabinetFiles.EnsureSafePath(Path.Combine(d, "Stats")); yield return name; }
        }
    }

    // Fingerprint of the OnlineFix stat files, used to detect changes.
    public static string Fingerprint(string appId)
    {
        CabinetPaths.Numeric(appId);
        var sb = new StringBuilder();
        foreach (var f in new[] { "Achievements.ini", "Stats.ini" })
        {
            var p = Path.Combine(OnlineFixDir, appId, "Stats", f);
            CabinetFiles.EnsureSafePath(p);
            if (File.Exists(p)) { var fi = new FileInfo(p); sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(';'); }
        }
        var schema = Path.Combine(StatsDir(), "UserGameStatsSchema_" + appId + ".bin");
        sb.Append(File.Exists(schema) ? "s" : "-");
        return sb.ToString();
    }

    internal static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        CabinetFiles.EnsureSafePath(path);
        if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Oversized OnlineFix INI");
        var res = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return res;
        Dictionary<string, string> cur = null;
        foreach (var raw in ReadAllLinesShared(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line.EndsWith("]"))
            {
                var sec = line.Substring(1, line.Length - 2);
                if (!res.TryGetValue(sec, out cur)) { cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); res[sec] = cur; }
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq > 0 && cur != null) cur[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return res;
    }

    static string[] ReadAllLinesShared(string path)
    {
        return CabinetFiles.ReadLines(path);
    }

    // Schema stat types appear as names ("INT") or numbers ("1"). AVGRATE is not synced.
    internal static int StatType(string s)
    {
        int n;
        if (int.TryParse(s, out n)) return n;
        switch ((s ?? "").ToUpperInvariant())
        {
            case "INT": return 1;
            case "FLOAT": return 2;
            case "AVGRATE": return 3;
            case "ACHIEVEMENTS": return 4;
            default: return 0;
        }
    }

    public static SyncResult Sync(string appId, string accountId)
    {
        CabinetPaths.Numeric(appId); CabinetPaths.Numeric(accountId);
        var r = new SyncResult { AppId = appId, GameName = appId };
        var statsDir = StatsDir();
        var schemaPath = Path.Combine(statsDir, "UserGameStatsSchema_" + appId + ".bin");
        CabinetFiles.EnsureSafePath(schemaPath);
        CabinetFiles.EnsureSafePath(Path.Combine(OnlineFixDir, appId, "Stats"));
        if (!File.Exists(schemaPath)) { r.Skipped = "no Steam schema yet (open the game's page in Steam once)"; return r; }

        var schemaRoot = KV.Read(CabinetFiles.ReadBytes(schemaPath));
        var game = schemaRoot.Children.FirstOrDefault();
        if (game == null) { r.Skipped = "empty schema"; return r; }
        var gn = game.Get("gamename");
        if (gn != null && !string.IsNullOrEmpty(gn.Str())) r.GameName = gn.Str();
        var stats = game.Get("stats");
        if (stats == null || stats.Children == null) { r.Skipped = "schema has no stats"; return r; }

        // name -> (statId, bit) for achievements; name -> (statId, type) for stats
        var achMap = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.OrdinalIgnoreCase);
        var statMap = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in stats.Children)
        {
            if (st.Children == null) continue;
            var bits = st.Get("bits");
            if (bits != null && bits.Children != null)
            {
                foreach (var b in bits.Children)
                {
                    var nm = b.Get("name");
                    var bitKv = b.Get("bit");
                    int bit;
                    if (nm == null || !int.TryParse(bitKv != null ? bitKv.Str() : b.Key, out bit) || bit < 0 || bit > 31) continue;
                    achMap[nm.Str()] = new KeyValuePair<string, int>(st.Key, bit);
                }
            }
            else
            {
                var nm = st.Get("name");
                var tp = st.Get("type");
                int type = tp == null ? 0 : StatType(tp.Str());
                if (nm != null && (type == 1 || type == 2))
                    statMap[nm.Str()] = new KeyValuePair<string, int>(st.Key, type);
            }
        }

        var userPath = Path.Combine(statsDir, "UserGameStats_" + accountId + "_" + appId + ".bin");
        CabinetFiles.EnsureSafePath(userPath);
        KV userRoot;
        byte[] original = null;
        if (File.Exists(userPath)) { original = CabinetFiles.ReadBytes(userPath); userRoot = KV.Read(original); }
        else userRoot = new KV(0, "", new List<KV>());
        if (userRoot.Children.Count == 0)
        {
            var c = new KV(0, "cache", new List<KV>());
            c.SetInt("crc", 0);
            c.SetInt("PendingChanges", 0);
            userRoot.Children.Add(c);
        }
        var cache = userRoot.Children[0];

        // Achievements
        var achIni = ReadIni(Path.Combine(OnlineFixDir, appId, "Stats", "Achievements.ini"));
        foreach (var sec in achIni)
        {
            string achieved;
            if (!sec.Value.TryGetValue("achieved", out achieved) || !achieved.Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            KeyValuePair<string, int> loc;
            if (!achMap.TryGetValue(sec.Key, out loc)) continue;

            var stat = cache.GetOrAddSub(loc.Key);
            var dataKv = stat.Get("data");
            int data = dataKv == null ? 0 : Convert.ToInt32(dataKv.Value);
            int mask = 1 << loc.Value;
            if ((data & mask) != 0) continue;

            stat.SetInt("data", data | mask);
            string ts;
            int unix;
            if (!sec.Value.TryGetValue("timestamp", out ts) || !int.TryParse(ts, out unix) || unix <= 0)
                unix = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            stat.GetOrAddSub("AchievementTimes").SetInt(loc.Value.ToString(), unix);
            r.NewAchievements++;
        }

        // Stats (type 1 = int, 2 = float)
        var statIni = ReadIni(Path.Combine(OnlineFixDir, appId, "Stats", "Stats.ini"));
        Dictionary<string, string> statSec;
        if (statIni.TryGetValue("Stats", out statSec))
        {
            foreach (var kv in statSec)
            {
                KeyValuePair<string, int> loc;
                if (!statMap.TryGetValue(kv.Key, out loc)) continue;
                var stat = cache.GetOrAddSub(loc.Key);
                var cur = stat.Get("data");
                if (loc.Value == 2)
                {
                    float f;
                    if (!float.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out f) || !float.IsFinite(f)) continue;
                    if (cur != null && cur.Type == 3 && (float)cur.Value == f) continue;
                    stat.SetFloat("data", f);
                }
                else
                {
                    long l;
                    if (!long.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) continue;
                    if (l < int.MinValue || l > int.MaxValue) continue;
                    int i = (int)l;
                    if (cur != null && cur.Type == 2 && (int)cur.Value == i) continue;
                    stat.SetInt("data", i);
                }
                r.StatsChanged++;
            }
        }

        if (r.NewAchievements == 0 && r.StatsChanged == 0) return r;

        // One-time backup of Steam's original file, then atomic replace.
        if (original != null)
        {
            var bdir = Path.Combine(CabinetPaths.Current.DataRoot, "backup");
            CabinetFiles.EnsureSafePath(bdir);
            Directory.CreateDirectory(bdir);
            var bpath = Path.Combine(bdir, Path.GetFileName(userPath));
            CabinetFiles.EnsureSafePath(bpath);
            if (!File.Exists(bpath)) File.WriteAllBytes(bpath, original);
        }
        var tmp = userPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        CabinetFiles.EnsureSafePath(tmp);
        File.WriteAllBytes(tmp, userRoot.Write());
        CabinetFiles.EnsureSafePath(userPath);
        if (original != null && !CabinetFiles.ReadBytes(userPath).SequenceEqual(original)) { File.Delete(tmp); throw new IOException("Steam changed stats before commit"); }
        if (File.Exists(userPath)) File.Replace(tmp, userPath, null); else File.Move(tmp, userPath);
        return r;
    }
}

// ---------- Isolated Steam IPC session (never loads OnlineFix DLLs) ----------
// Interface versions/Windows vtable order verified against SAM.API and Valve headers.
// This is a minimal independent binding, not a copy of SAM's implementation.
