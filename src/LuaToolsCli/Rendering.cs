using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;

namespace LuaToolsCli;

public static class Rendering
{
    private static readonly JsonSerializerOptions DisplayJson = new(Protocol.Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static bool UseColor(bool tty, Parsed parsed, string? noColor) => tty && !parsed.Has("no-color") && noColor is null && parsed.Format(tty) is "table" or "text";
    public static string Render(Envelope result, string format)
    {
        if (format is "json" or "jsonl" || !result.Ok) return JsonSerializer.Serialize(result, DisplayJson);
        var rows = result.Data is JsonArray array ? array.ToArray() : new[] { result.Data };
        if (format == "csv")
        {
            string Escape(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            string[] keys = rows.OfType<JsonObject>().SelectMany(o => o.Select(p => p.Key)).Distinct().ToArray();
            return string.Join(',', keys.Select(Escape)) + "\n" + string.Join('\n', rows.Select(row => string.Join(',', keys.Select(k => Escape(row is JsonObject obj ? obj[k]?.ToString() : row?.ToString())))));
        }
        return string.Join('\n', rows.Select(row => row is JsonObject obj ? string.Join("  |  ", obj.Select(p => p.Key + ": " + p.Value?.ToJsonString())) : row?.ToJsonString()));
    }
    public static readonly HashSet<string> GlobalFlags = Catalogue.Find("ping")!.Flags.Select(f => f.Name).Concat(new[] { "dry-run", "yes", "plan-id", "idempotency-key", "if-version", "actor-note", "restart-steam", "drain", "data", "ids", "ids-from", "where" }).ToHashSet();
    public static string Help(Command? command = null, string? resource = null)
    {
        var text = new StringBuilder("LuaTools agent CLI — operate the running LuaTools application.\n\n");
        if (command is null && resource is null)
        {
            text.AppendLine("Resources:");
            foreach (var group in Catalogue.Commands.GroupBy(c => c.Resource))
                text.AppendLine("  " + group.Key.PadRight(18) + ResourceSummaries.All[group.Key]);
            text.AppendLine("\nGlobal flags:");
            foreach (var flag in Catalogue.Find("ping")!.Flags) text.AppendLine("  --" + flag.Name + " (" + flag.Type + "; default " + flag.Default + ")");
            text.AppendLine("  Mutations: --dry-run --yes --plan-id --idempotency-key --if-version --actor-note --restart-steam --drain --data --ids --ids-from --where");
            text.AppendLine("\nStart here:\n  luatools doctor --json\n  luatools commands --json\n  luatools describe game --json\n  luatools guide");
        }
        else foreach (var c in command is null ? Catalogue.Commands.Where(c => c.Resource == resource) : [command])
        {
            text.AppendLine(c.Key).AppendLine(c.Purpose).AppendLine("Usage: " + c.Syntax).AppendLine("Flags:");
            text.AppendLine("Global flags: see luatools --help.");
            foreach (var flag in c.Flags.Where(f => !GlobalFlags.Contains(f.Name))) text.AppendLine($"  --{flag.Name}: {flag.Type}; default {flag.Default}; allowed {(flag.AllowedValues.Length == 0 ? "any value of this type" : string.Join(", ", flag.AllowedValues))}; required {flag.Required.ToString().ToLowerInvariant()}");
            text.AppendLine("Side effects: " + string.Join("; ", c.SideEffects));
            text.AppendLine("Reversibility: " + (c.Reversible is null ? "Not applicable for reads; mutation effects may be irreversible." : c.Reversible.Value ? "Reversible through the corresponding GUI operation." : "Irreversible."));
            text.AppendLine("Preconditions: " + string.Join("; ", c.Preconditions));
            text.AppendLine("Status: " + c.Status + "; job: " + c.JobBehavior);
            text.AppendLine("Examples:");
            foreach (string example in c.Examples) text.AppendLine("  " + example);
            text.AppendLine("See also: " + string.Join(", ", c.SeeAlso)).AppendLine();
        }
        text.AppendLine("\nExit codes: 0 success; 1 failure; 2 usage; 3 not found; 4 login; 5 permission/disabled; 6 conflict; 7 unavailable/timeout; 8 consent; 9 GUI not running; 130 interrupted.");
        return text.ToString();
    }
    public static string Completion(string shell)
    {
        string words = string.Join(' ', Catalogue.Commands.Select(c => c.Resource).Distinct());
        return shell switch
        {
            "bash" => $"complete -W '{words}' luatools",
            "zsh" => $"#compdef luatools\n_arguments '1:resource:({words})'",
            "fish" => string.Join('\n', words.Split(' ').Select(w => $"complete -c luatools -f -a '{w}'")),
            _ => $"Register-ArgumentCompleter -Native -CommandName luatools -ScriptBlock {{ param($wordToComplete) '{words}'.Split(' ') | Where-Object {{ $_ -like \"$wordToComplete*\" }} }}",
        };
    }
}
