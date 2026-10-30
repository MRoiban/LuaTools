using System.Text.Json;
using System.Text.Json.Nodes;
namespace LuaTools.Agent;

/// <summary>Shared, bounded selection grammar. Resources supply only public summaries.</summary>
public static class BulkSelection
{
    public static JsonObject Data(AgentRequest request, params string[] allowed)
    {
        var value = request.Flags.GetValueOrDefault("data");
        var data = value is null ? new JsonObject() : JsonNode.Parse(value) as JsonObject ?? throw new JsonException();
        if (data.Any(p => !allowed.Contains(p.Key))) throw new JsonException();
        return data;
    }
    public static string[] Select(AgentRequest request, JsonArray rows, string identity, string[] filterFields, string? dataIdentity = null)
    {
        string[] targets = request.Flags.GetValueOrDefault("ids")?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            ?? (request.Arguments.Length > 0 ? request.Arguments : dataIdentity is not null ? [dataIdentity] : []);
        if (request.Flags.GetValueOrDefault("where") is { } where)
        {
            if (targets.Length != 0) throw new ArgumentException("Conflicting selection.");
            var pair = where.Split('=', 2);
            if (pair.Length != 2 || !filterFields.Contains(pair[0])) throw new ArgumentException("Invalid filter.");
            targets = rows.Where(n => string.Equals(n?[pair[0]]?.ToString(), pair[1], StringComparison.OrdinalIgnoreCase)).Select(n => n![identity]!.ToString()).ToArray();
        }
        targets = targets.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (targets.Length is 0 or > 200) throw new ArgumentException("Selection must contain 1–200 items.");
        return targets;
    }
}
