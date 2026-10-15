#nullable disable
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace LuaToolsGui.Services.Cabinet;
    sealed class CloudSettings { public string server, token, deviceId, deviceName, userName, userId; }
    sealed class CloudAppJournal
    {
        public string progressHash, statsHash, saveHash;
        public long statsUpdatedAt, saveUpdatedAt, ownMinutes;
        public Dictionary<string, long> remote = new Dictionary<string, long>();
        public Dictionary<string, long> pending = new Dictionary<string, long>();
    }
    sealed class CloudJournal { public Dictionary<string, CloudAppJournal> apps = new Dictionary<string, CloudAppJournal>(); }
    sealed class CabinetCloudClient
    {
        internal readonly string Data, Root;
        readonly Func<string, long> minutes;
        readonly Action<string> log;
        CloudSettings settings;
        CloudJournal journal;
        static readonly CabinetJson Json = new CabinetJson { MaxJsonLength = 4 * 1024 * 1024 };
        public string UserName { get { return settings.userName ?? settings.deviceName; } }
        public bool Connected { get { return !string.IsNullOrEmpty(settings.token); } }
        internal string Device { get { return settings.deviceId; } }
        internal CabinetCloudClient(string data, string root, Func<string, long> getMinutes, Action<string> logger)
        {
            Data = data; Root = root; minutes = getMinutes; log = logger; CabinetFiles.EnsureSafePath(Data); Directory.CreateDirectory(Data);
            settings = new CloudSettings { server = null, deviceId = Guid.NewGuid().ToString("N"), deviceName = Environment.MachineName };
            string p = Path.Combine(Data, "cloud.json");
            if (File.Exists(p))
            {
                settings = Json.Deserialize<CloudSettings>(CabinetFiles.ReadText(p));
                if (!string.IsNullOrEmpty(settings.server)) ValidateOrigin(settings.server);
                if (!string.IsNullOrEmpty(settings.token))
                {
                    settings.token = SettingsService.UnprotectApiKey(settings.token);
                    if (settings.token == null) log("Stored cloud token is unavailable");
                }
            }
            else SaveSettings();
            string state = Path.Combine(Data, "cloud-state.json");
            journal = File.Exists(state) ? Json.Deserialize<CloudJournal>(CabinetFiles.ReadText(state)) : new CloudJournal();
        }
        static void Atomic(string path, string text)
        {
            CabinetFiles.EnsureSafePath(path);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        void SaveJournal() { Atomic(Path.Combine(Data, "cloud-state.json"), Json.Serialize(journal)); }
        void SaveSettings()
        {
            var stored = new CloudSettings { server = settings.server, token = string.IsNullOrEmpty(settings.token) ? null : SettingsService.ProtectApiKey(settings.token), deviceId = settings.deviceId, deviceName = settings.deviceName, userName = settings.userName, userId = settings.userId };
            Atomic(Path.Combine(Data, "cloud.json"), Json.Serialize(stored));
        }
        internal static void ValidateOrigin(string server)
        {
            if (!Uri.TryCreate(server, UriKind.Absolute, out var origin) || origin.AbsolutePath != "/"
                || origin.Query.Length != 0 || origin.Fragment.Length != 0) throw new FormatException("Invalid server origin");
            try { _ = new OutboundNetworkPolicy(externalDestination: origin, cabinetCloud: true); }
            catch { throw new FormatException("Invalid server origin"); }
        }
        public static void ParseLink(string link, out string server, out string token)
        {
            server = token = null;
            if (link == null || link.Length > 4096 || link.Any(char.IsControl) || link.Contains('\\')
                || !Uri.TryCreate(link, UriKind.Absolute, out var u) || u.UserInfo.Length != 0)
                throw new FormatException("Invalid connect link");
            if (u.Scheme == "cabinet" && u.Host == "connect" && u.Port == -1 && u.Fragment.Length == 0 && u.AbsolutePath is "" or "/")
            {
                var values = new Dictionary<string, string>();
                foreach (var pair in u.Query.TrimStart('?').Split('&'))
                {
                    int i = pair.IndexOf('=');
                    if (i <= 0 || !values.TryAdd(Uri.UnescapeDataString(pair[..i]), Uri.UnescapeDataString(pair[(i+1)..])))
                        throw new FormatException("Invalid connect link");
                }
                if (values.Count != 2 || !values.TryGetValue("server", out server) || !values.TryGetValue("token", out token))
                    throw new FormatException("Invalid connect link");
            }
            else if (u.Scheme == "https" && u.AbsolutePath == "/connect" && u.Query.Length == 0)
            { server = u.GetLeftPart(UriPartial.Authority); token = Uri.UnescapeDataString(u.Fragment.TrimStart('#')); }
            else throw new FormatException("Invalid connect link");
            ValidateOrigin(server);
            server = new Uri(server).GetLeftPart(UriPartial.Authority);
            if (!Regex.IsMatch(token, @"\Acab_[A-Za-z0-9_-]{43}\z")) throw new FormatException("Invalid token");
        }
        public void Connect(string link)
        {
            string server, token; ParseLink(link, out server, out token); var previous = settings; var previousJournal = journal;
            settings = new CloudSettings { server = server, token = token, deviceId = previous.deviceId, deviceName = previous.deviceName };
            try
            {
                var me = Map(Map(RequestJson("GET", "/api/me", null))["user"]);
                settings.userName = Convert.ToString(me["name"]); settings.userId = Convert.ToString(me["id"]);
                if (previous.server != server || previous.userId != settings.userId)
                {
                    // Account-specific remote watermarks must not suppress another account's
                    // progress, or relabel old pending receipts as belonging to the new user.
                    if (!string.IsNullOrEmpty(previous.userId))
                        Atomic(Path.Combine(Data, "cloud-account-" + Hash(Encoding.UTF8.GetBytes(previous.server + ":" + previous.userId)) + ".json"), Json.Serialize(journal));
                    string archive = Path.Combine(Data, "cloud-account-" + Hash(Encoding.UTF8.GetBytes(server + ":" + settings.userId)) + ".json");
                    journal = File.Exists(archive) ? Json.Deserialize<CloudJournal>(CabinetFiles.ReadText(archive)) : new CloudJournal();
                }
                SaveSettings(); SaveJournal();
            }
            catch { settings = previous; journal = previousJournal; throw; }
        }
        public void Disconnect() { settings.token = null; SaveSettings(); }
        internal static Dictionary<string, object> Map(object obj) { return obj as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static long Num(object obj) { return Convert.ToInt64(obj, CultureInfo.InvariantCulture); }
        internal static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
        internal static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal object RequestJson(string method, string path, object body)
        { return Json.DeserializeObject(Encoding.UTF8.GetString(Request(method, path, body == null ? null : Encoding.UTF8.GetBytes(Json.Serialize(body)), "application/json", false))); }
        internal string Server => settings.server;
        internal Func<Uri, HttpClient> Transport = origin => AppHttp.CreateCabinetCloud(origin);
        internal CancellationToken Cancellation;
        byte[] Request(string method, string path, byte[] body, string type, bool acceptConflict)
        {
            using var client = Transport(new Uri(settings.server));
            using var req = new HttpRequestMessage(new HttpMethod(method), settings.server + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.token);
            req.Headers.TryAddWithoutValidation("Origin", settings.server);
            req.Headers.UserAgent.ParseAdd("LuaTools-Cabinet/1.0");
            if (body != null) { req.Content = new ByteArrayContent(body); req.Content.Headers.ContentType = new MediaTypeHeaderValue(type); }
            if (path.Contains("/save")) new HttpResponseBudget(90L * 1024 * 1024, TimeSpan.FromSeconds(30)).Apply(req);
            try
            {
                using var response = client.SendAsync(req, Cancellation).GetAwaiter().GetResult();
                if (!(acceptConflict && response.StatusCode == HttpStatusCode.Conflict)) response.EnsureSuccessStatusCode();
                return response.Content.ReadAsByteArrayAsync(Cancellation).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { throw; }
            catch { throw new IOException("Cabinet Cloud request failed"); } // Never expose transport URLs, bearer tokens or server text.
        }
        CloudAppJournal AppJournal(string app)
        { CloudAppJournal a; if (!journal.apps.TryGetValue(app, out a)) { a = new CloudAppJournal(); journal.apps[app] = a; } return a; }
        static bool AppId(string id) { return uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 && id == n.ToString(CultureInfo.InvariantCulture); }
        string StatsPath(string app, string file) { CabinetPaths.Numeric(app); var path = Path.Combine(Root, app, "Stats", file); CabinetFiles.EnsureSafePath(path); return path; }
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
        public void Round(Func<string, bool> blocked)
        {
            foreach (string dir in Directory.Exists(Root) ? Directory.GetDirectories(Root) : new string[0])
            {
                string id = Path.GetFileName(dir); if (!AppId(id) || blocked(id)) continue;
                try { Push(id, blocked); } catch (OperationCanceledException) { throw; } catch { log("Cloud push deferred for " + id); }
            }
            var state = Map(Map(RequestJson("GET", "/api/state", null))["apps"]);
            foreach (var app in state)
            {
                if (!AppId(app.Key) || blocked(app.Key)) continue;
                try { Apply(app.Key, Map(app.Value), blocked); } catch (OperationCanceledException) { throw; } catch { log("Cloud pull deferred for " + app.Key); }
            }
            SaveJournal();
        }
        void Push(string id, Func<string, bool> blocked)
        {
            var a = AppJournal(id); var ach = Achievements(id); var stats = Stats(id);
            string statsHash = Hash(Encoding.UTF8.GetBytes(Json.Serialize(stats)));
            if (statsHash != a.statsHash)
            {
                a.statsHash = statsHash; string path = StatsPath(id, "Stats.ini");
                a.statsUpdatedAt = File.Exists(path) ? (long)(File.GetLastWriteTimeUtc(path) - new DateTime(1970, 1, 1)).TotalMilliseconds : 0;
            }
            a.ownMinutes = Math.Max(a.ownMinutes, minutes(id));
            var body = new Dictionary<string, object> { { "deviceId", Device }, { "deviceName", settings.deviceName }, { "achievements", ach }, { "stats", stats }, { "statsUpdatedAt", a.statsUpdatedAt }, { "playtimeMinutes", a.ownMinutes } };
            string progress = Hash(Encoding.UTF8.GetBytes(Json.Serialize(body))); if (blocked(id)) return;
            if (progress != a.progressHash) { RequestJson("POST", "/api/apps/" + id + "/progress", body); a.progressHash = progress; SaveJournal(); }
            string saves = Path.Combine(Root, id, "Saves"); if (!Directory.Exists(saves) || blocked(id)) return;
            CabinetFiles.EnsureSafePath(saves); byte[] zip = Zip(saves); string hash = Hash(zip); if (hash == a.saveHash) return;
            DateTime modified = SafeEntries(saves).Select(File.GetLastWriteTimeUtc).Concat(new[] { Directory.GetLastWriteTimeUtc(saves) }).Max();
            long updated = (long)(modified - new DateTime(1970, 1, 1)).TotalMilliseconds; if (blocked(id)) return;
            var reply = Map(Json.DeserializeObject(Encoding.UTF8.GetString(Request("PUT", "/api/apps/" + id + "/save?deviceId=" + Uri.EscapeDataString(Device) + "&updatedAt=" + updated + "&sha256=" + hash, zip, "application/zip", true))));
            // 409 remains unacknowledged until pull applies the winning snapshot.
            if (!reply.ContainsKey("current")) { a.saveHash = hash; a.saveUpdatedAt = Num(reply["updatedAt"]); SaveJournal(); }
        }
        void Apply(string id, Dictionary<string, object> state, Func<string, bool> blocked)
        {
            var a = AppJournal(id); string achPath = StatsPath(id, "Achievements.ini"); var local = Syncer.ReadIni(achPath);
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
                var values = new Dictionary<string, string>();
                foreach (var st in Map(state["stats"]))
                {
                    if (st.Value is not (long or double) || !double.IsFinite(Convert.ToDouble(st.Value, CultureInfo.InvariantCulture)))
                        throw new InvalidDataException("Invalid cloud stat value");
                    values[st.Key] = Convert.ToString(st.Value, CultureInfo.InvariantCulture);
                }
                EditIni(sp, "Stats", values, true); File.SetLastWriteTimeUtc(sp, new DateTime(1970, 1, 1).AddMilliseconds(statsTime));
                a.statsUpdatedAt = statsTime; a.statsHash = Hash(Encoding.UTF8.GetBytes(Json.Serialize(Stats(id))));
            }
            foreach (var time in Map(state["playtime"]))
            {
                if (time.Key == Device) continue; long previous; a.remote.TryGetValue(time.Key, out previous); long total = Num(time.Value);
                if (total < 0 || total > 9007199254740991L) throw new InvalidDataException("Invalid playtime");
                if (total > previous) a.pending[time.Key] = total;
            }
            object sm;
            if (state.TryGetValue("save", out sm) && sm != null)
            {
                var meta = Map(sm); string hash = Convert.ToString(meta["sha256"]); long updated = Num(meta["updatedAt"]);
                if (hash == a.saveHash) a.saveUpdatedAt = Math.Max(a.saveUpdatedAt, updated);
                if (Convert.ToString(meta["deviceId"]) != Device && updated > a.saveUpdatedAt && hash != a.saveHash && !blocked(id))
                {
                    byte[] bytes = Request("GET", "/api/apps/" + id + "/save", null, null, false);
                    if (Hash(bytes) != hash) throw new InvalidDataException("Save changed during download; retry");
                    ReplaceSaves(id, bytes, blocked); a.saveHash = hash; a.saveUpdatedAt = updated;
                }
            }
            SaveJournal();
        }
        public void Deliver(Func<string, string, int, bool> add, Func<string, bool> blocked)
        {
            foreach (var app in journal.apps)
            {
                if (!AppId(app.Key) || blocked(app.Key)) continue;
                foreach (var remote in app.Value.pending.ToArray())
                {
                    if (blocked(app.Key)) break;
                    long previous; app.Value.remote.TryGetValue(remote.Key, out previous); long delta = remote.Value - previous;
                    if (previous < 0 || remote.Value < 0 || remote.Value > 9007199254740991L) throw new InvalidDataException("Invalid receipt watermark");
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
            CabinetFiles.EnsureSafePath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)); string tmp = path + ".cloud.tmp"; File.WriteAllText(tmp, next, encoding);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        internal static byte[] Zip(string folder)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    long size = 0;
                    foreach (string path in SafeEntries(folder).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Save contains a reparse point");
                        bool dir = Directory.Exists(path); string name = path.Substring(folder.TrimEnd('\\', '/').Length + 1).Replace('\\', '/') + (dir ? "/" : "");
                        if (!dir) { size += new FileInfo(path).Length; if (size > 512L * 1024 * 1024) throw new InvalidDataException("Expanded save too large"); }
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal); entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        if (!dir) using (var src = File.OpenRead(path)) using (var dst = entry.Open())
                        { var buffer = new byte[81920]; int count; long actual = 0; while ((count = src.Read(buffer, 0, buffer.Length)) > 0) { actual += count; if (actual > 512L * 1024 * 1024 || ms.Length > 90L * 1024 * 1024) throw new InvalidDataException("Save too large"); dst.Write(buffer, 0, count); } }
                        if (ms.Length > 90L * 1024 * 1024) throw new InvalidDataException("Save exceeds 90 MB");
                    }
                }
                if (ms.Length > 90L * 1024 * 1024) throw new InvalidDataException("Save exceeds 90 MB"); return ms.ToArray();
            }
        }
        static IEnumerable<string> SafeEntries(string folder)
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Save contains a reparse point");
            foreach (string path in Directory.GetFileSystemEntries(folder))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Save contains a reparse point");
                yield return path;
                if (Directory.Exists(path)) foreach (string child in SafeEntries(path)) yield return child;
            }
        }
        internal void ReplaceSaves(string id, byte[] bytes, Func<string, bool> blocked)
        {
            CabinetPaths.Numeric(id); string parent = Path.Combine(Root, id); CabinetFiles.EnsureSafePath(parent); Directory.CreateDirectory(parent);
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("App directory is a reparse point");
            string saves = Path.Combine(parent, "Saves"), stage = Path.Combine(parent, ".cloud-" + Guid.NewGuid().ToString("N")), old = stage + "-old";
            CabinetFiles.EnsureSafePath(saves); Directory.CreateDirectory(stage);
            bool published = false;
            try
            {
                using (var ms = new MemoryStream(bytes)) using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    long size = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (zip.Entries.Count > 4096) throw new InvalidDataException("Too many archive entries");
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 200) throw new InvalidDataException("Excessive compression");
                        size += entry.Length; if (size > 512L * 1024 * 1024) throw new InvalidDataException("Expanded save too large");
                        string name = entry.FullName.Replace('/', '\\');
                        if (name.Contains(":") || name.Split('\\').Any(p => p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))) throw new InvalidDataException("Unsafe archive name");
                        string target = Path.GetFullPath(Path.Combine(stage, name));
                        if (!target.StartsWith(Path.GetFullPath(stage) + "\\", StringComparison.OrdinalIgnoreCase) || !names.Add(target)) throw new InvalidDataException("Unsafe archive path");
                        if (entry.FullName.EndsWith("/")) Directory.CreateDirectory(target);
                        else { Directory.CreateDirectory(Path.GetDirectoryName(target)); using (var src = entry.Open()) using (var dst = File.Create(target)) { var buffer = new byte[81920]; long actual = 0; int count; while ((count = src.Read(buffer, 0, buffer.Length)) > 0) { actual += count; if (actual > entry.Length) throw new InvalidDataException("Invalid expanded size"); dst.Write(buffer, 0, count); } if (actual != entry.Length) throw new InvalidDataException("Invalid expanded size"); } }
                    }
                }
                if (blocked(id)) throw new IOException("Game started; save deferred");
                if (Directory.Exists(saves))
                {
                    string backup = Path.Combine(Data, "backup", "saves", id); CabinetFiles.EnsureSafePath(backup); Directory.CreateDirectory(backup);
                    File.WriteAllBytes(Path.Combine(backup, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + ".zip"), Zip(saves));
                    if (blocked(id)) throw new IOException("Game started; save deferred"); Directory.Move(saves, old);
                }
                try { Directory.Move(stage, saves); published = true; }
                catch { if (Directory.Exists(old)) Directory.Move(old, saves); throw; }
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); if (published && Directory.Exists(old)) Directory.Delete(old, true); }
        }
    }
