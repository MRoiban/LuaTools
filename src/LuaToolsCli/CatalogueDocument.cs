using System.Text.Json.Nodes;
using LuaTools.Agent;
namespace LuaToolsCli;

/// <summary>Lossless catalogue: common flags and repeated metadata are defined once per document.</summary>
public static class CatalogueDocument
{
    public static object Create(IEnumerable<Command> source, bool brief = false)
    {
        var commands = source.ToArray();
        if (brief) return new { commands = commands.Select(c => new { command = c.Key, purpose = c.Purpose, syntax = c.Syntax }) };
        var globals = commands.SelectMany(c => c.Flags).Where(f => Rendering.GlobalFlags.Contains(f.Name))
            .GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
        var entries = commands.Select(c =>
        {
            var node = Protocol.Node(c)!.AsObject();
            node["variants"] = Protocol.Node(c.Variants.Where(v => v != c.Syntax).ToArray()); // do not repeat syntax
            node.Remove("see_also"); // discovery links are supplied once in the document
            node.Remove("input_schema"); // identities and flags already supply the complete input schema
            node.Remove("output_schema"); // output_fields plus the document's envelope/paging definition
            node.Remove("secret_channels"); // derived from secret-stdin/code-stdin flags
            node.Remove("resource"); node.Remove("verb"); // key carries resource and verb
            node["global_flags"] = new JsonArray(c.Flags.Where(f => globals.ContainsKey(f.Name)).Select(f => JsonValue.Create(f.Name) as JsonNode).ToArray());
            node["flags"] = Protocol.Node(c.Flags.Where(f => !globals.ContainsKey(f.Name)).ToArray());
            node["flags"] = Protocol.Node(c.Flags.Where(f => !globals.ContainsKey(f.Name)).Select(f => new object?[] { f.Name, f.Type, f.Default, f.AllowedValues, f.Required }).ToArray());
            return node;
        }).ToArray();
        var defaults = new JsonObject();
        foreach (string key in entries.First().Select(p => p.Key).ToArray())
        {
            var groups = entries.GroupBy(e => e[key]?.ToJsonString() ?? "null").OrderByDescending(g => g.Count()).ToArray();
            if (groups[0].Count() < 2) continue;
            defaults[key] = groups[0].First()[key]?.DeepClone();
            foreach (var entry in groups[0]) entry.Remove(key);
        }
        string[] contractColumns = ["capability_ids", "status", "disabled_code", "aliases", "read_only", "dry_run", "job_behavior", "destructive", "reversible", "side_effects", "identities", "identity_values", "exit_codes", "preconditions", "default_sort", "variants", "global_flags", "examples", "resource", "verb", "flags"];
        var propertyNames = contractColumns.Select((name, index) => (name, shortName: index.ToString())).ToDictionary(p => p.shortName, p => p.name);
        void Shorten(JsonObject obj)
        {
            foreach (var pair in propertyNames)
                if (obj.Remove(pair.Value, out var value)) obj[pair.Key] = value;
        }
        foreach (var entry in entries) Shorten(entry);
        Shorten(defaults);
        var definitions = new JsonObject();
        var occurrences = new Dictionary<string, int>();
        void Count(JsonNode? value)
        {
            if (value is null) return;
            string json = value.ToJsonString();
            if (json.Length > 40) occurrences[json] = occurrences.GetValueOrDefault(json) + 1;
            if (value is JsonObject obj) foreach (var pair in obj) Count(pair.Value);
            if (value is JsonArray array) foreach (var item in array) Count(item);
        }
        foreach (var entry in entries) Count(entry);
        var refs = occurrences.Where(p => p.Value > 1).ToDictionary(p => p.Key, p => "d" + definitions.Count.ToString());
        int index = 0;
        foreach (string json in refs.Keys.ToArray()) refs[json] = "d" + index++;
        JsonNode? Pack(JsonNode? value, bool root = false)
        {
            if (value is null) return null;
            if (!root && refs.TryGetValue(value.ToJsonString(), out string? name)) return new JsonObject { ["$ref"] = name };
            if (value is JsonObject obj) return new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Pack(p.Value))));
            if (value is JsonArray array) return new JsonArray(array.Select(v => Pack(v)).ToArray());
            return value.DeepClone();
        }
        foreach (var pair in refs) definitions[pair.Value] = Pack(JsonNode.Parse(pair.Key), true);
        return new { version = Protocol.Version, definitions, property_names = propertyNames, schema = "Expand numeric keys using property_names; resolve $ref via definitions; merge command_defaults, overridden by each command. Global flags reference global_flag_definitions. flags use flag_columns. Input: identities/identity_values/flags. Output: output_fields, v1 envelope; lists: snapshot 5m, default 50/max 200. Full examples and related commands: luatools <key> --help.", flag_columns = new[] { "name", "type", "default", "allowed_values", "required" }, global_flag_columns = new[] { "type", "default", "allowed_values", "required" }, global_flag_definitions = globals.ToDictionary(p => p.Key, p => new object?[] { p.Value.Type, p.Value.Default, p.Value.AllowedValues, p.Value.Required }), command_defaults = defaults, commands = entries.Select(e => Pack(e)) };
    }
}
