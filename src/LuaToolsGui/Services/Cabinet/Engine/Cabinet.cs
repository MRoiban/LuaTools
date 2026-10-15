// Verbatim port of github.com/MRoiban/cabinet (Cabinet.cs). Keep in sync with upstream;
// LuaTools-specific changes are marked "LuaTools:".
#nullable disable
#pragma warning disable SYSLIB0014
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using CabinetEngine.Compat;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CabinetEngine
{
    // ---------- Localisation ----------
    // French when the Windows display language is French (override with CABINET_LANG=fr|en), English otherwise.
    static class L
    {
        public static readonly bool Fr = IsFrench();

        static bool IsFrench()
        {
            var forced = Environment.GetEnvironmentVariable("CABINET_LANG");
            if (!string.IsNullOrEmpty(forced)) return forced.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";
        }

        public static string T(string en, string fr) { return Fr ? fr : en; }

        // Hours with one decimal, using the local decimal separator in French ("74,5 h").
        public static string Hours(double h) { return h.ToString("0.#", Fr ? CultureInfo.GetCultureInfo("fr-FR") : CultureInfo.InvariantCulture) + " h"; }
    }

    // ---------- Binary KeyValues (Valve) ----------
    class KV
    {
        public byte Type;            // 0 subkey, 1 string, 2 int32, 3 float, 7 uint64, 10 int64
        public string Key;
        public object Value;         // List<KV> for subkeys
        public List<KV> Children { get { return Value as List<KV>; } }

        public KV(byte type, string key, object value) { Type = type; Key = key; Value = value; }

        public KV Get(string key)
        {
            var c = Children;
            if (c == null) return null;
            foreach (var k in c) if (string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }

        public KV GetOrAddSub(string key)
        {
            var k = Get(key);
            if (k == null) { k = new KV(0, key, new List<KV>()); Children.Add(k); }
            return k;
        }

        public void SetInt(string key, int value)
        {
            var k = Get(key);
            if (k == null) Children.Add(new KV(2, key, value));
            else { k.Type = 2; k.Value = value; }
        }

        public void SetFloat(string key, float value)
        {
            var k = Get(key);
            if (k == null) Children.Add(new KV(3, key, value));
            else { k.Type = 3; k.Value = value; }
        }

        public string Str()
        {
            if (Value == null || Value is List<KV>) return null;
            return Convert.ToString(Value, CultureInfo.InvariantCulture);
        }

        public static KV Read(byte[] data)
        {
            int pos = 0;
            var root = new KV(0, "", new List<KV>());
            ReadInto(data, ref pos, root.Children);
            return root;
        }

        static void ReadInto(byte[] d, ref int pos, List<KV> list)
        {
            while (pos < d.Length)
            {
                byte t = d[pos++];
                if (t == 8 || t == 11) return;
                string key = ReadCStr(d, ref pos);
                object val;
                switch (t)
                {
                    case 0: var sub = new List<KV>(); ReadInto(d, ref pos, sub); val = sub; break;
                    case 1: val = ReadCStr(d, ref pos); break;
                    case 2: case 4: case 6: val = BitConverter.ToInt32(d, pos); pos += 4; break;
                    case 3: val = BitConverter.ToSingle(d, pos); pos += 4; break;
                    case 7: val = BitConverter.ToUInt64(d, pos); pos += 8; break;
                    case 10: val = BitConverter.ToInt64(d, pos); pos += 8; break;
                    default: throw new InvalidDataException("Unknown KV type " + t + " at " + pos);
                }
                list.Add(new KV(t, key, val));
            }
        }

        static string ReadCStr(byte[] d, ref int pos)
        {
            int start = pos;
            while (d[pos] != 0) pos++;
            string s = Encoding.UTF8.GetString(d, start, pos - start);
            pos++;
            return s;
        }

        public byte[] Write()
        {
            var ms = new MemoryStream();
            foreach (var c in Children) WriteNode(ms, c);
            ms.WriteByte(8);
            return ms.ToArray();
        }

        static void WriteNode(Stream s, KV k)
        {
            s.WriteByte(k.Type);
            WriteCStr(s, k.Key);
            byte[] b;
            switch (k.Type)
            {
                case 0: foreach (var c in k.Children) WriteNode(s, c); s.WriteByte(8); return;
                case 1: WriteCStr(s, (string)k.Value); return;
                case 2: case 4: case 6: b = BitConverter.GetBytes((int)k.Value); break;
                case 3: b = BitConverter.GetBytes((float)k.Value); break;
                case 7: b = BitConverter.GetBytes((ulong)k.Value); break;
                case 10: b = BitConverter.GetBytes((long)k.Value); break;
                default: throw new InvalidDataException("Unknown KV type " + k.Type);
            }
            s.Write(b, 0, b.Length);
        }

        static void WriteCStr(Stream s, string str)
        {
            var b = Encoding.UTF8.GetBytes(str);
            s.Write(b, 0, b.Length);
            s.WriteByte(0);
        }
    }

    // ---------- Sync logic ----------
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
        public const string OnlineFixDir = @"C:\Users\Public\Documents\OnlineFix";

        public static string SteamPath()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                var p = k == null ? null : k.GetValue("SteamPath") as string;
                return string.IsNullOrEmpty(p) ? @"C:\Program Files (x86)\Steam" : p.Replace('/', '\\');
            }
        }

        public static string StatsDir() { return Path.Combine(SteamPath(), @"appcache\stats"); }

        public static void BackupSteamFiles(string appId, string account)
        {
            var dir = Path.Combine(App.DataDir, "backup");
            Directory.CreateDirectory(dir);
            foreach (var file in new[] { Path.Combine(StatsDir(), "UserGameStats_" + account + "_" + appId + ".bin"),
                Path.Combine(SteamPath(), "userdata", account, "config", "localconfig.vdf") })
            {
                if (!File.Exists(file)) continue;
                string name = Path.GetFileName(file) == "localconfig.vdf" ? "localconfig_" + account + ".vdf" : Path.GetFileName(file);
                var backup = Path.Combine(dir, name);
                if (!File.Exists(backup)) File.Copy(file, backup);
            }
        }

        // Steam account id (32-bit) of the logged-in user, falling back to the most recently used one.
        public static string AccountId()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                object v = k == null ? null : k.GetValue("ActiveUser");
                if (v is int && (int)v != 0) return ((uint)(int)v).ToString();
            }
            var newest = new DirectoryInfo(StatsDir()).GetFiles("UserGameStats_*_*.bin")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return newest == null ? null : newest.Name.Split('_')[1];
        }

        public static IEnumerable<string> OnlineFixAppIds()
        {
            if (!Directory.Exists(OnlineFixDir)) yield break;
            foreach (var d in Directory.GetDirectories(OnlineFixDir))
            {
                var name = Path.GetFileName(d);
                if (name.All(char.IsDigit) && Directory.Exists(Path.Combine(d, "Stats"))) yield return name;
            }
        }

        // Fingerprint of the OnlineFix stat files, used to detect changes.
        public static string Fingerprint(string appId)
        {
            var sb = new StringBuilder();
            foreach (var f in new[] { "Achievements.ini", "Stats.ini" })
            {
                var p = Path.Combine(OnlineFixDir, appId, "Stats", f);
                if (File.Exists(p)) { var fi = new FileInfo(p); sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(';'); }
            }
            var schema = Path.Combine(StatsDir(), "UserGameStatsSchema_" + appId + ".bin");
            sb.Append(File.Exists(schema) ? "s" : "-");
            return sb.ToString();
        }

        internal static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
        {
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
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                return sr.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
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
            var r = new SyncResult { AppId = appId, GameName = appId };
            var statsDir = StatsDir();
            var schemaPath = Path.Combine(statsDir, "UserGameStatsSchema_" + appId + ".bin");
            if (!File.Exists(schemaPath)) { r.Skipped = "no Steam schema yet (open the game's page in Steam once)"; return r; }

            var schemaRoot = KV.Read(File.ReadAllBytes(schemaPath));
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
                        if (nm == null || !int.TryParse(bitKv != null ? bitKv.Str() : b.Key, out bit)) continue;
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
            KV userRoot;
            byte[] original = null;
            if (File.Exists(userPath)) { original = File.ReadAllBytes(userPath); userRoot = KV.Read(original); }
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
                        if (!float.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) continue;
                        if (cur != null && cur.Type == 3 && (float)cur.Value == f) continue;
                        stat.SetFloat("data", f);
                    }
                    else
                    {
                        long l;
                        if (!long.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) continue;
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
                var bdir = Path.Combine(App.DataDir, "backup");
                Directory.CreateDirectory(bdir);
                var bpath = Path.Combine(bdir, Path.GetFileName(userPath));
                if (!File.Exists(bpath)) File.WriteAllBytes(bpath, original);
            }
            var tmp = userPath + ".tmp";
            File.WriteAllBytes(tmp, userRoot.Write());
            if (File.Exists(userPath)) File.Replace(tmp, userPath, null); else File.Move(tmp, userPath);
            return r;
        }
    }

    // ---------- Isolated Steam IPC session (never loads OnlineFix DLLs) ----------
    // Interface versions/Windows vtable order verified against SAM.API and Valve headers.
    // This is a minimal independent binding, not a copy of SAM's implementation.
    sealed class SteamSession : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateInterface([MarshalAs(UnmanagedType.LPStr)] string version, IntPtr error);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int CreatePipe(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int ConnectUser(IntPtr self, int pipe);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void ReleaseUser(IntPtr self, int pipe, int user);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool ReleasePipe(IntPtr self, int pipe);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate IntPtr GetInterface(IntPtr self, int user, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate IntPtr GetUtils(IntPtr self, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate uint GetAppId(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool SimpleBool(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetInt(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name, out int value);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetFloat(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name, out float value);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool SetInt(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name, int value);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool SetFloat(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name, float value);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetAchievement(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.I1)] out bool value);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool SetAchievement(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name);
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Callback { public int User, Id; public IntPtr Data; public int Size; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetCallback(int pipe, out Callback message, out int call);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeCallback(int pipe);
        IntPtr client, stats;
        int pipe, user;
        GetCallback getCallback;
        FreeCallback freeCallback;
        readonly Action<string> log;
        readonly string appId;
        EventWaitHandle activity;
        bool pendingStore;
        public int StatsReceivedResult { get; private set; }
        DateTime lastStore = DateTime.MinValue;

        // SteamFriends018 / SteamMatchMaking009: indices from the Valve headers.
        [StructLayout(LayoutKind.Sequential, Pack = 8)] struct FriendGame { public ulong Game; public uint IP; public ushort Port, QueryPort; public ulong Lobby; }
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool FriendPlayed(IntPtr self, ulong id, out FriendGame game);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate IntPtr PresenceGet(IntPtr self, ulong id, [MarshalAs(UnmanagedType.LPStr)] string key);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool PresenceSet(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string key, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void PresenceClear(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate ulong LobbyCreate(IntPtr self, int type, int members);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void LobbyLeave(IntPtr self, ulong lobby);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool CallResult(IntPtr self, ulong call, IntPtr buffer, int size, int expected, [MarshalAs(UnmanagedType.I1)] out bool failed);
        IntPtr friends;
        ulong localId;
        bool presenceOwned, joinDisabled;
        string presenceFingerprint;
        DateTime lastPresenceAttempt = DateTime.MinValue;

        void Friends()
        {
            if (friends != IntPtr.Zero) return;
            friends = Function<GetInterface>(client, 8)(client, user, pipe, "SteamFriends018");
            localId = 76561197960265728UL + uint.Parse(Syncer.AccountId(), CultureInfo.InvariantCulture);
        }
        string Presence(string key)
        {
            var p = Function<PresenceGet>(friends, 43)(friends, localId, key);
            if (p == IntPtr.Zero) return "";
            int count = 0;
            while (count < 256 && Marshal.ReadByte(p, count) != 0) count++;
            if (count == 256) return "";
            var bytes = new byte[count]; Marshal.Copy(p, bytes, 0, count);
            return Encoding.UTF8.GetString(bytes);
        }
        bool SetPresence(string key, string value)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (value != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(value + "\0");
                    buffer = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, buffer, bytes.Length);
                }
                return Function<PresenceSet>(friends, 41)(friends, key, buffer);
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
        public void JoinProbe(bool producer, int seconds)
        {
            Friends();
            IntPtr matchmaking = IntPtr.Zero; ulong lobby = 0;
            try
            {
                if (producer)
                {
                    matchmaking = Function<GetInterface>(client, 10)(client, user, pipe, "SteamMatchMaking009");
                    var utils = Function<GetUtils>(client, 9)(client, pipe, "SteamUtils011");
                    ulong call = Function<LobbyCreate>(matchmaking, 13)(matchmaking, 0, 2);
                    var buffer = Marshal.AllocHGlobal(16);
                    try
                    {
                        var until = DateTime.UtcNow.AddSeconds(10); bool failed;
                        while (DateTime.UtcNow < until)
                        {
                            Pump();
                            if (Function<CallResult>(utils, 12)(utils, call, buffer, 16, 513, out failed))
                            {
                                if (!failed && Marshal.ReadInt32(buffer) == 1) lobby = (ulong)Marshal.ReadInt64(buffer, 8);
                                log("join probe: lobby result=" + Marshal.ReadInt32(buffer) + " failed=" + failed + " lobby=" + lobby); break;
                            }
                            Thread.Sleep(100);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                log("join probe initial " + appId + ": connect=" + Presence("connect"));
                if (producer || appId != "480")
                {
                    string connect = producer && lobby != 0 ? "+connect_lobby " + lobby : "cabinet-probe";
                    foreach (var kv in new[] { new[] { "connect", connect }, new[] { "status", L.T("Cabinet join probe", "Test de connexion Cabinet") }, new[] { "steam_display", "#Status_InGame" } })
                        log("join probe: set " + kv[0] + " accepted=" + SetPresence(kv[0], kv[1]));
                    presenceOwned = true;
                }
                var end = DateTime.UtcNow.AddSeconds(Math.Min(45, seconds));
                while (DateTime.UtcNow < end)
                {
                    Pump(); FriendGame game;
                    bool ok = Function<FriendPlayed>(friends, 7)(friends, localId, out game);
                    log("join probe " + appId + ": local game readable=" + ok + " game=" + game.Game + " lobby=" + game.Lobby + " connect=" + Presence("connect") + " group=" + Presence("steam_player_group"));
                    Thread.Sleep(1000);
                }
            }
            finally
            {
                if (lobby != 0) { Function<LobbyLeave>(matchmaking, 15)(matchmaking, lobby); log("join probe: left test lobby"); }
                if (presenceOwned) { Function<PresenceClear>(friends, 42)(friends); presenceOwned = false; log("join probe: cleared presence"); }
            }
        }

        public string PresenceSnapshot()
        {
            Friends(); FriendGame game;
            bool played = Function<FriendPlayed>(friends, 7)(friends, localId, out game);
            string connect = Presence("connect");
            if (string.IsNullOrEmpty(connect) && played && game.Game == 480 && game.Lobby != 0) connect = "+connect_lobby " + game.Lobby;
            return new JavaScriptSerializer().Serialize(new Dictionary<string, string> {
                { "stamp", Playtime.Unix(DateTime.UtcNow).ToString(CultureInfo.InvariantCulture) },
                { "connect", connect }, { "steam_player_group", Presence("steam_player_group") },
                { "steam_player_group_size", Presence("steam_player_group_size") }, { "status", Presence("status") } });
        }
        public void MirrorProbe(string source, int seconds)
        {
            Friends();
            for (int i = 0; i < Math.Min(30, seconds); i++)
            {
                Pump(); MirrorJoin(source);
                log("mirror probe: real connect=" + Presence("connect"));
                Thread.Sleep(1000);
            }
        }
        public void CaptureProbe()
        {
            var screenshots = Function<GetInterface>(client, 18)(client, user, pipe, "STEAMSCREENSHOTS_INTERFACE_VERSION003");
            Function<PresenceClear>(screenshots, 2)(screenshots);
            log("capture probe: TriggerScreenshot called for " + appId);
            for (int i = 0; i < 50; i++) { Pump(); Thread.Sleep(100); }
        }

        public void MirrorJoin(string source)
        {
            if (joinDisabled) return;
            try
            {
                Friends();
                var keys = new[] { "connect", "steam_player_group", "steam_player_group_size", "status" };
                var values = new string[keys.Length];
                Dictionary<string, string> snapshot = null;
                if (source != null && File.Exists(source))
                {
                    snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(source));
                    long stamp; string text;
                    if (!snapshot.TryGetValue("stamp", out text) || !long.TryParse(text, out stamp) || Math.Abs(Playtime.Unix(DateTime.UtcNow) - stamp) > 5) snapshot = null;
                }
                for (int i = 0; i < keys.Length; i++)
                {
                    string value;
                    values[i] = snapshot != null && snapshot.TryGetValue(keys[i], out value) && value != null && Encoding.UTF8.GetByteCount(value) < 256 ? value : "";
                }
                string fingerprint = string.Join("\n", values);
                if (fingerprint == presenceFingerprint) return;
                if (values.All(string.IsNullOrEmpty))
                {
                    if (presenceOwned) { Function<PresenceClear>(friends, 42)(friends); presenceOwned = false; }
                    presenceFingerprint = fingerprint;
                    return;
                }
                if (DateTime.UtcNow - lastPresenceAttempt < TimeSpan.FromSeconds(5)) return;
                lastPresenceAttempt = DateTime.UtcNow;
                bool accepted = true;
                for (int i = 0; i < keys.Length; i++)
                    if (!SetPresence(keys[i], values[i].Length == 0 ? null : values[i])) { accepted = false; log("shadow " + appId + ": rich presence rejected: " + keys[i]); }
                presenceOwned = true;
                if (accepted) { presenceFingerprint = fingerprint; log("shadow " + appId + ": mirrored join presence"); }
            }
            catch (IOException) { } // Atomic relay replacement can briefly race a read.
            catch (Exception ex)
            {
                joinDisabled = true;
                if (presenceOwned) { try { Function<PresenceClear>(friends, 42)(friends); } catch { } presenceOwned = false; }
                log("shadow " + appId + ": join mirroring unavailable: " + ex.Message);
            }
        }

        static T Function<T>(IntPtr obj, int index) where T : class
        {
            if (obj == IntPtr.Zero) throw new InvalidOperationException("Steam interface unavailable");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size), typeof(T));
        }
        static T Export<T>(IntPtr module, string name) where T : class
        {
            var p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new InvalidOperationException("Missing Steam export " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }
        public SteamSession(string id, Action<string> logger)
        {
            appId = id; log = logger;
            Environment.SetEnvironmentVariable("SteamAppId", id);
            Environment.SetEnvironmentVariable("SteamGameId", id);
            var module = LoadLibraryEx(Path.Combine(Syncer.SteamPath(), IntPtr.Size == 8 ? "steamclient64.dll" : "steamclient.dll"), IntPtr.Zero, 8);
            if (module == IntPtr.Zero) throw new InvalidOperationException("Load steamclient failed: " + Marshal.GetLastWin32Error());
            try
            {
                activity = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\Cabinet-shadow-active");
                getCallback = Export<GetCallback>(module, "Steam_BGetCallback");
                freeCallback = Export<FreeCallback>(module, "Steam_FreeLastCallback");
                client = Export<CreateInterface>(module, "CreateInterface")("SteamClient018", IntPtr.Zero);
                pipe = Function<CreatePipe>(client, 0)(client);
                if (pipe == 0) throw new InvalidOperationException("CreateSteamPipe failed");
                user = Function<ConnectUser>(client, 2)(client, pipe);
                if (user == 0) throw new InvalidOperationException("ConnectToGlobalUser failed");
                var utils = Function<GetUtils>(client, 9)(client, pipe, "SteamUtils005");
                uint actual = Function<GetAppId>(utils, 9)(utils);
                if (actual.ToString() != id) throw new InvalidOperationException("Steam app mismatch: " + actual);
                stats = Function<GetInterface>(client, 13)(client, user, pipe, "STEAMUSERSTATS_INTERFACE_VERSION013");
                if (stats == IntPtr.Zero) throw new InvalidOperationException("SteamUserStats013 unavailable");
                log("shadow " + id + ": connected, app id verified, " + (IntPtr.Size * 8) + " bit");
            }
            catch { Dispose(); throw; }
        }
        public void Pump()
        {
            Callback c; int call;
            for (int i = 0; i < 100 && getCallback(pipe, out c, out call); i++)
            {
                try
                {
                    if (c.Id == 2301 && c.Size >= 8) log("capture probe: ScreenshotReady handle=" + Marshal.ReadInt32(c.Data) + " result=" + Marshal.ReadInt32(c.Data, 4));
                    if (c.Id == 333 || c.Id == 337) log("shadow " + appId + ": join callback " + c.Id + " received; cross-app callback forwarding is unavailable");
                    if ((c.Id == 1101 || c.Id == 1102) && c.Size >= 12)
                    {
                        log("shadow " + appId + ": callback " + c.Id + " app=" + Marshal.ReadInt64(c.Data) + " result=" + Marshal.ReadInt32(c.Data, 8));
                        if (c.Id == 1101 && Marshal.ReadInt64(c.Data) == long.Parse(appId)) StatsReceivedResult = Marshal.ReadInt32(c.Data, 8);
                        if (c.Id == 1102 && Marshal.ReadInt64(c.Data) == long.Parse(appId) && Marshal.ReadInt32(c.Data, 8) != 1) pendingStore = true;
                    }
                }
                finally { freeCallback(pipe); }
            }
        }
        public void Sync(bool final)
        {
            var schemaPath = Path.Combine(Syncer.StatsDir(), "UserGameStatsSchema_" + appId + ".bin");
            if (!File.Exists(schemaPath)) return;
            var game = KV.Read(File.ReadAllBytes(schemaPath)).Children.FirstOrDefault();
            var schema = game == null ? null : game.Get("stats");
            if (schema == null || schema.Children == null) return;
            var achievements = Syncer.ReadIni(Path.Combine(Syncer.OnlineFixDir, appId, "Stats", "Achievements.ini"));
            var ini = Syncer.ReadIni(Path.Combine(Syncer.OnlineFixDir, appId, "Stats", "Stats.ini"));
            Dictionary<string, string> values;
            ini.TryGetValue("Stats", out values);
            int changed = 0, unavailable = 0, checkedAchievements = 0;
            foreach (var st in schema.Children)
            {
                if (st.Children == null) continue;
                var bits = st.Get("bits");
                if (bits != null && bits.Children != null)
                {
                    foreach (var bit in bits.Children)
                    {
                        var name = bit.Get("name"); Dictionary<string, string> entry; string achieved; bool current;
                        if (name == null || !achievements.TryGetValue(name.Str(), out entry) || !entry.TryGetValue("achieved", out achieved) || !achieved.Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!Function<GetAchievement>(stats, 5)(stats, name.Str(), out current)) { unavailable++; continue; }
                        checkedAchievements++;
                        if (!current && Function<SetAchievement>(stats, 6)(stats, name.Str())) changed++;
                    }
                }
                else
                {
                    var name = st.Get("name"); var type = st.Get("type"); string value;
                    if (name == null || type == null || values == null || !values.TryGetValue(name.Str(), out value)) continue;
                    if (Syncer.StatType(type.Str()) == 1)
                    {
                        int next, current;
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out next)) continue;
                        if (!Function<GetInt>(stats, 1)(stats, name.Str(), out current)) { unavailable++; continue; }
                        // Absolute values, matching the existing file sync (some stats decrease).
                        if (next != current && Function<SetInt>(stats, 3)(stats, name.Str(), next)) changed++;
                    }
                    else if (Syncer.StatType(type.Str()) == 2)
                    {
                        float next, current;
                        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out next) || float.IsNaN(next) || float.IsInfinity(next)) continue;
                        if (!Function<GetFloat>(stats, 0)(stats, name.Str(), out current)) { unavailable++; continue; }
                        if (next != current && Function<SetFloat>(stats, 2)(stats, name.Str(), next)) changed++;
                    }
                }
            }
            if (changed > 0) { pendingStore = true; log("shadow " + appId + ": " + changed + " live changes"); }
            if (final) log("shadow " + appId + ": final sync, " + checkedAchievements + " earned achievements read from live client");
            if (unavailable > 0 && final) log("shadow " + appId + ": " + unavailable + " values unavailable in client; file fallback retained");
            if (pendingStore && (final || DateTime.UtcNow - lastStore >= TimeSpan.FromSeconds(60)))
            {
                bool accepted = Function<SimpleBool>(stats, 9)(stats);
                log("shadow " + appId + ": StoreStats accepted=" + accepted);
                if (accepted) pendingStore = false;
                lastStore = DateTime.UtcNow;
            }
        }
        public void Dispose()
        {
            if (presenceOwned && friends != IntPtr.Zero) { try { Function<PresenceClear>(friends, 42)(friends); } catch (Exception ex) { log("shadow presence clear: " + ex.Message); } presenceOwned = false; }
            if (client != IntPtr.Zero && pipe != 0)
            {
                if (user != 0) { Function<ReleaseUser>(client, 4)(client, pipe, user); user = 0; }
                Function<ReleasePipe>(client, 1)(client, pipe); pipe = 0;
            }
            if (activity != null) { activity.Dispose(); activity = null; }
        }
    }

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
            catch (WaitHandleCannotBeOpenedException) { return Syncer.OnlineFixAppIds().Any(HelperActive); }
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
                    var info = new ProcessStartInfo(Application.ExecutablePath, "--shadow " + id + " " + parent.Id + " " + parent.StartTime.ToUniversalTime().Ticks + " " + name);
                    parent.Dispose();
                    info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = ProcessWindowStyle.Hidden;
                    children[id] = new Child { Process = Process.Start(info), Stop = stop };
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
            foreach (var child in children.Values) { child.Process.Dispose(); child.Stop.Dispose(); }
            children.Clear();
        }
    }

    // ---------- Text VDF (localconfig.vdf) minimal editor ----------
    // Edits values in place so the rest of Steam's file stays byte-identical.
    class VdfText
    {
        struct Tok { public char Kind; public int Start, End; public string Text; }   // Kind: 's' string, '{', '}'

        public string Text;
        List<Tok> toks;

        public VdfText(string text) { Text = text; Tokenize(); }

        void Tokenize()
        {
            toks = new List<Tok>();
            var t = Text;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '{' || c == '}') toks.Add(new Tok { Kind = c, Start = i, End = i + 1 });
                else if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; }
                else if (c == '"')
                {
                    var sb = new StringBuilder();
                    int s = i++;
                    while (i < t.Length && t[i] != '"')
                    {
                        if (t[i] == '\\' && i + 1 < t.Length) { sb.Append(t[i + 1]); i += 2; continue; }
                        sb.Append(t[i++]);
                    }
                    toks.Add(new Tok { Kind = 's', Start = s, End = i + 1, Text = sb.ToString() });
                }
            }
        }

        int Match(int open)
        {
            int depth = 0;
            for (int i = open; i < toks.Count; i++)
            {
                if (toks[i].Kind == '{') depth++;
                else if (toks[i].Kind == '}' && --depth == 0) return i;
            }
            return -1;
        }

        // Finds a direct child of the block opened at token `open` (-1 = top level). Returns token index of the key.
        int Child(int open, string key)
        {
            int end = open < 0 ? toks.Count : Match(open);
            for (int i = open + 1; i < end; i++)
            {
                if (toks[i].Kind != 's') continue;
                if (i + 1 >= toks.Count) return -1;
                bool isMatch = string.Equals(toks[i].Text, key, StringComparison.OrdinalIgnoreCase);
                if (toks[i + 1].Kind == '{') { if (isMatch) return i; i = Match(i + 1); }
                else { if (isMatch) return i; i++; }
            }
            return -1;
        }

        int Block(params string[] path)
        {
            int open = -1;
            foreach (var p in path)
            {
                int k = Child(open, p);
                if (k < 0 || k + 1 >= toks.Count || toks[k + 1].Kind != '{') return -1;
                open = k + 1;
            }
            return open;
        }

        static readonly string[] AppsPath = { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps" };

        public string GetAppValue(string appId, string key)
        {
            int app = Block(AppsPath.Concat(new[] { appId }).ToArray());
            if (app < 0) return null;
            int k = Child(app, key);
            return k >= 0 && toks[k + 1].Kind == 's' ? toks[k + 1].Text : null;
        }

        public bool SetAppValue(string appId, string key, string value)
        {
            int apps = Block(AppsPath);
            if (apps < 0) return false;
            int depth = 0;
            for (int i = 0; i <= apps; i++) if (toks[i].Kind == '{') depth++; else if (toks[i].Kind == '}') depth--;
            int app = Block(AppsPath.Concat(new[] { appId }).ToArray());
            string ind = new string('\t', depth);
            if (app < 0)
            {
                int close = Match(apps);
                Text = Text.Insert(LineStart(toks[close].Start),
                    ind + "\"" + appId + "\"\n" + ind + "{\n" + ind + "\t\"" + key + "\"\t\t\"" + value + "\"\n" + ind + "}\n");
            }
            else
            {
                int k = Child(app, key);
                if (k >= 0 && toks[k + 1].Kind == 's')
                {
                    var v = toks[k + 1];
                    Text = Text.Substring(0, v.Start) + "\"" + value + "\"" + Text.Substring(v.End);
                }
                else
                {
                    int close = Match(app);
                    Text = Text.Insert(LineStart(toks[close].Start), ind + "\t\"" + key + "\"\t\t\"" + value + "\"\n");
                }
            }
            Tokenize();
            return true;
        }

        // Raw value edits retain every unrelated byte, including comments and whitespace.
        public string RawValue(params string[] path)
        {
            ValidateControllerText();
            int parent = path.Length == 1 ? -1 : Block(path.Take(path.Length - 1).ToArray());
            if (path.Length > 1 && parent < 0) return null;
            int key = Child(parent, path[path.Length - 1]);
            if (key < 0) return null;
            int end = toks[key + 1].Kind == '{' ? Match(key + 1) : key + 1;
            return Text.Substring(toks[key + 1].Start, toks[end].End - toks[key + 1].Start);
        }

        public string StringValue(params string[] path)
        {
            string raw = RawValue(path);
            if (raw == null || !raw.StartsWith("\"")) return null;
            var value = new VdfText("\"value\" " + raw);
            return value.toks[1].Text;
        }

        public void SetRawValue(string raw, params string[] path)
        {
            ValidateControllerText();
            if (raw != null) new VdfText("\"value\" " + raw).ValidateControllerText();
            int parent = -1;
            for (int n = 0; n < path.Length - 1; n++)
            {
                int key = Child(parent, path[n]);
                if (key < 0)
                {
                    if (raw == null) return;
                    if (parent < 0) throw new InvalidDataException("Missing VDF root");
                    Text = Text.Insert(toks[Match(parent)].Start, "\n\"" + path[n] + "\" { }\n");
                    Tokenize(); key = Child(parent, path[n]);
                }
                if (toks[key + 1].Kind != '{') throw new InvalidDataException("VDF path is not a block");
                parent = key + 1;
            }
            int k = Child(parent, path[path.Length - 1]);
            if (k >= 0)
            {
                int end = toks[k + 1].Kind == '{' ? Match(k + 1) : k + 1;
                int start = raw == null ? toks[k].Start : toks[k + 1].Start;
                Text = Text.Remove(start, toks[end].End - start);
                if (raw != null) Text = Text.Insert(start, raw);
            }
            else if (raw != null)
            {
                if (parent < 0) throw new InvalidDataException("Missing VDF root");
                Text = Text.Insert(toks[Match(parent)].Start, "\n\"" + path[path.Length - 1] + "\" " + raw + "\n");
            }
            Tokenize(); ValidateControllerText();
        }

        public void ValidateControllerText()
        {
            int depth = 0;
            for (int i = 0; i < toks.Count; i++)
            {
                var t = toks[i];
                if (t.End > Text.Length) throw new InvalidDataException("Truncated VDF string");
                if (t.Kind == '{') depth++;
                if (t.Kind == '}' && --depth < 0) throw new InvalidDataException("Unbalanced VDF");
                if (t.Kind == 's')
                {
                    if (++i >= toks.Count || (toks[i].Kind != 's' && toks[i].Kind != '{')) throw new InvalidDataException("Missing VDF value");
                    if (toks[i].End > Text.Length) throw new InvalidDataException("Truncated VDF value");
                    if (toks[i].Kind == '{') depth++;
                }
            }
            if (depth != 0) throw new InvalidDataException("Unbalanced VDF");
        }

        int LineStart(int pos)
        {
            while (pos > 0 && Text[pos - 1] != '\n') pos--;
            return pos;
        }

        // Move one indexed screenshot at a time, preserving all unrelated VDF text.
        internal bool RefileScreenshot(string remote, Func<long, string> assign, Action<string> log, Func<bool> canWrite, Action beforeWrite)
        {
            int screenshots = Block("screenshots"), source = Block("screenshots", "480");
            if (screenshots < 0 || source < 0) return false;
            int sourceEnd = Match(source);
            for (int i = source + 1; i < sourceEnd; i++)
            {
                if (toks[i].Kind != 's' || i + 1 >= sourceEnd || toks[i + 1].Kind != '{') throw new InvalidDataException("Unexpected screenshot VDF structure");
                int entry = i + 1, end = Match(entry);
                long created; string target;
                if (!long.TryParse(Value(entry, "creation"), out created) || (target = assign(created)) == null) { i = end; continue; }
                string uploaded = Value(entry, "hscreenshot");
                if (uploaded != null && uploaded != "0" && uploaded != "18446744073709551615") { i = end; continue; }
                string filename = Value(entry, "filename"), thumbnail = Value(entry, "thumbnail");
                if (Value(entry, "gameid") != "480" || filename == null || !filename.StartsWith("480/screenshots/", StringComparison.Ordinal) || (thumbnail != null && !thumbnail.StartsWith("480/screenshots/thumbnails/", StringComparison.Ordinal))) { i = end; continue; }
                var moves = new List<KeyValuePair<string, string>>();
                foreach (var rel in new[] { filename, thumbnail })
                {
                    if (string.IsNullOrEmpty(rel)) continue;
                    string from = SafeMediaPath(remote, rel), to = SafeMediaPath(remote, target + rel.Substring(3));
                    if (!File.Exists(from) || File.Exists(to)) { moves.Clear(); break; }
                    moves.Add(new KeyValuePair<string, string>(from, to));
                }
                if (moves.Count == 0) { i = end; continue; }
                int dest = Block("screenshots", target), number = 0;
                while (dest >= 0 && Child(dest, number.ToString(CultureInfo.InvariantCulture)) >= 0) number++;
                string block = Text.Substring(toks[entry].Start, toks[end].End - toks[entry].Start);
                var edits = new List<KeyValuePair<int, KeyValuePair<int, string>>>();
                foreach (var key in new[] { "gameid", "filename", "thumbnail" })
                {
                    int k = Child(entry, key);
                    if (k < 0) continue;
                    var value = toks[k + 1];
                    string next = key == "gameid" ? target : target + value.Text.Substring(3);
                    edits.Add(new KeyValuePair<int, KeyValuePair<int, string>>(value.Start - toks[entry].Start, new KeyValuePair<int, string>(value.End - value.Start, "\"" + next.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")));
                }
                foreach (var edit in edits.OrderByDescending(e => e.Key)) block = block.Remove(edit.Key, edit.Value.Key).Insert(edit.Key, edit.Value.Value);
                string moved = "\n\t\t\"" + number + "\"\n\t\t" + block + "\n";
                int insert = dest < 0 ? toks[Match(screenshots)].Start : toks[Match(dest)].Start;
                if (dest < 0) moved = "\n\t\"" + target + "\"\n\t{" + moved + "\t}\n";
                string nextText = Text.Insert(insert, moved);
                int removeStart = toks[i].Start, removeEnd = toks[end].End;
                if (insert <= removeStart) { removeStart += moved.Length; removeEnd += moved.Length; }
                nextText = nextText.Remove(removeStart, removeEnd - removeStart);
                // Copy first; originals survive a crash before the index is committed.
                var copied = new List<string>();
                try
                {
                    beforeWrite();
                    string backup = Path.Combine(Path.GetDirectoryName(remote), "cabinet-backup", Guid.NewGuid().ToString("N"));
                    foreach (var move in moves)
                    {
                        if (!canWrite()) throw new IOException("Steam restarted during screenshot preparation");
                        string original = Path.Combine(backup, move.Key.Substring(Path.GetFullPath(remote).TrimEnd('\\').Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(original)); File.Copy(move.Key, original);
                    }
                    foreach (var move in moves)
                    {
                        if (!canWrite()) throw new IOException("Steam restarted during screenshot preparation");
                        Directory.CreateDirectory(Path.GetDirectoryName(move.Value)); File.Copy(move.Key, move.Value); copied.Add(move.Value);
                    }
                }
                catch { foreach (var copy in copied) { try { File.Delete(copy); } catch { } } throw; }
                Text = nextText;
                Tokenize();
                // Deletion happens only after the caller atomically commits the index.
                PendingMediaDeletes = moves.Select(m => m.Key).ToArray();
                PendingMediaCopies = copied.ToArray();
                log("media: prepared screenshot for " + target);
                return true;
            }
            return false;
        }
        internal string[] PendingMediaDeletes;
        internal string[] PendingMediaCopies;
        string Value(int block, string key)
        {
            int k = Child(block, key);
            return k >= 0 && k + 1 < toks.Count && toks[k + 1].Kind == 's' ? toks[k + 1].Text : null;
        }
        static string SafeMediaPath(string root, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || relative.Contains(":") || relative.Split(new[] { '/', '\\' }).Any(p => p == "..")) throw new InvalidDataException("Unsafe screenshot path");
            EnsureMediaParents(full);
            return full;
        }
        internal static void EnsureMediaParents(string path)
        {
            for (string p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
                if (WorkshopEngine.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Media reparse points are unsupported");
        }
    }


    // ---------- Playtime ----------
    // Counts time while any process from an OnlineFix game's folder is running, then adds it to
    // Steam's localconfig.vdf while Steam is closed (Steam rewrites that file while it runs).
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
            var processes = Process.GetProcessesByName("steam");
            bool running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            return running;
        }

        static string LocalConfig(string account) { return Path.Combine(Syncer.SteamPath(), "userdata", account, "config", "localconfig.vdf"); }

        internal static IEnumerable<string> Libraries()
        {
            var steam = Syncer.SteamPath();
            yield return steam;
            var lf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (!File.Exists(lf)) yield break;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(lf), "\"path\"\\s+\"([^\"]+)\""))
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
                    var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(ini), @"RealAppId\s*=\s*(\d+)");
                    if (m.Success) map[game.TrimEnd('\\') + "\\"] = m.Groups[1].Value;
                }
            }
            gameDirs = map;
        }

        static string FindIni(string dir, int depth)
        {
            try
            {
                var f = Path.Combine(dir, "OnlineFix.ini");
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
            if (!File.Exists(cfg)) return;
            var vdf = new VdfText(File.ReadAllText(cfg, new UTF8Encoding(false)));
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
        public string Tick(string account)
        {
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
            return ApplyConfig(LocalConfig(account), Path.Combine(App.DataDir, "backup", "localconfig_" + account + ".vdf"));
        }
        string ApplyConfig(string cfg, string bak)
        {
            var pending = entries.Where(kv => kv.Value.PendingSec >= 60 && kv.Value.InflightMin == 0 && !kv.Value.ApplyIntent).ToList();
            if (pending.Count == 0) return null;
            if (!File.Exists(cfg)) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(bak));
            if (!File.Exists(bak)) File.Copy(cfg, bak);

            var vdf = new VdfText(File.ReadAllText(cfg, new UTF8Encoding(false)));
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
                added.Add(kv.Key + ": +" + FormatMin(minutes));
                Log("playtime " + kv.Key + ": " + cur + " -> " + (cur + minutes) + " min");
            }
            var tmp = cfg + ".tmp";
            // Persist intent first: a crash on either side of the VDF replacement is recoverable.
            try
            {
                Save();
                if (SteamRunning()) throw new IOException("Steam restarted during playtime preparation");
                File.WriteAllText(tmp, vdf.Text, new UTF8Encoding(false));
                if (SteamRunning()) throw new IOException("Steam restarted before playtime commit");
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
            if (!File.Exists(cfg)) return;
            long mtime = Unix(File.GetLastWriteTimeUtc(cfg));
            VdfText vdf = null;
            foreach (var kv in entries)
            {
                var e = kv.Value;
                if (e.InflightMin == 0 || mtime < e.AppliedAt + 120) continue;
                if (vdf == null) vdf = new VdfText(File.ReadAllText(cfg, new UTF8Encoding(false)));
                int cur;
                int.TryParse(vdf.GetAppValue(kv.Key, "Playtime"), out cur);
                if (cur < e.Expected) { e.PendingSec += (double)e.InflightMin * 60; e.PendingCloud += e.InflightCloud; Log("playtime " + kv.Key + ": Steam dropped " + e.InflightMin + " min, re-queued"); }
                else e.CreditedCloud += e.InflightCloud;
                e.InflightMin = 0; e.InflightCloud = 0;
                Save();
            }
        }

        public int PendingMinutes() { return (int)Math.Min(int.MaxValue, entries.Values.Sum(e => e.PendingSec) / 60); }
        // LuaTools: per-game pending minutes for the native games list.
        public int PendingMinutes(string appId) { Entry e; return entries.TryGetValue(appId, out e) ? (int)Math.Min(int.MaxValue, e.PendingSec / 60) : 0; }

        public static string FormatMin(int m) { return m >= 60 ? (m / 60) + "h " + (m % 60) + "m" : m + "m"; }

        void Load()
        {
            if (!File.Exists(path)) return;
            foreach (var l in File.ReadAllLines(path))
            {
                var p = l.Split('|');
                if (p.Length == 2 && p[0] == "#cloud-receipt") { cloudReceipts.Add(p[1]); continue; }
                if (p.Length < 6) continue;
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
        static string OutcomePath(string id) { return Path.Combine(App.DataDir, "schemas", id + ".txt"); }
        static bool Due(string id)
        {
            if (File.Exists(SchemaPath(id))) return false;
            var path = OutcomePath(id);
            if (!File.Exists(path)) return true;
            var p = File.ReadAllText(path).Split('|'); long ticks;
            if (p.Length > 1 && p[1] == "found") return false;
            return p.Length == 0 || !long.TryParse(p[0], out ticks) || DateTime.UtcNow.Ticks - ticks >= TimeSpan.FromDays(7).Ticks;
        }
        public string Tick(bool playing, bool updating)
        {
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
                batch = false; int count = found; found = 0;
                return count > 0 ? L.T("Found achievement lists for " + count + " games.", "Listes de succès trouvées pour " + count + " jeux.") : null;
            }
            batch = true; appId = id;
            var name = "Local\\Cabinet-schema-" + Guid.NewGuid().ToString("N");
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
            using (var parent = Process.GetCurrentProcess())
            {
                var info = new ProcessStartInfo(Application.ExecutablePath, "--schema " + id + " " + parent.Id + " " + parent.StartTime.ToUniversalTime().Ticks + " " + name);
                info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = ProcessWindowStyle.Hidden;
                started = DateTime.UtcNow;
                try { child = Process.Start(info); log("schema " + id + ": started idle request"); }
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
                Directory.CreateDirectory(Path.GetDirectoryName(OutcomePath(id)));
                // Persist before connection, so crashes cannot cause a rapid retry loop.
                File.WriteAllText(OutcomePath(id), DateTime.UtcNow.Ticks + "|attempting");
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
                if (outcome == "interrupted") File.Delete(OutcomePath(id));
                else File.WriteAllText(OutcomePath(id), DateTime.UtcNow.Ticks + "|" + outcome);
                log("schema " + id + ": " + outcome);
            }
        }
        public void Dispose()
        {
            if (stop != null) { stop.Set(); stop.Dispose(); }
            if (child != null) child.Dispose();
        }
    }

    // LuaTools: the tray ApplicationContext is replaced by LuaToolsGui.Services.Cabinet.CabinetService;
    // only App's static helpers used by the engine and helper processes are kept.
    static partial class App
    {
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cabinet");
        static readonly string StatePath = Path.Combine(DataDir, "state.txt");
        static readonly string LogPath = Path.Combine(DataDir, "log.txt");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UpdateRepo = "MRoiban/cabinet";
        static readonly string SyncTimesPath = Path.Combine(DataDir, "sync-times.txt");

        internal sealed class GameOverview
        {
            public string Id, Name, Detail, Tip, Played;
            public long Minutes, Synced; // LuaTools: raw values for the native games list
            public int Unlocked, Total;
            public string Text { get { return Detail.Length == 0 ? Name : Name + " — " + Detail; } }
            public long Recent;
        }

        internal static string GameName(string id)
        {
            try
            {
                if (File.Exists(SchemaFill.SchemaPath(id)))
                {
                    var game = KV.Read(File.ReadAllBytes(SchemaFill.SchemaPath(id))).Children.FirstOrDefault();
                    var gn = game == null ? null : game.Get("gamename");
                    if (gn != null && !string.IsNullOrEmpty(gn.Str())) return gn.Str();
                }
                foreach (var lib in Playtime.Libraries())
                {
                    var manifest = Path.Combine(lib, "steamapps", "appmanifest_" + id + ".acf");
                    if (!File.Exists(manifest)) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), "\"name\"\\s+\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch (Exception ex) { Log("game name " + id + ": " + ex.Message); }
            return id;
        }

        internal static List<GameOverview> Games(Dictionary<string, string> times)
        {
            var result = new List<GameOverview>();
            string account = Syncer.AccountId();
            var cfg = account == null ? null : Path.Combine(Syncer.SteamPath(), "userdata", account, "config", "localconfig.vdf");
            var vdf = cfg != null && File.Exists(cfg) ? new VdfText(File.ReadAllText(cfg)) : null;
            foreach (var id in Syncer.OnlineFixAppIds())
            {
                string name = GameName(id);
                int total = 0, unlocked = 0;
                try
                {
                    if (File.Exists(SchemaFill.SchemaPath(id)))
                    {
                        var game = KV.Read(File.ReadAllBytes(SchemaFill.SchemaPath(id))).Children.FirstOrDefault();
                        var stats = game == null ? null : game.Get("stats");
                        var userPath = Path.Combine(Syncer.StatsDir(), "UserGameStats_" + account + "_" + id + ".bin");
                        var cache = File.Exists(userPath) ? KV.Read(File.ReadAllBytes(userPath)).Children.FirstOrDefault() : null;
                        if (stats != null && stats.Children != null) foreach (var stat in stats.Children)
                        {
                            var bits = stat.Get("bits");
                            if (bits == null || bits.Children == null) continue;
                            var value = cache == null ? null : cache.Get(stat.Key);
                            var data = value == null ? null : value.Get("data");
                            int mask = data == null ? 0 : Convert.ToInt32(data.Value);
                            foreach (var bit in bits.Children)
                            {
                                var number = bit.Get("bit"); int n;
                                if (!int.TryParse(number == null ? bit.Key : number.Str(), out n) || n < 0 || n > 31) continue;
                                total++; if ((mask & (1 << n)) != 0) unlocked++;
                            }
                        }
                    }
                }
                catch (Exception ex) { Log("overview " + id + ": " + ex.Message); }
                long minutes = 0, played = 0, synced = 0; string time;
                if (vdf != null) { long.TryParse(vdf.GetAppValue(id, "Playtime"), out minutes); long.TryParse(vdf.GetAppValue(id, "LastPlayed"), out played); }
                if (times.TryGetValue(id, out time)) long.TryParse(time, out synced);
                var date = synced > 0 && synced <= DateTime.MaxValue.Ticks ? new DateTime(synced, DateTimeKind.Utc) : DateTime.MinValue;
                // Skip games with nothing to show, and unnamed ones (uninstalled, no achievement list).
                if ((total == 0 && minutes == 0) || name == id) continue;
                var parts = new List<string>();
                string playedText = minutes == 0 ? "" : minutes < 60 ? minutes + " min" : L.Hours(minutes / 60.0);
                if (total > 0) parts.Add(unlocked + " / " + total + " achievements");
                if (minutes > 0) parts.Add(playedText);
                result.Add(new GameOverview { Id = id, Name = name, Detail = string.Join("   ·   ", parts.ToArray()),
                    Unlocked = unlocked, Total = total, Played = playedText, Minutes = minutes, Synced = synced,
                    Recent = Math.Max(played, date == DateTime.MinValue ? 0 : Playtime.Unix(date)),
                    Tip = L.T("Last synced ", "Dernière synchro : ") + (date == DateTime.MinValue ? L.T("never", "jamais") : date.ToLocalTime().ToString("g")) + L.T("\nClick to open in Steam", "\nCliquer pour ouvrir dans Steam") });
            }
            return result.OrderByDescending(g => g.Recent).ThenBy(g => g.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine); } catch { }
        }

        [DllImport("kernel32.dll")] internal static extern uint SetErrorMode(uint mode);

        internal static bool ParentAlive(int pid, long started)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == started; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        // SteamAppId is cached process-wide by steamclient: the 480 reader must
        // be a separate process. It only reads; it never joins or creates a lobby.
        sealed class PresenceRelay : IDisposable
        {
            public readonly string Path;
            readonly EventWaitHandle stop;
            readonly Process process;
            public PresenceRelay(Action<string> logger)
            {
                string token = Guid.NewGuid().ToString("N"), name = "Local\\Cabinet-presence-" + token;
                Directory.CreateDirectory(DataDir);
                Path = System.IO.Path.Combine(DataDir, "presence-" + token + ".json");
                stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
                try
                {
                    using (var parent = Process.GetCurrentProcess())
                    {
                        var info = new ProcessStartInfo(Application.ExecutablePath, "--presence-source " + parent.Id + " " + parent.StartTime.ToUniversalTime().Ticks + " " + name + " \"" + Path + "\"");
                        info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = ProcessWindowStyle.Hidden;
                        process = Process.Start(info);
                    }
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (!File.Exists(Path) && !process.HasExited && DateTime.UtcNow < until) Thread.Sleep(50);
                    if (!File.Exists(Path)) throw new IOException("480 reader did not become ready");
                    logger("shadow: read-only 480 presence relay ready");
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                stop.Set();
                if (process != null)
                {
                    try { if (!process.WaitForExit(3000)) { process.Kill(); process.WaitForExit(1000); } } catch { }
                    process.Dispose();
                }
                stop.Dispose();
                try { File.Delete(Path); File.Delete(Path + ".tmp"); } catch { }
            }
        }
        internal static void RunPresenceSource(string[] args)
        {
            SetErrorMode(0x0001 | 0x0002 | 0x8000);
            int parent = int.Parse(args[1]); long started = long.Parse(args[2]);
            try
            {
            using (var stop = EventWaitHandle.OpenExisting(args[3]))
            {
            if (!ParentAlive(parent, started) || !Playtime.SteamRunning() || stop.WaitOne(0)) return;
            using (var session = new SteamSession("480", Log))
            {
                while (ParentAlive(parent, started) && Playtime.SteamRunning() && !stop.WaitOne(0))
                {
                    session.Pump();
                    File.WriteAllText(args[4] + ".tmp", session.PresenceSnapshot(), new UTF8Encoding(false));
                    if (File.Exists(args[4])) File.Replace(args[4] + ".tmp", args[4], null); else File.Move(args[4] + ".tmp", args[4]);
                    stop.WaitOne(1000);
                }
            }
            }
            }
            finally { try { File.Delete(args[4]); File.Delete(args[4] + ".tmp"); } catch { } }
        }

        internal static void RunShadow(string[] args)
        {
            // All Steam environment variables are confined to this process. No game DLLs,
            // steam_appid.txt, injection, global persona calls, or shutdown commands.
            SetErrorMode(0x0001 | 0x0002 | 0x8000);
            Directory.CreateDirectory(DataDir);
            string id = args[1]; uint parsed;
            if (!uint.TryParse(id, out parsed) || parsed == 0) throw new ArgumentException("Invalid app id");
            int parent = int.Parse(args[2]); long started = long.Parse(args[3]);
            bool created;
            using (var mutex = new Mutex(true, "Local\\Cabinet-shadow-app-" + id, out created))
            {
                if (!created) return;
                using (var stop = EventWaitHandle.OpenExisting(args[4]))
                {
                    if (!ParentAlive(parent, started) || stop.WaitOne(0) || !Playtime.SteamRunning()) return;
                    var account = Syncer.AccountId();
                    if (account == null) return;
                    Syncer.BackupSteamFiles(id, account);
                    PresenceRelay source = null;
                    try { source = new PresenceRelay(Log); }
                    catch (Exception ex) { Log("shadow " + id + ": 480 presence reader unavailable: " + ex.Message); }
                    using (source)
                    using (var session = new SteamSession(id, Log))
                    {
                        var detector = new Playtime(Path.Combine(DataDir, "playtime.txt"), Log);
                        detector.RefreshGames();
                        while (ParentAlive(parent, started) && Playtime.SteamRunning() && !stop.WaitOne(0))
                        {
                            session.Pump();
                            session.MirrorJoin(source != null && detector.RunningApps().SetEquals(new[] { id }) ? source.Path : null);
                            try { session.Sync(false); }
                            catch (IOException ex) { Log("shadow " + id + ": retry reading stats: " + ex.Message); }
                            if (stop.WaitOne(1000)) break;
                        }
                        if (Playtime.SteamRunning())
                        {
                            // Allow OnlineFix's final file flush and Steam's store callback.
                            Thread.Sleep(1000);
                            try { session.Sync(true); }
                            catch (IOException ex) { Log("shadow " + id + ": final read failed: " + ex.Message); }
                            for (int i = 0; i < 3; i++) { session.Pump(); Thread.Sleep(250); }
                        }
                    }
                    Log("shadow " + id + ": disconnected");
                }
            }
        }
    }

    static class BuildInfo { public const string Version = "luatools"; }
}
