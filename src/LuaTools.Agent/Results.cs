using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace LuaTools.Agent;

public static class Results
{
    public static Envelope Project(Envelope result, string? fields)
    {
        if (!result.Ok || fields is null) return result;
        var names = fields.Split(',');
        JsonNode? Select(JsonNode? value)
        {
            var output = new JsonObject();
            foreach (string field in names)
            {
                JsonNode? source = value; JsonObject target = output; string[] parts = field.Split('.');
                for (int i = 0; i < parts.Length; i++)
                {
                    source = source is JsonObject obj ? obj[parts[i]] : null;
                    if (i == parts.Length - 1) target[parts[i]] = source?.DeepClone();
                    else { target[parts[i]] ??= new JsonObject(); target = target[parts[i]]!.AsObject(); }
                }
            }
            return output;
        }
        return result with { Data = result.Data is JsonArray array ? new JsonArray(array.Select(Select).ToArray()) : Select(result.Data) };
    }
}

/// <summary>Bounded host-owned snapshots. Opaque cursors never contain user state.</summary>
public sealed class SnapshotPager
{
    private sealed record Snapshot(string Key, JsonArray Data, DateTimeOffset Expires);
    private readonly Dictionary<string, Snapshot> _snapshots = [];
    private readonly object _gate = new();
    public Envelope List(string key, JsonArray data, Dictionary<string, string?> flags)
    {
        lock (_gate)
        {
            foreach (string expired in _snapshots.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) _snapshots.Remove(expired);
            string id; int offset = 0;
            if (flags.TryGetValue("cursor", out string? cursor))
            {
                string[] parts = (cursor ?? "").Split('.');
                if (parts.Length != 2 || !int.TryParse(parts[1], out offset) || offset < 0 || !_snapshots.TryGetValue(parts[0], out var snapshot) || snapshot.Key != key || offset > snapshot.Data.Count)
                    return Envelope.Fail("CURSOR_EXPIRED", 3, "Snapshot cursor expired or query changed. List again.");
                id = parts[0]; data = snapshot.Data;
            }
            else
            {
                id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                if (_snapshots.Count >= 32) _snapshots.Remove(_snapshots.Keys.First());
                if (Encoding.UTF8.GetByteCount(data.ToJsonString()) > 4 * Protocol.MaxFrame) return Envelope.Fail("RESULT_TOO_LARGE", 7, "Narrow the query.");
                _snapshots[id] = new(key, (JsonArray)data.DeepClone(), DateTimeOffset.UtcNow.AddMinutes(5));
            }
            int limit = int.TryParse(flags.GetValueOrDefault("limit"), out int n) ? n : 50;
            if (limit < 1 || limit > 200) return Envelope.Fail("INVALID_USAGE", 2, "Limit must be 1–200.");
            if (flags.ContainsKey("count")) return Envelope.Success(new { total = data.Count }, new(limit, null, false, data.Count));
            int take = flags.ContainsKey("all") ? data.Count : limit;
            var page = new JsonArray(data.Skip(offset).Take(take).Select(x => x?.DeepClone()).ToArray());
            if (Encoding.UTF8.GetByteCount(page.ToJsonString()) > Protocol.MaxFrame - 8192) return Envelope.Fail("RESULT_TOO_LARGE", 7, "Use a smaller --limit or narrower query.");
            int next = offset + page.Count;
            return Envelope.Success(page, new(limit, next < data.Count ? id + "." + next : null, next < data.Count, data.Count));
        }
    }
}
