using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public static class AvailableActions
{
    public static object[] For(string resource, Func<Command, string?>? liveReason = null, bool readOnly = false) => Catalogue.Commands.Where(c => c.Resource == resource).Select(c =>
    {
        string? code = c.Status != "enabled" ? c.DisabledCode ?? "NOT_IMPLEMENTED" : readOnly && !c.ReadOnly ? "READ_ONLY" : liveReason?.Invoke(c);
        return (object)new
        {
            resource, verb = c.Verb, enabled = code is null,
            disabled_reasons = code is null ? [] : new[] { new { code, message = Resources.Strings.Agent_MutationRejected, gate = code == "NOT_IMPLEMENTED" ? "implementation" : "live", source_capability_id = c.CapabilityIds.FirstOrDefault() } },
            destructive = c.Destructive, requires_confirmation = c.Destructive, required_flags = c.Destructive ? new[] { "--yes" } : [],
        };
    }).ToArray();
}
