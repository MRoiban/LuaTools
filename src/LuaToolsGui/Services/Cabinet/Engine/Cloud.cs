// Verbatim port of github.com/MRoiban/cabinet (Cloud.cs). Keep in sync with upstream;
// LuaTools-specific changes are marked "LuaTools:".
#nullable disable
#pragma warning disable SYSLIB0014
// Integration (Cabinet.cs is owned by the orchestrator):
// Main, before install/mutex: if (CloudSync.HandleArgs(args)) return;
// Install after copy: CloudSync.RegisterProtocol(target);
// App constructor: CloudSync.Configure(Log, playtime.QueueCloudMinutes,
//   playtime.CloudMinutesInSteam, delegate { return playtime.RunningApps(); },
//   delegate { return playtime.ResetMarker; });
// Tray menu: menu.Items.Add(CloudSync.MenuItem());
// Each tick after detection: CloudSync.Tick(running.Count > 0 || shadows.HasChildren
//   || schemas.HasChild, running); show its non-null return using the existing balloon.
// Required Playtime methods: bool QueueCloudMinutes(string receiptId,string appId,int minutes)
// must atomically persist receipt + pending seconds in the SAME ledger transaction;
// duplicate receipt returns true. long CloudMinutesInSteam(string appId) returns imported
// minutes credited in VDF (including inflight, excluding pending/requeued), persisting
// provenance through Apply/Verify. Never independently edit playtime.txt from cloud code.
// Cloud journal watermarks + deterministic receipt IDs recover across crashes. A journal lost to
// a crash is rebaselined (see Round), and a lost ledger is reported through ResetMarker.
// --cloud-selftest uses only temporary roots and an in-memory localhost server.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CabinetEngine.Compat;
using System.Windows.Forms;
using Microsoft.Win32;
using LuaToolsGui.Services.Cabinet; // LuaTools: shared pairing input and redemption.

namespace CabinetEngine
{
    static class CloudSync
    {
        static readonly object Gate = new object();
        static CloudClient client;
        static Action<string> logger;
        static Func<string, string, int, bool> queue;
        static Func<string, long> imported;
        static Func<ICollection<string>> detect;
        static Func<string> ledgerReset;
        static HashSet<string> running = new HashSet<string>();
        static Dictionary<string, long> localMinutes = new Dictionary<string, long>();
        static volatile bool busy, requested;
        static bool wasPlaying;
        static DateTime next = DateTime.MinValue;
        static volatile string message;
        static readonly Queue<string> connects = new Queue<string>();
        static CloudClient Client { get { if (client == null) client = new CloudClient(App.DataDir, Syncer.OnlineFixDir, LocalMinutes, Log); return client; } }
        static void Log(string s) { if (logger != null) logger("cloud: " + s); }
        // LuaTools: settings-page entry points replacing the tray menu. Call on the engine thread.
        internal static bool Connected { get { return Client.Connected; } }
        internal static string UserName { get { return Client.Connected ? Client.UserName : null; } }
        internal static string Server { get { return Client.Server; } }
        internal static string Token { get { return Client.Token; } }
        internal static string DeviceId { get { return Client.Device; } } // LuaTools: shared lua cache uploads.
        internal static int ConnectionVersion { get { return Client.ConnectionVersion; } }
        // LuaTools: cloud.json was damaged and nothing intact was found; Settings asks to connect again.
        internal static bool ConnectionDamaged { get { return !Client.Connected && Client.SettingsDamaged; } }
        internal static bool Busy { get { return busy; } }
        internal static void QueueConnect(string input) { ValidateConnect(input); connects.Enqueue(input.Trim()); }
        static void ValidateConnect(string input)
        { if (CabinetConnectInput.Classify(input) == CabinetConnectInputKind.Invalid) throw new CabinetConnectException(CabinetConnectFailure.InvalidCode); }
        // LuaTools: Settings awaits authentication and persistence, so failures reach its error label.
        internal static void Connect(string input, string server)
        {
            if (busy) throw new InvalidOperationException("Cabinet Cloud is busy");
            busy = true;
            try { Client.Connect(input, server); requested = true; }
            finally { busy = false; }
        }
        internal static void RequestSync() { requested = true; }
        internal static void Disconnect() { if (!busy) Client.Disconnect(); }
        public static void Configure(Action<string> log, Func<string, string, int, bool> queueMinutes,
            Func<string, long> minutesInSteam, Func<ICollection<string>> runningApps, Func<string> ledgerResetMarker = null)
        { logger = log; queue = queueMinutes; imported = minutesInSteam; detect = runningApps; ledgerReset = ledgerResetMarker; }
        static long LocalMinutes(string app)
        { long value; return localMinutes.TryGetValue(app, out value) ? value : 0; }
        // Snapshot ledger provenance on the tray thread; its entries are not thread-safe.
        static long ReadLocalMinutes(string app)
        {
            string account = Syncer.AccountId(); if (account == null) return 0;
            string p = Path.Combine(Syncer.SteamPath(), "userdata", account, "config", "localconfig.vdf"); long n = 0;
            if (File.Exists(p)) long.TryParse(new VdfText(File.ReadAllText(p)).GetAppValue(app, "Playtime"), out n);
            return Math.Max(0, n - (imported == null ? 0 : imported(app)));
        }
        public static bool HandleArgs(string[] args)
        {
            if (args.Length == 1 && args[0] == "--cloud-selftest")
            { try { CloudSelfTest.Run(); } catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; } return true; }
            string url = args.FirstOrDefault(a => a.StartsWith("cabinet:", StringComparison.OrdinalIgnoreCase));
            if (url == null) return false;
            try
            {
                string server, token; CloudClient.ParseLink(url, out server, out token);
                Directory.CreateDirectory(App.DataDir);
                // Unique DPAPI inbox files avoid losing links from concurrent protocol launches.
                string path = Path.Combine(App.DataDir, "cloud-connect-" + Guid.NewGuid().ToString("N") + ".dat");
                File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(url), null, DataProtectionScope.CurrentUser));
                bool active;
                try { using (Mutex.OpenExisting(@"Local\Cabinet")) active = true; }
                catch (WaitHandleCannotBeOpenedException) { active = false; }
                if (!active) System.Diagnostics.Process.Start(Application.ExecutablePath);
            }
            catch { MessageBox.Show(L.T("Invalid Cabinet Cloud connect link.", "Lien de connexion Cabinet Cloud invalide."), L.T("Cabinet Cloud", "Cabinet Cloud")); }
            return true;
        }
        public static void RegisterProtocol(string exePath)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\cabinet"))
            {
                k.SetValue("", "URL:Cabinet"); k.SetValue("URL Protocol", "");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + exePath + "\" \"%1\"");
            }
        }
        static bool Blocked(string app)
        {
            lock (Gate) if (running.Contains(app)) return true;
            return Shadows.HelperActive(app) || Playtime.SteamCounting(app) || (detect != null && detect().Contains(app));
        }
        public static string Tick(bool anyGameRunning, ICollection<string> runningAppIds)
        {
            lock (Gate) running = new HashSet<string>(runningAppIds ?? new string[0]);
            if (wasPlaying && !anyGameRunning) requested = true; wasPlaying = anyGameRunning;
            string result = message; message = null; FriendLabels.Tick(Client, runningAppIds, Log); if (busy) return result;
            try
            {
                Directory.CreateDirectory(App.DataDir);
                foreach (string path in Directory.GetFiles(App.DataDir, "cloud-connect-*.dat"))
                {
                    connects.Enqueue(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)));
                    File.Delete(path);
                }
                if (ledgerReset != null) Client.LedgerReset(ledgerReset());
                if (queue != null && Client.Connected) Client.Deliver(queue, Blocked);
            }
            catch (Exception ex) { Log(ex.Message); }
            if (connects.Count > 0)
            {
                string url = connects.Dequeue(); busy = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Client.Connect(url); message = L.T("Connected to Cabinet Cloud as ", "Connecté à Cabinet Cloud en tant que ") + Client.UserName; requested = true; }
                    catch (CabinetConnectException ex) { message = ex.Message; Log("connection failed"); }
                    catch (WebException ex) { message = ex.Response == null ? LuaToolsGui.Resources.Strings.CabinetCloud_Unreachable : LuaToolsGui.Resources.Strings.CabinetCloud_ConnectError; Log("connection failed"); }
                    catch { message = LuaToolsGui.Resources.Strings.CabinetCloud_ConnectError; Log("connection failed"); }
                    finally { busy = false; }
                });
            }
            else if (!anyGameRunning && Client.Connected && queue != null && imported != null && (requested || DateTime.UtcNow >= next))
            {
                var snapshot = new Dictionary<string, long>();
                try
                {
                    if (Directory.Exists(Syncer.OnlineFixDir)) foreach (string folder in Directory.GetDirectories(Syncer.OnlineFixDir))
                    { string id = Path.GetFileName(folder); if (Regex.IsMatch(id, @"^[0-9]{1,10}$") && !Blocked(id)) snapshot[id] = ReadLocalMinutes(id); }
                }
                catch (Exception ex) { Log("playtime snapshot failed: " + ex.Message); return result; }
                localMinutes = snapshot;
                requested = false; next = DateTime.UtcNow.AddMinutes(30); busy = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Client.Round(Blocked); }
                    catch (Exception ex) { Log("sync failed: " + ex.Message); next = DateTime.UtcNow.AddMinutes(1); }
                    finally { busy = false; }
                });
            }
            return result;
        }
        public static ToolStripMenuItem MenuItem()
        {
            var root = new ToolStripMenuItem(L.T("Cabinet Cloud", "Cabinet Cloud"));
            var status = new ToolStripMenuItem() { Enabled = false };
            var connect = new ToolStripMenuItem(L.T("Connect…", "Se connecter…"));
            var sync = new ToolStripMenuItem(L.T("Sync now", "Synchroniser"));
            var disconnect = new ToolStripMenuItem(L.T("Disconnect", "Se déconnecter"));
            var labels = FriendLabels.MenuItem();
            root.DropDownItems.AddRange(new ToolStripItem[] { status, connect, sync, disconnect, labels });
            root.DropDownOpening += delegate
            {
                status.Text = Client.Connected ? L.T("Connected as ", "Connecté en tant que ") + Client.UserName.Replace("&", "&&")
                    : Client.SettingsDamaged ? L.T("Not connected: the saved connection was damaged, connect again", "Non connecté : la connexion enregistrée était endommagée, reconnectez-vous")
                    : L.T("Not connected", "Non connecté");
                labels.Visible = Client.Connected && !FriendLabels.DebugEnabled();
                connect.Enabled = !busy; sync.Enabled = Client.Connected; disconnect.Enabled = Client.Connected && !busy;
            };
            connect.Click += delegate { ShowConnect(); }; sync.Click += delegate { requested = true; };
            disconnect.Click += delegate { if (!busy) Client.Disconnect(); }; return root;
        }
        static void ShowConnect()
        {
            using (var f = new Form())
            {
                f.Text = L.T("Connect Cabinet Cloud", "Connecter Cabinet Cloud"); f.Width = 540; f.Height = 210;
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterScreen; f.MaximizeBox = false; f.MinimizeBox = false;
                var label = new Label { Left = 12, Top = 12, Width = 500, Text = LuaToolsGui.Resources.Strings.CabinetCloud_PairingCode };
                var input = new TextBox { Left = 12, Top = 40, Width = 500, PlaceholderText = LuaToolsGui.Resources.Strings.CabinetCloud_CodePlaceholder };
                input.TextChanged += delegate { if (CabinetConnectInput.TryNormalizeCode(input.Text, out string code)) { string formatted = CabinetConnectInput.FormatCode(code); if (input.Text != formatted) { input.Text = formatted; input.SelectionStart = formatted.Length; } } };
                var help = new Label { Left = 12, Top = 70, Width = 500, Height = 40, Text = LuaToolsGui.Resources.Strings.CabinetCloud_PairingHelp };
                var ok = new Button { Left = 310, Top = 120, Width = 100, Text = L.T("Connect", "Connexion") };
                var cancel = new Button { Left = 420, Top = 120, Width = 90, Text = L.T("Cancel", "Annuler"), DialogResult = DialogResult.Cancel };
                ok.Click += delegate
                {
                    try { QueueConnect(input.Text); f.DialogResult = DialogResult.OK; f.Close(); }
                    catch { MessageBox.Show(f, LuaToolsGui.Resources.Strings.CabinetCloud_InvalidCode, L.T("Cabinet Cloud", "Cabinet Cloud")); }
                };
                f.Controls.AddRange(new Control[] { label, input, help, ok, cancel }); f.AcceptButton = ok; f.CancelButton = cancel; f.ShowDialog();
            }
        }
    }
    // The fake endpoint is deliberately scoped to the self-test and never uses production roots.
    static class CloudSelfTest
    {
        // LuaTools: one PC's Lua files and Steam stats cache, so the test never touches the real Steam folder.
        sealed class FakeSteam
        {
            internal readonly HashSet<string> Lua = new HashSet<string>();
            internal readonly Dictionary<string, Dictionary<string, long>> Stats = new Dictionary<string, Dictionary<string, long>>();
            internal readonly HashSet<string> Schemas = new HashSet<string>();
            internal CloudClient Wire(CloudClient c)
            {
                c.LuaApps = delegate { return Lua.ToArray(); };
                c.SteamAchievements = delegate(string app)
                { Dictionary<string, long> s; return !Schemas.Contains(app) ? null : Stats.TryGetValue(app, out s) ? new Dictionary<string, long>(s) : new Dictionary<string, long>(); };
                c.MergeSteamAchievements = delegate(string app, IDictionary<string, long> unlocks)
                {
                    if (!Schemas.Contains(app)) return -1;
                    Dictionary<string, long> s; if (!Stats.TryGetValue(app, out s)) Stats[app] = s = new Dictionary<string, long>();
                    int added = 0; foreach (var u in unlocks) if (!s.ContainsKey(u.Key)) { s[u.Key] = u.Value; added++; }
                    return added;
                };
                return c;
            }
        }
        static void Check(bool ok, string label)
        { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        static void Seed(string root, string ach, long timestamp, string save, DateTime date)
        {
            string stats = Path.Combine(root, "123", "Stats"), saves = Path.Combine(root, "123", "Saves");
            Directory.CreateDirectory(stats); Directory.CreateDirectory(saves);
            File.WriteAllText(Path.Combine(stats, "Achievements.ini"), "; retained comment\r\n[" + ach + "]\r\nachieved=true\r\ntimestamp=" + timestamp + "\r\nextra=keep\r\n");
            File.WriteAllText(Path.Combine(stats, "Stats.ini"), "; stats comment\r\n[Stats]\r\nscore=" + timestamp + "\r\n[Other]\r\nkeep=yes\r\n");
            File.SetLastWriteTimeUtc(Path.Combine(stats, "Stats.ini"), date);
            File.WriteAllText(Path.Combine(saves, "slot.dat"), save); File.SetLastWriteTimeUtc(Path.Combine(saves, "slot.dat"), date); Directory.SetLastWriteTimeUtc(saves, date);
        }
        public static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "Cabinet-cloud-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                using (var server = new FakeServer())
                {
                    string ar = Path.Combine(root, "A", "OnlineFix"), br = Path.Combine(root, "B", "OnlineFix");
                    DateTime date = DateTime.UtcNow.AddHours(-2); Seed(ar, "A", 100, "save-A", date.AddHours(1)); Seed(br, "B", 200, "save-B-old", date);
                    var errors = new List<string>(); Action<string> log = delegate(string s) { if (!s.EndsWith("achievements into Steam")) errors.Add(s); Console.WriteLine("LOG: " + s); };
                    // A plays Lua game 777 through Steam and has a DLC Lua (888); B has 777's Lua but never played it.
                    var steamA = new FakeSteam(); steamA.Lua.Add("777"); steamA.Lua.Add("888"); steamA.Schemas.Add("777");
                    steamA.Stats["777"] = new Dictionary<string, long> { { "LUA_X", 1600000000 } };
                    var steamBox = new FakeSteam(); steamBox.Lua.Add("777"); steamBox.Schemas.Add("777"); steamBox.Schemas.Add("999");
                    // 999 is only owned on Steam somewhere else: it must not be written here.
                    server.AppState("999")["achievements"] = new Dictionary<string, object> { { "OWNED", 1500000000L } };
                    var a = steamA.Wire(new CloudClient(Path.Combine(root, "A", "data"), ar, delegate { return 12; }, log));
                    long steamB = 7, importedB = 0;
                    var b = steamBox.Wire(new CloudClient(Path.Combine(root, "B", "data"), br, delegate { return steamB - importedB; }, log));
                    string url = "cabinet://connect?server=" + Uri.EscapeDataString(server.Origin) + "&token=" + FakeServer.Token;
                    a.Connect(url); b.Connect(server.Origin + "/connect#" + FakeServer.Token);
                    Check(a.Device != b.Device && a.UserName == "Tester", "protocol and browser link connect; independent device IDs");
                    Check(!File.ReadAllText(Path.Combine(a.Data, "cloud.json")).Contains(FakeServer.Token), "DPAPI settings contain no plaintext token");
                    Func<string, bool> idle = delegate { return false; };
                    a.Round(idle); b.Round(idle); a.Round(idle);
                    // LuaTools: CloudRedirect owns saves; the engine never uploads or replaces the Saves folder.
                    Check(server.SaveRequests == 0, "no save upload or download");
                    Check(File.ReadAllText(Path.Combine(br, "123", "Saves", "slot.dat")) == "save-B-old" && !Directory.Exists(Path.Combine(b.Data, "backup")), "local saves left untouched");
                    var ini = Syncer.ReadIni(Path.Combine(br, "123", "Stats", "Achievements.ini"));
                    Check(ini.ContainsKey("A") && ini.ContainsKey("B") && ini["B"]["extra"] == "keep", "achievement union preserves INI sections and custom values");
                    Check(File.ReadAllText(Path.Combine(br, "123", "Stats", "Achievements.ini")).Contains("; retained comment"), "INI comments preserved");
                    Check(Syncer.ReadIni(Path.Combine(br, "123", "Stats", "Stats.ini"))["Stats"]["score"] == "100", "newer stats snapshot applied");
                    // LuaTools: achievements of games with a Lua file sync through Steam's stats cache.
                    Check(CloudClient.Map(server.AppState("777")["achievements"]).ContainsKey("LUA_X") && CloudClient.Num(server.AppState("777")["statsUpdatedAt"]) == 0
                        && CloudClient.Num(server.AppState("777")["playtimeTotal"]) == 0, "Lua game achievements upload from Steam's stats cache, without stats or playtime");
                    Check(!server.Pushed.Contains("888"), "a Lua file with nothing unlocked (DLC) is not pushed");
                    Check(steamBox.Stats.ContainsKey("777") && steamBox.Stats["777"]["LUA_X"] == 1600000000, "another PC's Lua unlocks are written into Steam with their original time");
                    Check(!steamBox.Stats.ContainsKey("999") && !Directory.Exists(Path.Combine(br, "999")) && !Directory.Exists(Path.Combine(br, "777")),
                        "games without a Lua file or OnlineFix folder here are never written");
                    var receipts = new HashSet<string>(); int added = 0;
                    Func<string, string, int, bool> ledger = delegate(string receipt, string app, int n)
                    { if (receipts.Add(receipt)) { added += n; steamB += n; importedB += n; } return true; };
                    string before = File.ReadAllText(Path.Combine(b.Data, "cloud-state.json"));
                    b.Deliver(ledger, delegate { return true; }); Check(added == 0, "running app playtime remains pending");
                    b.Deliver(ledger, idle); Check(added == 12, "remote playtime queued once");
                    // Simulate a crash after ledger commit but before the cloud watermark commit.
                    File.WriteAllText(Path.Combine(b.Data, "cloud-state.json"), before);
                    b = steamBox.Wire(new CloudClient(b.Data, br, delegate { return steamB - importedB; }, log)); b.Deliver(ledger, idle); b.Round(idle); b.Deliver(ledger, idle);
                    Check(added == 12, "idempotent receipt replay across restart does not double count");
                    Check(CloudClient.Num(CloudClient.Map(server.App["playtime"])[a.Device]) == 12 && CloudClient.Num(CloudClient.Map(server.App["playtime"])[b.Device]) == 7, "per-device playtime max and own totals retained");
                    Check(steamB == 19 && importedB == 12, "imported playtime is subtracted before the next push");
                    server.EarlierAchievement(); b.Round(idle);
                    Check(Syncer.ReadIni(Path.Combine(br, "123", "Stats", "Achievements.ini"))["A"]["timestamp"] == "50", "earliest achievement timestamp wins");
                    string unchanged = File.ReadAllText(Path.Combine(br, "123", "Stats", "Achievements.ini")); int requests = server.Progress;
                    b.Round(delegate { return true; }); Check(server.Progress == requests && unchanged == File.ReadAllText(Path.Combine(br, "123", "Stats", "Achievements.ini")), "running app is never pushed or applied");
                    Check(errors.Count == 0, "end-to-end rounds completed without errors");
                    bool denied = false;
                    try { b.Connect(server.Origin + "/connect#cab_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"); } catch (WebException) { denied = true; }
                    Check(denied && b.Connected, "401 connection leaves the prior connection intact");
                    b.Disconnect(); Check(!steamBox.Wire(new CloudClient(b.Data, br, delegate { return 0; }, log)).Connected, "disconnect persists");
                    Console.WriteLine("Cabinet Cloud self-test passed (temporary roots only).");
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            RunRecovery();
        }
        // State files damaged by a crash or power loss: recovery from leftovers, quarantine, rebaseline.
        internal static void RunRecovery()
        {
            JournalRecoveredFromReplaceBackup();
            JournalRecoveredFromNewestTempFile();
            TruncatedJournalQuarantined();
            DamagedJournalRebaselinesWithoutDoubleCounting();
            DamagedSettingsStartDisconnectedThenConnect();
            LedgerRecoveredOrReset();
            LedgerResetNeverPushesImportedMinutes();
            DurableWritesLeaveNoTemporaryFiles();
            Console.WriteLine("Cabinet Cloud recovery self-test passed (temporary roots only).");
        }
        const string Journal = "{\"apps\":{\"123\":{\"ownMinutes\":3,\"remote\":{\"dev-remote\":5},\"pending\":{}}}}";
        static void InTemp(Action<string> test)
        {
            string root = Path.Combine(Path.GetTempPath(), "Cabinet-recovery-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { test(root); } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        static void Leftover(string path, byte[] bytes, TimeSpan age) { File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age); }
        static byte[] Utf8(string text) { return new UTF8Encoding(false).GetBytes(text); }
        static string[] Names(string dir) { return Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal).ToArray(); }
        static CloudClient Client(string data, string root, Func<string, long> minutes, Action<string> log)
        { var steam = new FakeSteam(); return steam.Wire(new CloudClient(data, root, minutes, log)); }
        internal static void JournalRecoveredFromReplaceBackup()
        {
            InTemp(delegate(string dir)
            {
                // The field case: 25,077 zero bytes, and File.Replace's backup of the previous version beside it.
                string state = Path.Combine(dir, "cloud-state.json"); File.WriteAllBytes(state, new byte[25077]);
                Leftover(state + "~RF37d5e83.TMP", Utf8(Journal), TimeSpan.FromHours(1));
                var logs = new List<string>(); var client = Client(dir, Path.Combine(dir, "OnlineFix"), delegate { return 0; }, logs.Add);
                Check(File.ReadAllText(state) == Journal && !client.Rebaselining, "zero-filled journal recovered from File.Replace's ~RF backup");
                Check(!File.Exists(state + "~RF37d5e83.TMP") && !Names(dir).Any(n => n.Contains(".corrupt-")), "recovered journal rewritten; stale leftover removed, nothing quarantined");
                Check(logs.Any(l => l.Contains("recovered it from cloud-state.json~RF37d5e83.TMP")) && !logs.Any(l => l.Contains("dev-remote")), "recovery logged by file name, without contents");
            });
        }
        internal static void JournalRecoveredFromNewestTempFile()
        {
            InTemp(delegate(string dir)
            {
                string state = Path.Combine(dir, "cloud-state.json"); File.WriteAllBytes(state, new byte[512]);
                Leftover(state + "~RF1a2b.TMP", Utf8(Journal.Replace(":5", ":4")), TimeSpan.FromHours(2));
                Leftover(state + "." + Guid.NewGuid().ToString("N") + ".tmp", Utf8(Journal.Replace(":5", ":6")), TimeSpan.FromHours(1));
                Leftover(state + "." + Guid.NewGuid().ToString("N") + ".tmp", new byte[300], TimeSpan.FromMinutes(30));
                var client = Client(dir, Path.Combine(dir, "OnlineFix"), delegate { return 0; }, delegate { });
                Check(File.ReadAllText(state).Contains("\"dev-remote\":6") && !client.Rebaselining, "journal recovered from the newest parseable Atomic temp file");
            });
        }
        internal static void TruncatedJournalQuarantined()
        {
            InTemp(delegate(string dir)
            {
                string state = Path.Combine(dir, "cloud-state.json"); File.WriteAllText(state, Journal.Substring(0, Journal.Length / 2));
                var logs = new List<string>(); var client = Client(dir, Path.Combine(dir, "OnlineFix"), delegate { return 0; }, logs.Add);
                string[] names = Names(dir);
                Check(client.Rebaselining && names.Count(n => n.StartsWith("cloud-state.json.corrupt-")) == 1, "truncated journal quarantined; fresh journal marked rebaseline");
                Check(File.ReadAllText(state).Contains("\"rebaseline\":true") && logs.Any(l => l.Contains("moved it to cloud-state.json.corrupt-")), "rebaseline persisted at once and the quarantine logged");
            });
        }
        internal static void DamagedJournalRebaselinesWithoutDoubleCounting()
        {
            InTemp(delegate(string root)
            {
                using (var server = new FakeServer())
                {
                    string ar = Path.Combine(root, "A", "OnlineFix"), br = Path.Combine(root, "B", "OnlineFix"); DateTime date = DateTime.UtcNow.AddHours(-2);
                    Seed(ar, "A", 100, "a", date); Seed(br, "B", 200, "b", date);
                    long minutesA = 12, steamB = 7, importedB = 0; var receipts = new HashSet<string>(); int added = 0;
                    Func<string, string, int, bool> ledger = delegate(string receipt, string app, int n)
                    { if (receipts.Add(receipt)) { added += n; steamB += n; importedB += n; } return true; };
                    Func<string, bool> idle = delegate { return false; }; Action<string> log = delegate { };
                    var a = Client(Path.Combine(root, "A", "data"), ar, delegate { return minutesA; }, log);
                    var b = Client(Path.Combine(root, "B", "data"), br, delegate { return steamB - importedB; }, log);
                    a.Connect(server.Origin + "/connect#" + FakeServer.Token); b.Connect(server.Origin + "/connect#" + FakeServer.Token);
                    a.Round(idle); b.Round(idle); b.Deliver(ledger, idle);
                    Check(added == 12, "before the reset: A's 12 minutes applied on B once");
                    // A plays 3 more minutes; B pulls them but crashes before applying them.
                    minutesA = 15; a.Round(idle); b.Round(idle);
                    string state = Path.Combine(b.Data, "cloud-state.json"), device = b.Device; File.WriteAllBytes(state, new byte[4096]);
                    b = Client(b.Data, br, delegate { return steamB - importedB; }, log);
                    Check(b.Connected && b.Device == device && b.Rebaselining, "zero-filled journal without leftovers: still connected, same device, rebaseline");
                    b.Deliver(ledger, idle); b.Round(idle); b.Deliver(ledger, idle); b.Round(idle); b.Deliver(ledger, idle);
                    Check(added == 12 && !b.Rebaselining, "rebaseline: A's totals on the server count as applied; nothing added twice (the 3 pulled but unapplied minutes are lost)");
                    Check(CloudClient.Num(CloudClient.Map(server.App["playtime"])[b.Device]) == 7, "B's own playtime still pushed after the reset");
                    minutesA = 20; a.Round(idle); b.Round(idle); b.Deliver(ledger, idle);
                    Check(added == 17, "after the baseline, new remote minutes are applied normally");
                }
            });
        }
        internal static void DamagedSettingsStartDisconnectedThenConnect()
        {
            InTemp(delegate(string root)
            {
                using (var server = new FakeServer())
                {
                    string data = Path.Combine(root, "data"), of = Path.Combine(root, "OnlineFix"); Func<string, bool> idle = delegate { return false; };
                    Seed(of, "A", 100, "a", DateTime.UtcNow.AddHours(-2));
                    var logs = new List<string>(); var c = Client(data, of, delegate { return 4; }, logs.Add);
                    c.Connect(server.Origin + "/connect#" + FakeServer.Token); c.Round(idle); string device = c.Device;
                    string settings = Path.Combine(data, "cloud.json"); File.WriteAllBytes(settings, new byte[700]);
                    c = Client(data, of, delegate { return 4; }, logs.Add);
                    Check(!c.Connected && c.SettingsDamaged && c.Device == device && Names(data).Any(n => n.StartsWith("cloud.json.corrupt-")),
                        "zero-filled cloud.json: quarantined, disconnected, device ID kept from the journal");
                    Check(!logs.Any(l => l.Contains(FakeServer.Token)), "no token in the logs");
                    c = Client(data, of, delegate { return 4; }, logs.Add);
                    Check(c.SettingsDamaged && !c.Connected, "the damaged-connection notice survives a restart until the next connect");
                    c.Connect(server.Origin + "/connect#" + FakeServer.Token); c.Round(idle);
                    Check(c.Connected && !c.SettingsDamaged && c.Device == device && !c.Rebaselining && !File.ReadAllText(settings).Contains(FakeServer.Token),
                        "connect after a damaged cloud.json works and keeps this account's journal");
                    Check(CloudClient.Map(server.App["playtime"]).Count == 1, "no second device appears on the server");
                }
            });
        }
        internal static void LedgerRecoveredOrReset()
        {
            InTemp(delegate(string dir)
            {
                string path = Path.Combine(dir, "playtime.txt"), good = "123|120|0|0|0|1700000000|0|0|5|0\r\n#cloud-receipt|cloud:abc\r\n";
                File.WriteAllBytes(path, new byte[900]); Leftover(path + "~RF77.TMP", Utf8(good), TimeSpan.FromHours(1));
                var recovered = new Playtime(path, delegate { });
                Check(recovered.PendingMinutes("123") == 2 && recovered.CloudMinutesInSteam("123") == 5 && recovered.ResetMarker == null && File.ReadAllText(path) == good,
                    "zero-filled playtime ledger recovered from its ~RF backup");
                File.WriteAllText(path, "123|12x|0|0|0|1700000000\r\n");
                var reset = new Playtime(path, delegate { });
                Check(reset.ResetMarker != null && reset.PendingMinutes("123") == 0 && File.ReadAllText(path).Contains("#ledger-reset|" + reset.ResetMarker)
                    && Names(dir).Any(n => n.StartsWith("playtime.txt.corrupt-")), "unreadable ledger quarantined; the fresh ledger records its reset");
                Check(new Playtime(path, delegate { }).ResetMarker == reset.ResetMarker, "ledger reset marker persists");
            });
        }
        internal static void LedgerResetNeverPushesImportedMinutes()
        {
            InTemp(delegate(string root)
            {
                using (var server = new FakeServer())
                {
                    string ar = Path.Combine(root, "A", "OnlineFix"), br = Path.Combine(root, "B", "OnlineFix"); DateTime date = DateTime.UtcNow.AddHours(-2);
                    Seed(ar, "A", 100, "a", date); Seed(br, "B", 200, "b", date);
                    long steamB = 7, importedB = 0; var receipts = new HashSet<string>();
                    Func<string, string, int, bool> ledger = delegate(string receipt, string app, int n) { if (receipts.Add(receipt)) { steamB += n; importedB += n; } return true; };
                    Func<string, bool> idle = delegate { return false; }; Action<string> log = delegate { };
                    var a = Client(Path.Combine(root, "A", "data"), ar, delegate { return 12; }, log);
                    var b = Client(Path.Combine(root, "B", "data"), br, delegate { return steamB - importedB; }, log);
                    a.Connect(server.Origin + "/connect#" + FakeServer.Token); b.Connect(server.Origin + "/connect#" + FakeServer.Token);
                    a.Round(idle); b.Round(idle); b.Deliver(ledger, idle); b.Round(idle);
                    // B's ledger is lost: Steam still shows 19 minutes, but nothing says 12 of them came from A.
                    importedB = 0; b.LedgerReset("reset-1"); b.Round(idle); b.LedgerReset("reset-1"); b.Round(idle);
                    Check(CloudClient.Num(CloudClient.Map(server.App["playtime"])[b.Device]) == 7, "after a ledger reset, imported minutes are not pushed back as B's own");
                    steamB += 3; b.Round(idle);
                    Check(CloudClient.Num(CloudClient.Map(server.App["playtime"])[b.Device]) == 10, "B's own new minutes are still pushed after a ledger reset");
                    // Both the journal and the ledger lost: own minutes wait for the baseline, then exclude A's.
                    File.WriteAllBytes(Path.Combine(b.Data, "cloud-state.json"), new byte[64]); steamB += 2;
                    b = Client(b.Data, br, delegate { return steamB - importedB; }, log); b.LedgerReset("reset-2"); b.Round(idle); b.Round(idle); b.Deliver(ledger, idle);
                    Check(CloudClient.Num(CloudClient.Map(server.App["playtime"])[b.Device]) == 12 && steamB == 24, "journal and ledger both lost: no minutes double counted either way");
                }
            });
        }
        internal static void DurableWritesLeaveNoTemporaryFiles()
        {
            InTemp(delegate(string dir)
            {
                string path = Path.Combine(dir, "state.json");
                for (int i = 0; i < 5; i++) StateFile.Write(path, "{\"n\":" + i + "}");
                Check(File.ReadAllText(path) == "{\"n\":4}" && Names(dir).SequenceEqual(new[] { "state.json" }), "durable writes replace in place and leave no temp or ~RF*.TMP file");
                string blocked = Path.Combine(dir, "folder"); Directory.CreateDirectory(blocked); bool failed = false;
                try { StateFile.Write(blocked, "x"); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
                Check(failed && Names(dir).SequenceEqual(new[] { "state.json" }), "a failed durable write removes its temp file");
            });
        }
        internal sealed class FakeServer : IDisposable
        {
            internal const string Token = "cab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            readonly HttpListener listener = new HttpListener(); readonly Thread thread;
            readonly JavaScriptSerializer json = new JavaScriptSerializer();
            internal readonly string Origin;
            internal readonly Dictionary<string, Dictionary<string, object>> Apps = new Dictionary<string, Dictionary<string, object>>();
            internal Dictionary<string, object> AppState(string id)
            {
                Dictionary<string, object> app;
                if (!Apps.TryGetValue(id, out app)) Apps[id] = app = new Dictionary<string, object> { { "achievements", new Dictionary<string, object>() }, { "stats", new Dictionary<string, object>() }, { "statsUpdatedAt", 0L }, { "playtime", new Dictionary<string, object>() }, { "playtimeTotal", 0L }, { "save", null } };
                return app;
            }
            internal Dictionary<string, object> App { get { return AppState("123"); } }
            internal readonly List<string> Pushed = new List<string>();
            internal int SaveRequests, Progress;
            internal FakeServer()
            {
                var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
                Origin = "http://localhost:" + port; listener.Prefixes.Add(Origin + "/"); listener.Start();
                thread = new Thread(Loop); thread.IsBackground = true; thread.Start();
            }
            internal void EarlierAchievement() { CloudClient.Map(App["achievements"])["A"] = 50L; }
            void Loop()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c; try { c = listener.GetContext(); } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                    try { Handle(c); }
                    catch (ObjectDisposedException) { break; }
                    catch (HttpListenerException) { break; }
                    catch (Exception ex)
                    {
                        try { c.Response.StatusCode = 500; Reply(c, new Dictionary<string, object> { { "error", ex.Message } }); }
                        catch (ObjectDisposedException) { break; }
                        catch (HttpListenerException) { break; }
                    }
                    finally
                    {
                        // Shutdown closes the HTTP queue before the worker's response handle.
                        try { c.Response.Close(); }
                        catch (ObjectDisposedException) { }
                        catch (HttpListenerException) { }
                    }
                }
            }
            void Reply(HttpListenerContext c, object obj)
            { byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(obj)); c.Response.ContentType = "application/json"; c.Response.OutputStream.Write(bytes, 0, bytes.Length); }
            void Handle(HttpListenerContext c)
            {
                if (c.Request.Headers["Authorization"] != "Bearer " + Token) { c.Response.StatusCode = 401; Reply(c, new { error = "unauthorized" }); return; }
                if (!c.Request.UserAgent.StartsWith("Cabinet/")) throw new Exception("Missing user agent");
                string path = c.Request.Url.AbsolutePath;
                if (path == "/api/me") { Reply(c, new { user = new { id = "test-user", name = "Tester" } }); return; }
                if (path == "/api/state") { AppState("123"); Reply(c, new { apps = Apps.ToDictionary(kv => kv.Key, kv => (object)kv.Value) }); return; }
                var progress = Regex.Match(path, "^/api/apps/([0-9]+)/progress$");
                if (progress.Success && c.Request.HttpMethod == "POST")
                {
                    Progress++; Dictionary<string, object> body; var App = AppState(progress.Groups[1].Value); Pushed.Add(progress.Groups[1].Value);
                    using (var r = new StreamReader(c.Request.InputStream)) body = CloudClient.Map(json.DeserializeObject(r.ReadToEnd()));
                    var ach = CloudClient.Map(App["achievements"]);
                    foreach (var item in CloudClient.Map(body["achievements"]))
                    { object prev; ach[item.Key] = ach.TryGetValue(item.Key, out prev) ? Math.Min(CloudClient.Num(prev), CloudClient.Num(item.Value)) : CloudClient.Num(item.Value); }
                    if (body.ContainsKey("statsUpdatedAt") && CloudClient.Num(body["statsUpdatedAt"]) > CloudClient.Num(App["statsUpdatedAt"])) { App["stats"] = body["stats"]; App["statsUpdatedAt"] = body["statsUpdatedAt"]; }
                    var times = CloudClient.Map(App["playtime"]); string device = Convert.ToString(body["deviceId"]); object n;
                    times[device] = Math.Max(times.TryGetValue(device, out n) ? CloudClient.Num(n) : 0, CloudClient.Num(body["playtimeMinutes"])); App["playtimeTotal"] = times.Values.Sum(v => CloudClient.Num(v));
                    Reply(c, App); return;
                }
                if (path.EndsWith("/save") || path.EndsWith("/archive")) SaveRequests++;
                c.Response.StatusCode = 404; Reply(c, new { error = "not_found" });
            }
            public void Dispose() { listener.Close(); thread.Join(5000); }
        }
    }
    // damaged: the saved connection was lost to a damaged file; cleared by the next connect.
    sealed class CloudSettings { public string server, token, deviceId, deviceName, userName, userId; public bool damaged; }
    sealed class CloudAppJournal
    {
        public string progressHash, statsHash, saveHash;
        // importedBase: minutes in Steam's playtime imported from the cloud that the playtime ledger no
        // longer counts (it was reset); they are never pushed back as this PC's own playtime.
        public long statsUpdatedAt, saveUpdatedAt, ownMinutes, importedBase;
        // remote: per other device, the server total already applied here; pending: totals to apply.
        public Dictionary<string, long> remote = new Dictionary<string, long>();
        public Dictionary<string, long> pending = new Dictionary<string, long>();
        // The next pull records the server's totals as already applied instead of adding them.
        public bool rebaseline;
    }
    sealed class CloudJournal
    {
        public Dictionary<string, CloudAppJournal> apps = new Dictionary<string, CloudAppJournal>();
        // rebaseline: this journal replaced a damaged one, so it cannot tell which remote minutes were
        // applied; the next round baselines every app on the server. importsUnknown: the playtime ledger
        // was reset too, so baselined minutes also count as imported (importedBase).
        public bool rebaseline, importsUnknown;
        // Owner (device and account hash), so a damaged cloud.json can be rebuilt around this journal,
        // and the last playtime ledger reset already accounted for.
        public string deviceId, account, ledgerReset;
    }
    sealed class CloudClient
    {
        internal readonly string Data, Root;
        readonly Func<string, long> minutes;
        readonly Action<string> log;
        CloudSettings settings;
        CloudJournal journal;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        public string UserName { get { return settings.userName ?? settings.deviceName; } }
        public bool Connected { get { return !string.IsNullOrEmpty(settings.token); } }
        public string Server { get { return settings.server; } } // LuaTools: shown in Settings
        internal string Token { get { return settings.token; } } // LuaTools: CloudRedirect only.
        internal int ConnectionVersion { get; private set; }
        internal string Device { get { return settings.deviceId; } }
        internal bool SettingsDamaged { get { return settings.damaged; } }
        internal bool Rebaselining { get { return journal.rebaseline || journal.apps.Values.Any(a => a.rebaseline); } }
        // LuaTools: games with a Lua file sync achievements too, through Steam's own stats cache.
        // Replaceable so the self-test never reads or writes the real Steam folder.
        internal Func<IEnumerable<string>> LuaApps = Syncer.LuaAppIds;
        internal Func<string, Dictionary<string, long>> SteamAchievements = delegate(string app)
        { string account = Syncer.AccountId(); return account == null ? null : Syncer.ReadSteamAchievements(app, account); };
        internal Func<string, IDictionary<string, long>, int> MergeSteamAchievements = delegate(string app, IDictionary<string, long> unlocks)
        { string account = Syncer.AccountId(); return account == null ? -1 : Syncer.MergeSteamAchievements(app, account, unlocks); };
        internal CloudClient(string data, string root, Func<string, long> getMinutes, Action<string> logger)
        {
            Data = data; Root = root; minutes = getMinutes; log = logger; Directory.CreateDirectory(Data);
            // A state file damaged by a crash or power loss never stops the client: StateFile recovers it
            // from an interrupted write's leftover, or moves it aside and the client starts safely.
            StateLoad stored;
            settings = StateFile.Load(Path.Combine(Data, "cloud.json"), ParseSettings, log, out stored);
            if (settings == null)
                settings = new CloudSettings { server = LuaToolsGui.AppConfig.CabinetDefaultServer, deviceId = Guid.NewGuid().ToString("N"), deviceName = Environment.MachineName, damaged = stored == StateLoad.Damaged };
            else if (!string.IsNullOrEmpty(settings.token))
            {
                try { settings.token = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.token), null, DataProtectionScope.CurrentUser)); }
                catch (CryptographicException) { settings.token = null; log("Stored token cannot be decrypted for this Windows user"); }
            }
            journal = LoadJournal(Path.Combine(Data, "cloud-state.json"), false);
            if (stored == StateLoad.Damaged)
            {
                // Keep this PC's device ID when the journal knows it: a new one would make the server
                // count this PC's playtime twice.
                if (!string.IsNullOrEmpty(journal.deviceId)) settings.deviceId = journal.deviceId;
                log("Cabinet Cloud connection settings were lost; connect again");
            }
            if (stored == StateLoad.Missing || stored == StateLoad.Damaged) Persist(SaveSettings);
            if (journal.rebaseline) Persist(SaveJournal);
        }
        void Persist(Action save) { try { save(); } catch (Exception ex) { log("state not saved yet: " + ex.GetType().Name); } }
        static CloudSettings ParseSettings(string text)
        { var s = Json.Deserialize<CloudSettings>(text); return s == null || string.IsNullOrEmpty(s.deviceId) ? null : s; }
        static CloudJournal ParseJournal(string text)
        {
            var j = Json.Deserialize<CloudJournal>(text);
            if (j == null || j.apps == null || j.apps.Values.Any(a => a == null)) return null;
            foreach (var a in j.apps.Values)
            { if (a.remote == null) a.remote = new Dictionary<string, long>(); if (a.pending == null) a.pending = new Dictionary<string, long>(); }
            return j;
        }
        // A journal that replaces a damaged one, or one whose history is unknown, starts with a rebaseline.
        CloudJournal LoadJournal(string path, bool unknownHistory)
        {
            StateLoad loaded; var j = StateFile.Load(path, ParseJournal, log, out loaded);
            return j ?? new CloudJournal { rebaseline = unknownHistory || loaded == StateLoad.Damaged };
        }
        static void Atomic(string path, string text) { StateFile.Write(path, text); }
        static string AccountKey(string server, string userId) { return Hash(Encoding.UTF8.GetBytes(server + ":" + userId)); }
        string savedJournal;
        void SaveJournal()
        {
            journal.deviceId = settings.deviceId;
            if (!string.IsNullOrEmpty(settings.userId)) journal.account = AccountKey(settings.server, settings.userId);
            // Rounds save after every app; only changes reach the disk.
            string text = Json.Serialize(journal); if (text == savedJournal) return;
            Atomic(Path.Combine(Data, "cloud-state.json"), text); savedJournal = text;
        }
        void SaveSettings()
        {
            var stored = new CloudSettings { server = settings.server, token = string.IsNullOrEmpty(settings.token) ? null : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.token), null, DataProtectionScope.CurrentUser)), deviceId = settings.deviceId, deviceName = settings.deviceName, userName = settings.userName, userId = settings.userId, damaged = settings.damaged };
            Atomic(Path.Combine(Data, "cloud.json"), Json.Serialize(stored));
        }
        public static void ParseLink(string link, out string server, out string token)
        {
            Uri u; if (!Uri.TryCreate(link, UriKind.Absolute, out u)) throw new FormatException("Invalid link");
            if (u.Scheme == "cabinet" && u.Host == "connect")
            {
                var values = new Dictionary<string, string>();
                foreach (string pair in u.Query.TrimStart('?').Split('&'))
                { int i = pair.IndexOf('='); if (i > 0) values[Uri.UnescapeDataString(pair.Substring(0, i))] = Uri.UnescapeDataString(pair.Substring(i + 1)); }
                if (!values.TryGetValue("server", out server) || !values.TryGetValue("token", out token)) throw new FormatException("Missing credentials");
            }
            else if ((u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback)) && u.AbsolutePath == "/connect")
            { server = u.GetLeftPart(UriPartial.Authority); token = Uri.UnescapeDataString(u.Fragment.TrimStart('#')); }
            else throw new FormatException("Invalid link");
            Uri origin;
            if (!Uri.TryCreate(server, UriKind.Absolute, out origin) || !(origin.Scheme == "https" || (origin.Scheme == "http" && origin.IsLoopback)) || origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != "" || origin.UserInfo != "") throw new FormatException("Invalid server origin");
            server = origin.GetLeftPart(UriPartial.Authority);
            if (!Regex.IsMatch(token, @"^cab_[A-Za-z0-9_-]{43}$")) throw new FormatException("Invalid token");
        }
        public void Connect(string input, string pairingServer = null)
        {
            string server, token;
            if (CabinetConnectInput.TryNormalizeCode(input, out string code))
            {
                server = CabinetConnectInput.NormalizeServer(pairingServer);
                using (var pairing = new CabinetPairingClient()) token = pairing.RedeemAsync(server, code).GetAwaiter().GetResult();
            }
            else { ParseLink(input.Trim(), out server, out token); }
            ConnectCredentials(server, token);
        }
        // LuaTools: links and single-use pairing codes share verification and DPAPI persistence.
        internal void ConnectCredentials(string server, string token)
        {
            var previous = settings; var previousJournal = journal;
            settings = new CloudSettings { server = server, token = token, deviceId = previous.deviceId, deviceName = previous.deviceName };
            try
            {
                var me = Map(Map(RequestJson("GET", "/api/me", null))["user"]);
                settings.userName = Convert.ToString(me["name"]); settings.userId = Convert.ToString(me["id"]);
                string account = AccountKey(server, settings.userId);
                // After a damaged cloud.json, the journal on disk may still be this account's: keep it.
                if ((previous.server != server || previous.userId != settings.userId) && !(previous.damaged && journal.account == account))
                {
                    // Account-specific remote watermarks must not suppress another account's
                    // progress, or relabel old pending receipts as belonging to the new user.
                    string owner = !string.IsNullOrEmpty(previous.userId) ? AccountKey(previous.server, previous.userId) : journal.account;
                    if (owner != null) Atomic(Path.Combine(Data, "cloud-account-" + owner + ".json"), Json.Serialize(journal));
                    // Without a known owner, the old journal may have been this account's: what this PC
                    // already applied is unknown, so a fresh journal rebaselines.
                    journal = LoadJournal(Path.Combine(Data, "cloud-account-" + account + ".json"), previous.damaged && owner == null);
                }
                SaveSettings(); SaveJournal(); ConnectionVersion++;
            }
            catch { settings = previous; journal = previousJournal; throw; }
        }
        public void Disconnect()
        {
            string token = settings.token; settings.token = null;
            try { SaveSettings(); ConnectionVersion++; }
            catch { settings.token = token; throw; }
        }
        internal static Dictionary<string, object> Map(object obj) { return obj as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static long Num(object obj) { return Convert.ToInt64(obj, CultureInfo.InvariantCulture); }
        internal static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
        internal static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal object RequestJson(string method, string path, object body)
        { return Json.DeserializeObject(Encoding.UTF8.GetString(Request(method, path, body == null ? null : Encoding.UTF8.GetBytes(Json.Serialize(body)), "application/json", false))); }
        internal string PresenceKey { get { return settings.server + ":" + settings.userId + ":" + settings.token; } }
        internal Func<string, string, object, object> PresenceRequest()
        {
            string server = settings.server, token = settings.token;
            return delegate(string method, string path, object body)
            {
                var json = new JavaScriptSerializer();
                byte[] bytes = RequestTo(server, token, method, path, body == null ? null : Encoding.UTF8.GetBytes(json.Serialize(body)), "application/json", false, 5000);
                return json.DeserializeObject(Encoding.UTF8.GetString(bytes));
            };
        }
        byte[] Request(string method, string path, byte[] body, string type, bool acceptConflict)
        { return RequestTo(settings.server, settings.token, method, path, body, type, acceptConflict, 30000); }
        static byte[] RequestTo(string server, string token, string method, string path, byte[] body, string type, bool acceptConflict, int timeout)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(server + path);
            req.Method = method; req.Timeout = timeout; req.ReadWriteTimeout = timeout; req.AllowAutoRedirect = false;
            req.UserAgent = "Cabinet/" + BuildInfo.Version; req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            // Bedrock rejects writes whose Origin isn't the pebble's own.
            req.Headers["Origin"] = new Uri(server).GetLeftPart(UriPartial.Authority);
            if (body != null) { req.ContentType = type; req.ContentLength = body.Length; using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length); }
            HttpWebResponse response;
            try { response = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse; if (response == null) throw;
                if (!acceptConflict || response.StatusCode != HttpStatusCode.Conflict) { response.Dispose(); throw; }
            }
            using (response) using (var s = response.GetResponseStream()) using (var ms = new MemoryStream())
            {
                byte[] buffer = new byte[65536]; int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) { if (ms.Length + n > 90L * 1024 * 1024) throw new InvalidDataException("Cloud response too large"); ms.Write(buffer, 0, n); }
                return ms.ToArray();
            }
        }
        CloudAppJournal AppJournal(string app)
        { CloudAppJournal a; if (!journal.apps.TryGetValue(app, out a)) { a = new CloudAppJournal(); journal.apps[app] = a; } return a; }
        static bool AppId(string id) { return Regex.IsMatch(id, @"^[0-9]{1,10}$"); }
        string StatsPath(string app, string file) { return Path.Combine(Root, app, "Stats", file); }
        Dictionary<string, object> Achievements(string app)
        {
            var result = new Dictionary<string, object>();
            foreach (var s in Syncer.ReadIni(StatsPath(app, "Achievements.ini")))
            {
                string yes, time; long ts;
                if (!s.Value.TryGetValue("achieved", out yes) || !yes.Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
                if (!s.Value.TryGetValue("timestamp", out time) || !long.TryParse(time, out ts) || ts <= 0) ts = Playtime.Unix(File.GetLastWriteTimeUtc(StatsPath(app, "Achievements.ini")));
                result[s.Key] = ts;
            }
            return result;
        }
        Dictionary<string, object> Stats(string app)
        {
            var result = new Dictionary<string, object>(); Dictionary<string, string> section;
            if (Syncer.ReadIni(StatsPath(app, "Stats.ini")).TryGetValue("Stats", out section))
                foreach (var s in section) { double n; if (double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !double.IsNaN(n) && !double.IsInfinity(n)) result[s.Key] = n; }
            return result;
        }
        bool OnlineFix(string id) { return Directory.Exists(Path.Combine(Root, id)); }
        public void Round(Func<string, bool> blocked)
        {
            // LuaTools: OnlineFix folders plus games with a Lua file; games only owned on Steam are not synced.
            var lua = new HashSet<string>(LuaApps().Where(AppId), StringComparer.Ordinal);
            var ids = new SortedSet<string>(lua, StringComparer.Ordinal);
            foreach (string dir in Directory.Exists(Root) ? Directory.GetDirectories(Root) : new string[0])
            { string id = Path.GetFileName(dir); if (AppId(id)) ids.Add(id); }
            foreach (string id in ids)
            {
                if (blocked(id)) continue;
                try { Push(id, blocked, lua.Contains(id)); } catch (Exception ex) { log("push " + id + ": " + ex.Message); }
            }
            var state = Map(Map(RequestJson("GET", "/api/state", null))["apps"]);
            if (journal.rebaseline)
            {
                // First round after the journal was reset: it cannot tell which of the other devices'
                // minutes were already applied here, so each app on the server takes its totals as a new
                // baseline when it is next pulled (now, or once it stops running), rather than adding them again.
                int count = 0;
                foreach (var app in state) if (AppId(app.Key)) { AppJournal(app.Key).rebaseline = true; count++; }
                journal.rebaseline = false; SaveJournal();
                log("journal reset: the server's playtime totals for " + count + " apps count as already applied");
            }
            foreach (var app in state)
            {
                if (!AppId(app.Key) || blocked(app.Key)) continue;
                try { Apply(app.Key, Map(app.Value), blocked, lua.Contains(app.Key)); } catch (Exception ex) { log("pull " + app.Key + ": " + ex.Message); }
            }
            if (journal.importsUnknown && !journal.apps.Values.Any(a => a.rebaseline)) journal.importsUnknown = false;
            SaveJournal();
        }
        // The playtime ledger was reset (a damaged playtime.txt): Steam's playtime still holds the cloud
        // minutes imported before, but the new ledger no longer counts them, so they would be pushed back as
        // this PC's own playtime. Every remote minute this journal applied counts as imported instead. The
        // marker is remembered per journal, so each account's journal is adjusted once.
        internal void LedgerReset(string marker)
        {
            if (string.IsNullOrEmpty(marker) || marker == journal.ledgerReset) return;
            foreach (var a in journal.apps.Values) a.importedBase = Math.Max(a.importedBase, a.remote.Values.Sum());
            // Apps still waiting for a baseline get theirs from the server's totals.
            if (journal.rebaseline || journal.apps.Values.Any(a => a.rebaseline)) journal.importsUnknown = true;
            journal.ledgerReset = marker; SaveJournal();
            log("playtime ledger reset: cloud minutes already in Steam stay counted as imported");
        }
        void Push(string id, Func<string, bool> blocked, bool lua)
        {
            bool onlineFix = OnlineFix(id);
            var a = AppJournal(id); var ach = onlineFix ? Achievements(id) : new Dictionary<string, object>();
            if (lua)
            {
                // LuaTools: unlocks Steam recorded for the Lua game join OnlineFix's; the earliest time wins.
                var steam = SteamAchievements(id);
                if (steam != null) foreach (var kv in steam)
                { object old; ach[kv.Key] = ach.TryGetValue(kv.Key, out old) ? Math.Min(Num(old), kv.Value) : kv.Value; }
                // DLC unlocks are bare <dlc>.lua files, and unplayed games have nothing to report.
                if (!onlineFix && ach.Count == 0 && a.progressHash == null) return;
            }
            var stats = onlineFix ? Stats(id) : new Dictionary<string, object>();
            string statsHash = Hash(Encoding.UTF8.GetBytes(Json.Serialize(stats)));
            if (statsHash != a.statsHash)
            {
                a.statsHash = statsHash; string path = StatsPath(id, "Stats.ini");
                a.statsUpdatedAt = File.Exists(path) ? (long)(File.GetLastWriteTimeUtc(path) - new DateTime(1970, 1, 1)).TotalMilliseconds : 0;
            }
            // LuaTools: Cabinet's playtime ledger covers OnlineFix games only; Steam itself counts Lua games.
            // Own minutes wait for an app's baseline when Steam's imported minutes are unknown.
            if (onlineFix && !(journal.importsUnknown && (journal.rebaseline || a.rebaseline))) a.ownMinutes = Math.Max(a.ownMinutes, minutes(id) - a.importedBase);
            var body = new Dictionary<string, object> { { "deviceId", Device }, { "deviceName", settings.deviceName }, { "achievements", ach }, { "playtimeMinutes", a.ownMinutes } };
            // LuaTools: stats come only from OnlineFix's Stats.ini.
            if (onlineFix) { body["stats"] = stats; body["statsUpdatedAt"] = a.statsUpdatedAt; }
            string progress = Hash(Encoding.UTF8.GetBytes(Json.Serialize(body))); if (blocked(id)) return;
            if (progress != a.progressHash) { RequestJson("POST", "/api/apps/" + id + "/progress", body); a.progressHash = progress; SaveJournal(); }
            // LuaTools: saves sync through CloudRedirect (Steam Cloud paths) to this Cabinet server, so the
            // engine no longer uploads or restores the OnlineFix Saves folder.
        }
        void Apply(string id, Dictionary<string, object> state, Func<string, bool> blocked, bool lua)
        {
            var a = AppJournal(id);
            // LuaTools: write only where the game is set up here. OnlineFix INIs are not created for other games
            // (Cabinet would copy them into Steam), and a Lua-only game gets its unlocks in Steam's stats cache.
            if (OnlineFix(id)) ApplyOnlineFix(id, state, blocked, a);
            else if (lua && !blocked(id))
            {
                var unlocks = new Dictionary<string, long>();
                foreach (var ach in Map(state["achievements"])) unlocks[ach.Key] = Num(ach.Value);
                int added = unlocks.Count == 0 ? 0 : MergeSteamAchievements(id, unlocks);
                if (added > 0) log("pull " + id + ": +" + added + " achievements into Steam");
            }
            foreach (var time in Map(state["playtime"]))
            {
                if (time.Key == Device) continue; long previous; a.remote.TryGetValue(time.Key, out previous); long total = Num(time.Value);
                // A new baseline records the totals already on the server as applied here: never add them twice.
                if (a.rebaseline) { if (total > previous) a.remote[time.Key] = total; a.pending.Remove(time.Key); }
                else if (total > previous) a.pending[time.Key] = total;
            }
            if (a.rebaseline)
            {
                // With the playtime ledger reset too, Steam already holds these minutes, uncounted.
                if (journal.importsUnknown) a.importedBase = Math.Max(a.importedBase, a.remote.Values.Sum());
                a.rebaseline = false;
            }
            SaveJournal();
        }
        void ApplyOnlineFix(string id, Dictionary<string, object> state, Func<string, bool> blocked, CloudAppJournal a)
        {
            string achPath = StatsPath(id, "Achievements.ini"); var local = Syncer.ReadIni(achPath);
            foreach (var ach in Map(state["achievements"]))
            {
                if (blocked(id)) return; long ts = Num(ach.Value); Dictionary<string, string> sec; string t, yes; long old;
                if (local.TryGetValue(ach.Key, out sec) && sec.TryGetValue("achieved", out yes) && yes.Equals("true", StringComparison.OrdinalIgnoreCase) && sec.TryGetValue("timestamp", out t) && long.TryParse(t, out old) && old > 0) ts = Math.Min(ts, old);
                EditIni(achPath, ach.Key, new Dictionary<string, string> { { "achieved", "true" }, { "timestamp", ts.ToString(CultureInfo.InvariantCulture) } }, false);
            }
            long statsTime = Num(state["statsUpdatedAt"]); string sp = StatsPath(id, "Stats.ini");
            long localTime = File.Exists(sp) ? (long)(File.GetLastWriteTimeUtc(sp) - new DateTime(1970, 1, 1)).TotalMilliseconds : 0;
            if (statsTime > Math.Max(a.statsUpdatedAt, localTime) && !blocked(id))
            {
                var values = new Dictionary<string, string>(); foreach (var st in Map(state["stats"])) values[st.Key] = Convert.ToString(st.Value, CultureInfo.InvariantCulture);
                EditIni(sp, "Stats", values, true); File.SetLastWriteTimeUtc(sp, new DateTime(1970, 1, 1).AddMilliseconds(statsTime));
                a.statsUpdatedAt = statsTime; a.statsHash = Hash(Encoding.UTF8.GetBytes(Json.Serialize(Stats(id))));
            }
        }
        public void Deliver(Func<string, string, int, bool> add, Func<string, bool> blocked)
        {
            if (journal.rebaseline) return;
            foreach (var app in journal.apps)
            {
                if (app.Value.rebaseline || blocked(app.Key)) continue;
                foreach (var remote in app.Value.pending.ToArray())
                {
                    if (blocked(app.Key)) break;
                    long previous; app.Value.remote.TryGetValue(remote.Key, out previous); long delta = remote.Value - previous;
                    if (delta <= 0) { app.Value.pending.Remove(remote.Key); continue; }
                    int chunk = (int)Math.Min(int.MaxValue, delta); long target = previous + chunk;
                    string receipt = "cloud:" + Hash(Encoding.UTF8.GetBytes(settings.server + ":" + settings.userId + ":" + app.Key + ":" + remote.Key + ":" + target));
                    if (add(receipt, app.Key, chunk)) { app.Value.remote[remote.Key] = target; if (target == remote.Value) app.Value.pending.Remove(remote.Key); SaveJournal(); }
                }
            }
        }
        // Preserve comments, unrelated sections, detected encoding and newline style.
        static void EditIni(string path, string section, Dictionary<string, string> values, bool replace)
        {
            if (section.IndexOfAny(new[] { '\r', '\n', '[', ']' }) >= 0 || values.Keys.Any(k => k.IndexOfAny(new[] { '\r', '\n', '=' }) >= 0)) throw new InvalidDataException("Invalid INI key");
            Encoding encoding = new UTF8Encoding(false); string text = "";
            if (File.Exists(path)) using (var reader = new StreamReader(path, encoding, true)) { text = reader.ReadToEnd(); encoding = reader.CurrentEncoding; }
            string nl = text.Contains("\r\n") ? "\r\n" : "\n"; var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            int start = -1, end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i].Trim();
                if (s.StartsWith("[") && s.EndsWith("]"))
                { if (start >= 0) { end = i; break; } if (s.Substring(1, s.Length - 2).Equals(section, StringComparison.OrdinalIgnoreCase)) start = i; }
            }
            if (start < 0) { lines.Add("[" + section + "]"); start = lines.Count - 1; end = lines.Count; }
            var lookup = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            var remaining = new Dictionary<string, string>(lookup, StringComparer.OrdinalIgnoreCase);
            for (int i = start + 1; i < end; i++)
            {
                string s = lines[i].Trim(); int eq = s.IndexOf('='); if (eq <= 0 || s.StartsWith(";") || s.StartsWith("#")) continue;
                string key = s.Substring(0, eq).Trim(), value;
                if (lookup.TryGetValue(key, out value)) { lines[i] = lines[i].Substring(0, lines[i].IndexOf('=') + 1) + value; remaining.Remove(key); }
                else if (replace) { lines.RemoveAt(i--); end--; }
            }
            foreach (var value in remaining) lines.Insert(end++, value.Key + "=" + value.Value);
            string next = string.Join(nl, lines); if (next == text) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string tmp = path + ".cloud.tmp";
            try { StateFile.WriteFlushed(tmp, next, encoding); StateFile.Commit(tmp, path); }
            finally { StateFile.TryDelete(tmp); }
        }
    }
}
