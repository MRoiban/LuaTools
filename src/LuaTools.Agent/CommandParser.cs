using System.Globalization;

namespace LuaTools.Agent;

public sealed record Parsed(Command? Command, string[] Arguments, Dictionary<string, string?> Flags, Problem[] Problems)
{
    public byte[][] FileBytes { get; init; } = [];
    public SubmittedFile[] Files { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string? Secret { get; init; }
    public bool Has(string flag) => Flags.ContainsKey(flag);
    public string? Value(string flag) => Flags.GetValueOrDefault(flag);
    public TimeSpan Timeout => CommandParser.Duration(Value("timeout") ?? (Command?.Key == "job wait" ? "5m" : "30s")) ?? TimeSpan.FromSeconds(30);
    public string Format(bool tty) => Has("json") ? "json" : Value("format") ?? (tty ? "table" : "json");
}
public static class CommandParser
{
    public static TimeSpan? Duration(string value)
    {
        double scale = value.EndsWith("ms", StringComparison.Ordinal) ? .001 : value.EndsWith('s') ? 1 : value.EndsWith('m') ? 60 : value.EndsWith('h') ? 3600 : 0;
        string number = value.TrimEnd('m', 's', 'h');
        return scale > 0 && double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n) && n > 0 && n * scale <= 86400 ? TimeSpan.FromSeconds(n * scale) : null;
    }
    public static Parsed Parse(string[] args)
    {
        List<Problem> problems = []; List<string> operands = []; Dictionary<string, string?> flags = new(StringComparer.Ordinal);
        var allFlags = Catalogue.Commands.SelectMany(c => c.Flags).GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
        bool end = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--") { end = true; continue; }
            if (end || !arg.StartsWith('-')) { operands.Add(arg); continue; }
            if (arg is "--help" or "-h") { flags["help"] = null; continue; }
            string[] pair = arg.TrimStart('-').Split('=', 2); string name = pair[0];
            if (!allFlags.TryGetValue(name, out var flag)) { problems.Add(new(name, "Unknown flag.")); continue; }
            string? value = pair.Length == 2 ? pair[1] : null;
            if (flag.Type != "bool" && value is null)
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) value = args[++i];
                else problems.Add(new(name, "A value is required."));
            }
            if (!flags.TryAdd(name, value))
            {
                if (name is "file" or "owned" && value is not null) flags[name] += "\n" + value;
                else if (name == "depot" && value is not null) flags[name] += "," + value;
                else problems.Add(new(name, "Flag repeated."));
            }
        }
        if (operands.Count == 0) operands.Add("help");
        var command = Catalogue.Find(string.Join(' ', operands.Take(2))) ?? Catalogue.Find(operands[0]);
        if (command is null && flags.ContainsKey("help") && operands.Count == 1 && Catalogue.Commands.Any(c => c.Resource == operands[0]))
        {
            command = Catalogue.Find("help"); operands.Insert(0, "help"); flags.Remove("help");
        }
        int consumed = command?.Verb.Length > 0 ? 2 : 1;
        string[] identities = operands.Skip(consumed).ToArray();
        if (command is null) problems.Add(new("command", "Unknown command. Closest matches: " + string.Join(", ", Catalogue.Suggest(string.Join(' ', operands.Take(2))))));
        else
        {
            if (command.Key == "help" && identities.Length > 0 && !Catalogue.Commands.Any(c => c.Resource == identities[0])) problems.Add(new("resource", "Unknown resource. Closest matches: " + string.Join(", ", Catalogue.Suggest(identities[0]))));
            if (!flags.ContainsKey("help") && !(!command.ReadOnly && (flags.ContainsKey("ids") || flags.ContainsKey("ids-from") || flags.ContainsKey("where") || flags.ContainsKey("data"))) && identities.Length < command.Identities.Length) problems.Add(new("identity", "Required: " + string.Join(", ", command.Identities)));
            if (!flags.ContainsKey("help") && command.Key == "account login" && flags.ContainsKey("browser") == flags.ContainsKey("code-stdin")) problems.Add(new("login", "Select exactly one of --browser and --code-stdin."));
            int max = command.Key == "help" ? 2 : command.Key == "setting update" ? 1 : command.Identities.Length;
            if (identities.Length > max && !(command.Syntax.Contains(">...", StringComparison.Ordinal) && command.Identities.Length > 0)) problems.Add(new("identity", "Too many arguments."));
            if (command.IdentityValues.Length > 0 && identities.Length > 0 && !command.IdentityValues.Contains(identities[0])) problems.Add(new("identity", "Invalid value.", command.IdentityValues));
            if (!flags.ContainsKey("help"))
                foreach (var required in command.Flags.Where(f => f.Required))
                    if (!flags.ContainsKey(required.Name) && !flags.ContainsKey("data")) problems.Add(new(required.Name, "Required flag."));
            foreach (var (name, value) in flags)
            {
                if (name == "help") continue;
                var flag = command.Flags.FirstOrDefault(f => f.Name == name);
                if (flag is null) { problems.Add(new(name, "Flag is not valid for this command.")); continue; }
                if (flag.Type == "bool" && value is not null) problems.Add(new(name, "Switch does not take a value."));
                if (flag.AllowedValues.Length > 0 && value is not null && !flag.AllowedValues.Contains(value)) problems.Add(new(name, "Invalid value.", flag.AllowedValues));
                if (flag.Type == "integer" && (!int.TryParse(value, out int n) || n < 1 || n > 200)) problems.Add(new(name, "Expected integer 1–200."));
                if (flag.Type == "duration" && Duration(value ?? "") is null) problems.Add(new(name, "Expected a positive duration, e.g. 500ms, 30s, 5m (maximum 24h)."));
                if (name == "since" && (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) || time.Offset != TimeSpan.Zero || !(value?.EndsWith('Z') == true || value?.EndsWith("+00:00", StringComparison.Ordinal) == true))) problems.Add(new(name, "Expected RFC3339 UTC timestamp."));
            }
            if (flags.TryGetValue("fields", out var fields))
                foreach (var field in (fields ?? "").Split(','))
                    if (!command.OutputFields.Contains(field)) problems.Add(new("fields", "Unknown or private field: " + field, command.OutputFields));
        }
        if (flags.ContainsKey("json") && flags.TryGetValue("format", out var format) && format != "json") problems.Add(new("format", "--json conflicts with this format."));
        if (flags.ContainsKey("quiet") && flags.ContainsKey("verbose")) problems.Add(new("verbose", "Conflicts with --quiet."));
        if (flags.ContainsKey("keep-files") && flags.ContainsKey("delete-created")) problems.Add(new("delete-created", "Conflicts with --keep-files."));
        if (flags.ContainsKey("all") && flags.ContainsKey("cursor")) problems.Add(new("cursor", "Conflicts with --all."));
        return new(command, identities, flags, problems.ToArray());
    }
}
