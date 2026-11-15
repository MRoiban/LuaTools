using System.Globalization;
using System.IO;
using CabinetEngine;

namespace LuaToolsGui.Services.GameTools;

/// <summary>
/// Pure reads and edits of Steam's binary stats files, on bytes so tests never need a Steam folder:
/// the schema (appcache\stats\UserGameStatsSchema_&lt;app&gt;.bin) and the user's cache
/// (UserGameStats_&lt;account&gt;_&lt;app&gt;.bin). Uses Cabinet's binary KeyValues reader/writer, so unknown
/// keys (crc, PendingChanges, other stats) round-trip untouched.
/// </summary>
internal static class GameStatsFiles
{
    internal const string IconBase = "https://shared.akamai.steamstatic.com/community_assets/images/apps/";

    // ── Schema ──────────────────────────────────────────────────────

    /// <summary>Parse a schema. Null when it has no stats section. Display text uses
    /// <paramref name="language"/> (Steam's names: "english", "french", ...) with English as fallback.</summary>
    internal static GameStatsSchema? ReadSchema(byte[] bytes, long appId, string language)
    {
        var root = KV.Read(bytes);
        var game = root.Get(appId.ToString(CultureInfo.InvariantCulture))
            ?? root.Children?.FirstOrDefault(c => c.Get("stats") != null);
        var stats = game?.Get("stats");
        if (stats?.Children is null) return null;

        var achievements = new List<AchievementInfo>();
        var plain = new List<StatInfo>();
        foreach (var stat in stats.Children)
        {
            if (stat.Children is null || !int.TryParse(stat.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int statId)) continue;
            int type = StatType(stat);
            bool statProtected = (Int(stat.Get("permission")) & 3) != 0;
            if (type is 4 or 5 || stat.Get("bits") is not null)
            {
                foreach (var bits in stat.Children.Where(c => string.Equals(c.Key, "bits", StringComparison.OrdinalIgnoreCase) && c.Children is not null))
                foreach (var entry in bits.Children!)
                {
                    var bitKv = entry.Get("bit");
                    if (!int.TryParse(bitKv is null ? entry.Key : Str(bitKv), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bit)
                        || bit < 0 || bit > 31) continue;
                    string apiName = Str(entry.Get("name"));
                    if (apiName.Length == 0) continue;
                    var display = entry.Get("display");
                    string icon = Str(display?.Get("icon")), gray = Str(display?.Get("icon_gray"));
                    achievements.Add(new AchievementInfo(
                        apiName, statId, bit,
                        Localized(display?.Get("name"), language, apiName),
                        Localized(display?.Get("desc"), language, ""),
                        Int(display?.Get("hidden")) != 0,
                        statProtected || (Int(entry.Get("permission")) & 3) != 0,
                        IconUrl(appId, icon), IconUrl(appId, gray),
                        Progress: ReadProgress(entry.Get("progress"))));
                }
            }
            else if (type is 1 or 2 or 3)
            {
                string apiName = Str(stat.Get("name"));
                if (apiName.Length == 0) continue;
                plain.Add(new StatInfo(statId, apiName,
                    Localized(stat.Get("display")?.Get("name"), language, apiName),
                    IsFloat: type != 1, Value: Num(stat.Get("default")),
                    Protected: statProtected || type == 3,
                    IncrementOnly: Int(stat.Get("incrementonly")) != 0));
            }
        }
        return new GameStatsSchema(achievements, plain);
    }

    /// <summary>Icon file names come from the schema; only a bare file name is accepted, so a hostile
    /// schema can't point the icon download anywhere but the app's own CDN folder.</summary>
    private static string? IconUrl(long appId, string file) =>
        file.Length > 0 && file.Length <= 200 && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && !file.StartsWith('.')
            ? IconBase + appId.ToString(CultureInfo.InvariantCulture) + "/" + file : null;

    private static AchievementProgress? ReadProgress(KV? progress)
    {
        var value = progress?.Get("value");
        if (value is null || !Str(value.Get("operation")).Equals("statvalue", StringComparison.OrdinalIgnoreCase)) return null;
        string stat = Str(value.Get("operand1"));
        double min = Num(progress!.Get("min_val")), max = Num(progress.Get("max_val"));
        return stat.Length > 0 && max > min ? new AchievementProgress(stat, min, max, min) : null;
    }

    // Steam writes the type as a number or a name; "type_int" wins when both exist.
    private static int StatType(KV stat)
    {
        var typeInt = stat.Get("type_int");
        if (typeInt is not null) return Int(typeInt);
        string s = Str(stat.Get("type"));
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return n;
        return s.ToUpperInvariant() switch
        {
            "INT" or "INTEGER" => 1,
            "FLOAT" => 2,
            "AVGRATE" or "AVERAGERATE" => 3,
            "ACHIEVEMENTS" => 4,
            "GROUPACHIEVEMENTS" => 5,
            _ => 0,
        };
    }

    // ── User stats cache ────────────────────────────────────────────

    /// <summary>Merge the user's cache (null when the game was never played) into the schema.</summary>
    internal static (List<AchievementInfo> Achievements, List<StatInfo> Stats) ReadState(GameStatsSchema schema, byte[]? userBytes)
    {
        var cache = userBytes is null ? null : Cache(KV.Read(userBytes));
        var values = new Dictionary<int, double>();
        var bits = new Dictionary<int, uint>();
        var times = new Dictionary<(int, int), long>();
        foreach (var stat in cache?.Children ?? [])
        {
            if (stat.Children is null || !int.TryParse(stat.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int id)) continue;
            var data = stat.Get("data");
            if (data is not null)
            {
                values[id] = Num(data);
                bits[id] = data.Type == 3 ? 0u : unchecked((uint)Int(data));
            }
            foreach (var t in stat.Get("AchievementTimes")?.Children ?? [])
                if (int.TryParse(t.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int bit)) times[(id, bit)] = unchecked((uint)Int(t));
        }

        var stats = schema.Stats.Select(s => values.TryGetValue(s.StatId, out double v) ? s with { Value = v } : s).ToList();
        var byName = stats.GroupBy(s => s.ApiName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        var achievements = schema.Achievements.Select(a =>
        {
            bool unlocked = ((bits.GetValueOrDefault(a.StatId) >> a.Bit) & 1) == 1;
            DateTimeOffset? at = unlocked && times.TryGetValue((a.StatId, a.Bit), out long unix) && unix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
            var progress = a.Progress is { } p && byName.TryGetValue(p.StatName, out double pv) ? p with { Value = pv } : a.Progress;
            return a with { Unlocked = unlocked, UnlockedAt = at, Progress = progress };
        }).ToList();
        return (achievements, stats);
    }

    /// <summary>
    /// Apply an edit to the user's cache and return the new file bytes, or null when nothing changes.
    /// Unlock sets the bit and records the time unless one exists; lock clears the bit (the achievement
    /// stat's value IS the bitfield) and removes its AchievementTimes entry. Names not in the schema are
    /// ignored. Every other key is preserved byte for byte in meaning.
    /// </summary>
    internal static byte[]? ApplyEdit(byte[]? userBytes, GameStatsSchema schema, GameStatsEdit edit, DateTimeOffset now)
    {
        var root = userBytes is null ? new KV(0, "", new List<KV>()) : KV.Read(userBytes);
        var cache = Cache(root);
        if (cache is null)
        {
            cache = new KV(0, "cache", new List<KV>());
            cache.SetInt("crc", 0);
            cache.SetInt("PendingChanges", 0);
            root.Children!.Add(cache);
        }

        var achievements = schema.Achievements.GroupBy(a => a.ApiName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var stats = schema.Stats.GroupBy(s => s.ApiName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        int stamp = (int)Math.Clamp(now.ToUnixTimeSeconds(), 1, int.MaxValue);
        bool changed = false;

        foreach (var (name, unlock) in edit.Achievements)
        {
            if (!achievements.TryGetValue(name, out var a)) continue;
            var stat = cache.GetOrAddSub(a.StatId.ToString(CultureInfo.InvariantCulture));
            var data = stat.Get("data");
            uint current = data is null || data.Type == 3 ? 0u : unchecked((uint)Int(data));
            uint mask = 1u << a.Bit;
            uint next = unlock ? current | mask : current & ~mask;
            if (next != current || data is null || data.Type != 2) { stat.SetInt("data", unchecked((int)next)); changed |= next != current; }
            // Steam marks bits it has not uploaded yet in "pendingbits"; a locked bit must not stay pending.
            if (!unlock && stat.Get("pendingbits") is { Type: 2 } pendingBits && (unchecked((uint)Int(pendingBits)) & mask) != 0)
            { stat.SetInt("pendingbits", unchecked((int)(unchecked((uint)Int(pendingBits)) & ~mask))); changed = true; }
            string bitKey = a.Bit.ToString(CultureInfo.InvariantCulture);
            var times = stat.Get("AchievementTimes");
            if (unlock)
            {
                times ??= stat.GetOrAddSub("AchievementTimes");
                var t = times.Get(bitKey);
                if (t is null || Int(t) == 0) { times.SetInt(bitKey, stamp); changed = true; }
            }
            else if (times?.Children is { } list && list.RemoveAll(t => t.Key == bitKey) > 0) changed = true;
        }

        foreach (var (name, value) in edit.Stats)
        {
            if (!stats.TryGetValue(name, out var s) || double.IsNaN(value) || double.IsInfinity(value)) continue;
            var stat = cache.GetOrAddSub(s.StatId.ToString(CultureInfo.InvariantCulture));
            var data = stat.Get("data");
            if (s.IsFloat)
            {
                float next = (float)value;
                if (data is { Type: 3 } && (float)data.Value == next) continue;
                stat.SetFloat("data", next);
            }
            else
            {
                int next = (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
                if (data is { Type: 2 } && (int)data.Value == next) continue;
                stat.SetInt("data", next);
            }
            changed = true;
        }
        return changed ? root.Write() : null;
    }

    /// <summary>True when the snapshot already shows every requested value (used to confirm a live write
    /// Steam reported as failed but may still have recorded).</summary>
    internal static bool IsApplied(GameStatsSnapshot snapshot, GameStatsEdit edit)
    {
        if (!snapshot.HasSchema) return false;
        foreach (var (name, unlocked) in edit.Achievements)
            if (snapshot.Achievements.FirstOrDefault(a => a.ApiName == name) is not { } a || a.Unlocked != unlocked) return false;
        foreach (var (name, value) in edit.Stats)
        {
            if (snapshot.Stats.FirstOrDefault(s => s.ApiName == name) is not { } s) return false;
            bool same = s.IsFloat ? (float)s.Value == (float)value : s.Value == Math.Round(value);
            if (!same) return false;
        }
        return true;
    }

    private static KV? Cache(KV root) => root.Get("cache") ?? root.Children?.FirstOrDefault(c => c.Children is not null);

    // ── KV value helpers ────────────────────────────────────────────

    internal static string Str(KV? kv) => kv?.Str() ?? "";

    internal static int Int(KV? kv) => kv?.Value switch
    {
        int i => i,
        uint u => unchecked((int)u),
        long l => unchecked((int)l),
        ulong ul => unchecked((int)ul),
        float f => float.IsFinite(f) ? (int)Math.Clamp(f, int.MinValue, int.MaxValue) : 0,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) => r,
        string s when uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint r) => unchecked((int)r),
        _ => 0,
    };

    internal static double Num(KV? kv) => kv?.Value switch
    {
        float f => float.IsFinite(f) ? f : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) => d,
        _ => Int(kv),
    };

    private static string Localized(KV? node, string language, string fallback)
    {
        if (node is null) return fallback;
        if (node.Children is null) return Str(node) is { Length: > 0 } plain ? plain : fallback;
        string text = Str(node.Get(language));
        if (text.Length == 0) text = Str(node.Get("english"));
        return text.Length > 0 ? text : fallback;
    }

    /// <summary>Read a Steam file that Steam may hold open.</summary>
    internal static byte[]? ReadShared(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }
}
