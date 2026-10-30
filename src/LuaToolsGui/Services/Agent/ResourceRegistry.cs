using System.Reflection;
using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class AgentResourceAttribute(string name) : Attribute { public string Name { get; } = name; }
internal static class ResourceRegistry
{
    private static readonly Dictionary<string, MethodInfo> Methods = typeof(AgentCommandService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .SelectMany(m => m.GetCustomAttributes<AgentResourceAttribute>().Select(a => (a.Name, Method: m))).ToDictionary(x => x.Name, x => x.Method);
    public static Func<AgentRequest, CancellationToken, Task<Envelope>>? Find(AgentCommandService service, string resource) =>
        Methods.TryGetValue(resource, out var method) ? method.CreateDelegate<Func<AgentRequest, CancellationToken, Task<Envelope>>>(service) : null;
}
public sealed partial class AgentCommandService
{
    public static object[] Actions(string resource) => AvailableActions.For(resource);
}
