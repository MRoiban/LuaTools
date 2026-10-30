using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services.Agent;

/// <summary>Explicit, read-only application boundary. No DI resolution or plugin dispatch is exposed.</summary>
public sealed partial class AgentCommandService(SettingsService settings, SteamService steam, AuthService auth,
    DownloadQueue queue, UnlockerService unlocker, PluginInstallerService plugin, UpdateService updates, ToastService? toast = null, MutationCoordinator? mutations = null, Cabinet.ICabinetService? cabinet = null, ModeCoordinator? modes = null, FixCoordinator? fixes = null,
    PluginCoordinator? plugins = null, SteamCoordinator? processes = null, ApplicationCoordinator? lifecycle = null,
    SteamAutoCrackService? tool = null, ManifestJobFactory? factory = null)
{
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public AgentEventHub Events => _jobs.Events;
    private volatile bool _exiting;
    private CancellationToken _hostLifetime;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action> _shutdownReplies = new();
    private readonly SnapshotPager _pager = new();
    private readonly MutationCoordinator _mutations = mutations ?? new();
    private readonly AgentJobRegistry _jobs = new(queue);
    internal AgentJobRegistry Jobs => _jobs;
    internal void BindHost(CancellationToken lifetime) { _hostLifetime = lifetime; Jobs.Events = new AgentEventHub(InstanceId); }
    private string PlanContext => MutationCoordinator.Hash(InstanceId + SteamIdentity + auth.IsSignedIn + string.Join(':',
        Protocol.Node(ReadSettings(settings))!.AsArray().Select(n => n!["version"]!.ToString())));
    public void AfterReply(AgentRequest request)
    {
        if (_shutdownReplies.TryRemove(request.RequestId, out var action))
            Application.Current?.Dispatcher.BeginInvoke(action);
    }
    public string SteamIdentity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((steam.EffectivePath ?? "").ToUpperInvariant())));
    private Task<T> Snapshot<T>(Func<T> read, CancellationToken ct)
    {
        var dispatcher = queue.CommandDispatcher;
        return dispatcher is null || dispatcher.CheckAccess() ? Task.FromResult(read()) : dispatcher.InvokeAsync(read, DispatcherPriority.Background, ct).Task;
    }
    public async Task<Envelope> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        if (request.Protocol != Protocol.Version) return Envelope.Fail("PROTOCOL_MISMATCH", 6, Resources.Strings.Agent_ContextChanged);
        if (request.InstanceId != InstanceId || request.Flags.GetValueOrDefault("expect-instance") is { } instance && instance != InstanceId || request.ExpectedSteam is { } selected && selected != SteamIdentity)
            return Envelope.Fail(request.Flags.ContainsKey("plan-id") ? "PLAN_STALE" : "CONTEXT_CHANGED", 6, Resources.Strings.Agent_ContextChanged);
        // Validate again at the trust boundary, including projections. Never trust client parsing.
        if (request.Flags.ContainsKey("ids-from")) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments);
        var args = request.Command.Split(' ').Concat(request.Arguments).ToList();
        foreach (var (flag, value) in request.Flags) { args.Add("--" + flag); if (value is not null) args.Add(value); }
        var parsed = CommandParser.Parse(args.ToArray());
        if (parsed.Problems.Length != 0) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments, problems: parsed.Problems);
        if (parsed.Command is null || parsed.Command.Key != request.Command) return Envelope.Fail("INVALID_USAGE", 2, Resources.Strings.Agent_InvalidArguments);
        if (_exiting && !parsed.Command.ReadOnly) return Envelope.Fail("BUSY", 7, Resources.Strings.Agent_MutationRejected);
        if (request.Flags.ContainsKey("read-only") && !parsed.Command.ReadOnly) return Envelope.Fail("READ_ONLY", 5, Resources.Strings.Agent_ReadOnly);
        if (!parsed.Command.ReadOnly && request.ExpectedSteam is null) return Envelope.Fail("CONTEXT_CHANGED", 6, Resources.Strings.Agent_ContextChanged);
        if (parsed.Command.Status != "enabled" || request.Flags.ContainsKey("fix")) return Envelope.Fail("NOT_IMPLEMENTED", 5, Resources.Strings.Agent_NotImplemented);
        if (request.Flags.ContainsKey("dry-run") && !parsed.Command.DryRun) return Envelope.Fail("DRY_RUN_UNSUPPORTED", 5, Resources.Strings.Agent_MutationRejected);
        var handler = ResourceRegistry.Find(this, parsed.Command.Resource);
        Envelope result = handler is null ? Envelope.Fail("NOT_IMPLEMENTED", 5, Resources.Strings.Agent_NotImplemented)
            : await handler(request, ct);
        return Results.Project(result, request.Flags.GetValueOrDefault("fields"));
    }
    private Envelope List(AgentRequest request, JsonArray rows)
    {
        bool reverse = request.Flags.GetValueOrDefault("sort") == "reverse";
        if (reverse) rows = new JsonArray(rows.Reverse().Select(x => x?.DeepClone()).ToArray());
        string key = request.Command + ":" + string.Join(';', request.Flags.Where(p => p.Key is "severity" or "since" or "category" or "sort").OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
        return _pager.List(key, rows, request.Flags);
    }    private bool? SelectedSteamRunning()
    {
        if (!steam.IsValid) return null;
        try { return (processes ?? new(steam)).IsRunning(); }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

}
