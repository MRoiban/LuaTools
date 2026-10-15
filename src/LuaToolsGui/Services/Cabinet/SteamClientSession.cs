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
    // CallbackMsg_t is declared under VALVE_CALLBACK_PACK_LARGE (8) on Win64: 24 bytes, not 20.
    // Pack=1 would let Steam's struct copy write 4 bytes past this pinned local.
    [StructLayout(LayoutKind.Sequential, Pack = 8)] struct Callback { public int User, Id; public IntPtr Data; public int Size; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetCallback(int pipe, out Callback message, out ulong call);
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
            return new CabinetJson().Serialize(new Dictionary<string, string> {
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
                    snapshot = new CabinetJson().Deserialize<Dictionary<string, string>>(CabinetFiles.ReadText(source));
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
        if (!CabinetHelper.IsHelperProcess) throw new InvalidOperationException("Steam IPC is restricted to the hidden helper process");
        appId = id; log = logger;
        Environment.SetEnvironmentVariable("SteamAppId", id);
        Environment.SetEnvironmentVariable("SteamGameId", id);
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Steam helper requires x64");
        CabinetPaths.Numeric(id);
        string dll = Path.Combine(Syncer.SteamPath(), "steamclient64.dll");
        CabinetFiles.EnsureSafePath(dll);
        using var locked = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read);
        string canonical = ValveSignature.FinalPath(locked.SafeFileHandle);
        CabinetFiles.EnsureSafePath(canonical);
        if (!ValveSignature.Verify(canonical, locked.SafeFileHandle)) throw new System.Security.SecurityException("Steam client signature refused: signer must be Valve Corp.");
        var module = LoadLibraryEx(canonical, IntPtr.Zero, 0x100 | 0x800);
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
        Callback c; ulong call;
        for (int i = 0; i < 100 && getCallback(pipe, out c, out call); i++)
        {
            try
            {
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
        var game = KV.Read(CabinetFiles.ReadBytes(schemaPath)).Children.FirstOrDefault();
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
        if (client != IntPtr.Zero && pipe != 0)
        {
            if (presenceOwned && friends != IntPtr.Zero) { try { Function<PresenceClear>(friends, 42)(friends); } catch { } presenceOwned = false; }
            if (user != 0) { Function<ReleaseUser>(client, 4)(client, pipe, user); user = 0; }
            Function<ReleasePipe>(client, 1)(client, pipe); pipe = 0;
        }
        if (activity != null) { activity.Dispose(); activity = null; }
    }
}

