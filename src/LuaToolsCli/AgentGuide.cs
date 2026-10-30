using LuaTools.Agent;
namespace LuaToolsCli;
public static class AgentGuide
{
    public static string Text
    {
        get
        {
            using var reader = new StreamReader(typeof(AgentGuide).Assembly.GetManifestResourceStream("LuaTools.Agent.guide.md")!);
            return reader.ReadToEnd() + string.Join('\n', Catalogue.Commands.GroupBy(c => c.Resource).Select(g =>
                "- " + g.Key + ": " + string.Join("; ", g.Select(c => c.Syntax)))) + "\n";
        }
    }
}
