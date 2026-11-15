using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuaToolsGui.Services.GameTools;

/// <summary>
/// Pure edits of the two CloudRedirect files involved when a CloudRedirect-managed game's achievements
/// change while Steam is closed:
/// <list type="bullet">
/// <item><c>&lt;steam&gt;\cloud_redirect\stats\&lt;account&gt;\&lt;app&gt;.json</c>: CloudRedirect's own copy
/// (<c>stats[{id,value}]</c>, <c>achievements[{stat_id,bits,unlock_times[32]}]</c>).</item>
/// <item><c>&lt;steam&gt;\cloud_redirect\pending_edits.json</c>:
/// <c>{ "&lt;account&gt;": { "&lt;app&gt;": [ {"name","stat","bit"} ] } }</c>. CloudRedirect's
/// ApplyPendingEdits uploads every listed app on its next start and makes each listed (locked) achievement
/// stick by replacing, not OR-merging, that app's cloud entry. It consumes only the current account's entry.</item>
/// </list>
/// </summary>
internal static class CloudRedirectStatsFiles
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>Apply the edit to CloudRedirect's per-app stats JSON. Throws on a malformed file so the
    /// caller writes nothing rather than replacing CloudRedirect's data with a guess.</summary>
    internal static string ApplyEdit(string json, GameStatsSchema schema, GameStatsEdit edit, DateTimeOffset now)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("CloudRedirect stats file is not a JSON object.");
        var blocks = root["achievements"] as JsonArray ?? (JsonArray)(root["achievements"] = new JsonArray());
        var statValues = root["stats"] as JsonArray ?? (JsonArray)(root["stats"] = new JsonArray());
        long stamp = Math.Clamp(now.ToUnixTimeSeconds(), 1, uint.MaxValue);
        var byName = schema.Achievements.GroupBy(a => a.ApiName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (name, unlock) in edit.Achievements)
        {
            if (!byName.TryGetValue(name, out var a)) continue;
            uint mask = 1u << a.Bit;
            var block = blocks.OfType<JsonObject>().FirstOrDefault(b => Long(b["stat_id"]) == a.StatId);
            if (block is null && unlock)
            {
                block = new JsonObject { ["stat_id"] = a.StatId, ["bits"] = 0L, ["unlock_times"] = new JsonArray() };
                blocks.Add(block);
            }
            if (block is not null)
            {
                var times = block["unlock_times"] as JsonArray ?? (JsonArray)(block["unlock_times"] = new JsonArray());
                while (times.Count < 32) times.Add(0L);
                uint bits = (uint)Long(block["bits"]);
                if (unlock)
                {
                    block["bits"] = (long)(bits | mask);
                    if (Long(times[a.Bit]) == 0) times[a.Bit] = stamp;
                }
                else
                {
                    block["bits"] = (long)(bits & ~mask);
                    times[a.Bit] = 0L;
                }
            }
            // The achievement stat's value mirrors the bitfield; CloudRedirect ORs it back into the block on
            // merge, so it has to change with the bit.
            foreach (var stat in statValues.OfType<JsonObject>().Where(s => Long(s["id"]) == a.StatId))
            {
                uint value = (uint)Long(stat["value"]);
                stat["value"] = (long)(unlock ? value | mask : value & ~mask);
            }
        }

        var stats = schema.Stats.GroupBy(s => s.ApiName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (name, value) in edit.Stats)
        {
            if (!stats.TryGetValue(name, out var s) || !double.IsFinite(value)) continue;
            // CloudRedirect stores Steam's raw 32-bit stat value: an int32, or a float's bit pattern.
            long raw = s.IsFloat
                ? BitConverter.SingleToUInt32Bits((float)value)
                : unchecked((uint)(int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue));
            var entry = statValues.OfType<JsonObject>().FirstOrDefault(e => Long(e["id"]) == s.StatId);
            if (entry is null) statValues.Add(new JsonObject { ["id"] = s.StatId, ["value"] = raw });
            else entry["value"] = raw;
        }
        return root.ToJsonString(Compact);
    }

    /// <summary>
    /// Merge this edit into pending_edits.json. Only <paramref name="account"/>'s entry for
    /// <paramref name="appId"/> changes; other accounts and apps are kept as found. A re-unlock removes an
    /// earlier pending lock of the same achievement. The app is listed even with no locks, so CloudRedirect
    /// still uploads the offline unlocks on its next start. A malformed existing file is replaced
    /// (CloudRedirect would delete it unread).
    /// </summary>
    internal static string MergePendingEdits(string? existing, uint account, long appId, GameStatsSchema schema, GameStatsEdit edit)
    {
        JsonObject root;
        try { root = existing is null ? new JsonObject() : JsonNode.Parse(existing) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { root = new JsonObject(); }

        string accountKey = account.ToString(CultureInfo.InvariantCulture), appKey = appId.ToString(CultureInfo.InvariantCulture);
        var apps = root[accountKey] as JsonObject ?? (JsonObject)(root[accountKey] = new JsonObject());
        var locks = apps[appKey] as JsonArray ?? (JsonArray)(apps[appKey] = new JsonArray());
        var byName = schema.Achievements.GroupBy(a => a.ApiName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (name, unlock) in edit.Achievements)
        {
            if (!byName.TryGetValue(name, out var a)) continue;
            for (int i = locks.Count - 1; i >= 0; i--)
                if (locks[i] is JsonObject o && o["name"] is JsonValue v && v.TryGetValue(out string? n) && n == name) locks.RemoveAt(i);
            if (!unlock) locks.Add(new JsonObject { ["name"] = name, ["stat"] = a.StatId, ["bit"] = a.Bit });
        }
        return root.ToJsonString(Compact);
    }

    private static long Long(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out uint u)) return u;
        if (v.TryGetValue(out double d) && double.IsFinite(d)) return (long)d;
        return 0;
    }
}
