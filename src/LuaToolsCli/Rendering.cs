using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;

namespace LuaToolsCli;

public static class Rendering
{
    public static bool UseColor(bool tty, Parsed parsed, string? noColor) => tty && !parsed.Has("no-color") && noColor is null && parsed.Format(tty) is "table" or "text";
    public static string Render(Envelope result, string format)
    {
        if (format is "json" or "jsonl" || !result.Ok) return JsonSerializer.Serialize(result, Protocol.Json);
        var rows = result.Data is JsonArray array ? array.ToArray() : new[] { result.Data };
        if (format == "csv")
        {
            string Escape(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            string[] keys = rows.OfType<JsonObject>().SelectMany(o => o.Select(p => p.Key)).Distinct().ToArray();
            return string.Join(',', keys.Select(Escape)) + "\n" + string.Join('\n', rows.Select(row => string.Join(',', keys.Select(k => Escape(row is JsonObject obj ? obj[k]?.ToString() : row?.ToString())))));
        }
        return string.Join('\n', rows.Select(row => row is JsonObject obj ? string.Join("  |  ", obj.Select(p => p.Key + ": " + p.Value?.ToJsonString())) : row?.ToJsonString()));
    }
    public static string Help(Command? command = null, string? resource = null)
    {
        var text = new StringBuilder("LuaTools agent CLI\n\nStart here:\n  luatools ping --json\n  luatools doctor --json\n  luatools commands --json\n  luatools describe setting\n\n");
        foreach (var c in command is null ? Catalogue.Commands.Where(c => resource is null || c.Resource == resource) : [command])
        {
            text.AppendLine(c.Syntax + "  [" + c.Status + "]").AppendLine("  " + c.Purpose);
            if (command is not null || resource is not null)
            {
                foreach (var flag in c.Flags) text.AppendLine($"  --{flag.Name} ({flag.Type}; default {flag.Default}; allowed: {string.Join(", ", flag.AllowedValues)})");
                foreach (string example in c.Examples) text.AppendLine("  " + example);
            }
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
