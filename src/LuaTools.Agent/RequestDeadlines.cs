namespace LuaTools.Agent;

public static class RequestDeadlines
{
    public static TimeSpan For(AgentRequest request)
    {
        // Wait is a sequence of short observations; accepted work is never tied to a connection.
        var maximum = request.Command switch { "doctor" => TimeSpan.FromSeconds(30), _ => TimeSpan.FromSeconds(10) };
        var requested = CommandParser.Duration(request.Flags.GetValueOrDefault("timeout") ?? "30s") ?? TimeSpan.FromSeconds(30);
        return requested < maximum ? requested : maximum;
    }
}
