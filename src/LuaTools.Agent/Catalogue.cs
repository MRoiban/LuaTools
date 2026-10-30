using System.Text.Json;

namespace LuaTools.Agent;

public sealed record Flag(string Name, string Type, JsonElement Default, string[] AllowedValues, bool Required = false);
public sealed record Command(string Resource, string Verb, string Syntax, string[] Variants, string Purpose,
    string[] CapabilityIds, string Status, string? DisabledCode, string[] Aliases, bool ReadOnly, bool DryRun,
    string JobBehavior, bool Destructive, bool? Reversible, string[] SideEffects, Flag[] Flags, string[] Identities,
    string[] IdentityValues, string[] Examples, string[] SeeAlso, int[] ExitCodes, string[] Preconditions, string[] OutputFields)
{
    public string DefaultSort => Resource switch { "activity-log" => "time descending, id ascending", "download" or "queue" => "GUI scheduling order", "setting" => "schema order", _ => "default" };
    public object InputSchema => new { identities = Identities, allowed_identity_values = IdentityValues, flags = Flags };
    public object OutputSchema => new { fields = OutputFields, envelope = "v1", paging = Verb == "list" ? "snapshot; expires after 5m; default 50; maximum 200" : null };
    public string[] SecretChannels => Flags.Where(f => f.Name is "secret-stdin" or "code-stdin").Select(f => "--" + f.Name).ToArray();
    public string Key => string.IsNullOrEmpty(Verb) ? Resource : Resource + " " + Verb;
}
public static class Catalogue
{
    public static readonly Command[] Commands = JsonSerializer.Deserialize<Command[]>(
        typeof(Catalogue).Assembly.GetManifestResourceStream("LuaTools.Agent.catalogue.json")!, Protocol.Json)!;
    public static Command? Find(string key) => Commands.FirstOrDefault(c => c.Key == key);
    public static string[] Suggest(string input) => Commands.Select(c => c.Key).OrderBy(c => Distance(input, c)).Take(3).ToArray();
    private static int Distance(string a, string b)
    {
        int[] row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++) { int old = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1)); prev = old; }
        }
        return row[^1];
    }
}
