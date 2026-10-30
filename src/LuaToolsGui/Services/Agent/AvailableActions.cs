using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public static class AvailableActions
{
    public static object[] For(string resource, Func<Command, string?>? liveReason = null, bool readOnly = false) =>
        ForReasons(resource, c => liveReason?.Invoke(c) is { } reason ? [reason] : [], readOnly);
    public static object[] ForReasons(string resource, Func<Command, string[]> liveReasons, bool readOnly = false) => Catalogue.Commands.Where(c => c.Resource == resource).Select(c =>
    {
        var reasons = new List<string>();
        if (c.Status != "enabled") reasons.Add(c.DisabledCode ?? "NOT_IMPLEMENTED");
        if (readOnly && !c.ReadOnly) reasons.Add("READ_ONLY");
        reasons.AddRange(liveReasons(c));
        return (object)new
        {
            resource, verb = c.Verb, enabled = reasons.Count == 0,
            disabled_reasons = reasons.Distinct().Select(code => new { code, message = Resources.Strings.Agent_MutationRejected, gate = code == "NOT_IMPLEMENTED" ? "implementation" : "live", source_capability_id = c.CapabilityIds.FirstOrDefault() }).ToArray(),
            destructive = c.Destructive, requires_confirmation = c.Destructive, required_flags = c.Destructive ? new[] { "--yes" } : [],
        };
    }).ToArray();
}
