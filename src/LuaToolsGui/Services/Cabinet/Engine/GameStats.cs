#nullable disable
// LuaTools: live achievement and stat edits for the Tools page. Not part of upstream Cabinet.
// The GUI never loads steamclient; it starts LuaTools.exe --game-stats in a hidden child process,
// which binds to Steam as the game (SteamSession checks the AppId) and applies one edit request.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CabinetEngine
{
    sealed partial class SteamSession
    {
        // ISteamUserStats013 vtable slots on Windows (MSVC). Valve's isteamuserstats.h (SDK 1.61+, interface
        // STEAMUSERSTATS_INTERFACE_VERSION013) declares, in order: GetStat(int32*), GetStat(float*),
        // SetStat(int32), SetStat(float), UpdateAvgRateStat, GetAchievement, SetAchievement, ClearAchievement,
        // GetAchievementAndUnlockTime, StoreStats; RequestCurrentStats was removed in 013. MSVC lays out each
        // group of overloads in reverse declaration order, so the float overloads come first. The same order
        // appears in SAM.API's ISteamUserStats013 and matches the slots Sync above already uses (0, 1, 2, 3, 5, 6, 9).
        internal const int SlotGetStatFloat = 0, SlotGetStatInt = 1, SlotSetStatFloat = 2, SlotSetStatInt = 3,
            SlotGetAchievement = 5, SlotSetAchievement = 6, SlotClearAchievement = 7, SlotStoreStats = 9;

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool ClearAchievement(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name);

        /// <summary>EResult of the last UserStatsStored_t (1102) for this app; 0 until one arrives.</summary>
        public int StatsStoredResult { get; private set; }

        internal GameStatsHelper.Response ApplyGameStats(GameStatsHelper.Request request, Func<bool> cancelled)
        {
            // Steam loads the user's stats for the app on connection and posts UserStatsReceived_t (1101).
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until && StatsReceivedResult == 0 && !cancelled()) { Pump(); Thread.Sleep(50); }
            if (cancelled()) return GameStatsHelper.Fail("cancelled", 0);
            if (StatsReceivedResult != 1 && !Probe(request)) return GameStatsHelper.Fail("stats-unavailable", StatsReceivedResult);

            var refused = new List<string>();
            int changed = 0;
            foreach (var a in request.Achievements ?? new Dictionary<string, bool>())
            {
                bool current;
                if (!Function<GetAchievement>(stats, SlotGetAchievement)(stats, a.Key, out current)) { refused.Add(a.Key); continue; }
                if (current == a.Value) continue;
                bool ok = a.Value ? Function<SetAchievement>(stats, SlotSetAchievement)(stats, a.Key)
                                  : Function<ClearAchievement>(stats, SlotClearAchievement)(stats, a.Key);
                if (ok) changed++; else refused.Add(a.Key);
            }
            foreach (var s in request.Stats ?? new Dictionary<string, GameStatsHelper.StatValue>())
            {
                if (s.Value == null || double.IsNaN(s.Value.Value) || double.IsInfinity(s.Value.Value)) { refused.Add(s.Key); continue; }
                bool ok;
                if (s.Value.Float)
                {
                    float current, next = (float)s.Value.Value;
                    if (!Function<GetFloat>(stats, SlotGetStatFloat)(stats, s.Key, out current)) { refused.Add(s.Key); continue; }
                    if (current == next) continue;
                    ok = Function<SetFloat>(stats, SlotSetStatFloat)(stats, s.Key, next);
                }
                else
                {
                    int current; double rounded = Math.Round(s.Value.Value);
                    if (rounded < int.MinValue || rounded > int.MaxValue) { refused.Add(s.Key); continue; }
                    int next = (int)rounded;
                    if (!Function<GetInt>(stats, SlotGetStatInt)(stats, s.Key, out current)) { refused.Add(s.Key); continue; }
                    if (current == next) continue;
                    ok = Function<SetInt>(stats, SlotSetStatInt)(stats, s.Key, next);
                }
                if (ok) changed++; else refused.Add(s.Key);
            }
            log("game stats " + appId + ": " + changed + " live changes, " + refused.Count + " refused");
            if (changed == 0) return refused.Count == 0 ? GameStatsHelper.Success(0) : GameStatsHelper.Fail("refused", 0, refused);

            StatsStoredResult = 0;
            if (!Function<SimpleBool>(stats, SlotStoreStats)(stats)) return GameStatsHelper.Fail("store-rejected", 0, refused);
            until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until && StatsStoredResult == 0 && !cancelled()) { Pump(); Thread.Sleep(50); }
            log("game stats " + appId + ": StoreStats result=" + StatsStoredResult);
            if (StatsStoredResult == 0) return GameStatsHelper.Fail(cancelled() ? "cancelled" : "store-timeout", 0, refused);
            if (StatsStoredResult != 1) return GameStatsHelper.Fail("store-failed", StatsStoredResult, refused);
            return refused.Count == 0 ? GameStatsHelper.Success(StatsStoredResult) : GameStatsHelper.Fail("refused", StatsStoredResult, refused);
        }

        // Stats are readable once Steam has loaded them, even if the 1101 callback was missed.
        bool Probe(GameStatsHelper.Request request)
        {
            foreach (var a in request.Achievements ?? new Dictionary<string, bool>())
            { bool v; return Function<GetAchievement>(stats, SlotGetAchievement)(stats, a.Key, out v); }
            foreach (var s in request.Stats ?? new Dictionary<string, GameStatsHelper.StatValue>())
            {
                if (s.Value != null && s.Value.Float) { float f; return Function<GetFloat>(stats, SlotGetStatFloat)(stats, s.Key, out f); }
                int i; return Function<GetInt>(stats, SlotGetStatInt)(stats, s.Key, out i);
            }
            return false;
        }
    }

    // --game-stats <appid> <parent pid> <parent start ticks> <stop event>
    // Reads one JSON request line from stdin and writes one JSON response line to stdout.
    static class GameStatsHelper
    {
        internal sealed class StatValue { public double Value { get; set; } public bool Float { get; set; } }
        internal sealed class Request
        {
            public Dictionary<string, bool> Achievements { get; set; } = new Dictionary<string, bool>();
            public Dictionary<string, StatValue> Stats { get; set; } = new Dictionary<string, StatValue>();
        }
        internal sealed class Response
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public int SteamResult { get; set; }
            public List<string> Refused { get; set; } = new List<string>();
        }

        internal static Response Success(int result) { return new Response { Ok = true, SteamResult = result }; }
        internal static Response Fail(string code, int result, List<string> refused = null)
        { return new Response { Ok = false, Error = code, SteamResult = result, Refused = refused ?? new List<string>() }; }

        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8 };
        const int MaxRequestChars = 1024 * 1024;

        internal static void Run(string[] args)
        {
            App.SetErrorMode(0x0001 | 0x0002 | 0x8000);
            Response response;
            try
            {
                uint parsed;
                if (!uint.TryParse(args[1], out parsed) || parsed == 0) throw new ArgumentException("Invalid app id");
                int parent = int.Parse(args[2]); long started = long.Parse(args[3]);
                string line;
                using (var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                {
                    var buffer = new char[MaxRequestChars + 1];
                    int read = 0, n;
                    while (read < buffer.Length && (n = input.Read(buffer, read, buffer.Length - read)) > 0) read += n;
                    if (read > MaxRequestChars) throw new InvalidDataException("Request too large");
                    line = new string(buffer, 0, read).Trim();
                }
                var request = JsonSerializer.Deserialize<Request>(line, Json);
                if (request == null) throw new InvalidDataException("Empty request");
                using (var stop = EventWaitHandle.OpenExisting(args[4]))
                    response = Apply(args[1], request, delegate { return !App.ParentAlive(parent, started) || stop.WaitOne(0); });
            }
            catch (Exception ex) { App.Log("game stats failed: " + ex.Message); response = Fail("exception: " + ex.Message, 0); }
            using (var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
            {
                output.WriteLine(JsonSerializer.Serialize(response, Json));
                output.Flush();
            }
            Environment.ExitCode = response.Ok ? 0 : 1;
        }

        static Response Apply(string id, Request request, Func<bool> cancelled)
        {
            bool created;
            // Same per-app mutex as the Cabinet shadow and schema helpers: one Steam IPC writer per game.
            using (var mutex = new Mutex(true, "Local\\Cabinet-shadow-app-" + id, out created))
            {
                if (!created) return Fail("busy", 0);
                if (cancelled()) return Fail("cancelled", 0);
                if (!Playtime.SteamRunning()) return Fail("steam-closed", 0);
                using (var session = new SteamSession(id, App.Log))
                    return session.ApplyGameStats(request, cancelled);
            }
        }
    }
}
