using LuaTools.Agent;
using LuaToolsGui.Services.Cabinet;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    private string CabinetVersion => MutationCoordinator.Hash(System.Text.Json.JsonSerializer.Serialize(cabinet?.Status, Protocol.Json));
    private string? CabinetGate() => cabinet is null ? "SERVICE_UNAVAILABLE" : !cabinet.Status.Enabled ? "CABINET_DISABLED" : cabinet.Status.StandaloneCabinetRunning ? "ENGINE_OWNED_ELSEWHERE" : cabinet.Status.IsSyncing ? "BUSY" : null;
    [AgentResource("cabinet")]
    private Task<Envelope> CabinetAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "cabinet get") return Snapshot(() => cabinet is null ? Envelope.Fail("SERVICE_UNAVAILABLE", 5, Resources.Strings.Agent_Account_Unavailable) : Envelope.Success(new
        { status = cabinet.Status, version = CabinetVersion, available_actions = AvailableActions.For("cabinet", c => c.ReadOnly ? null : CabinetGate()) }), ct);
        return CabinetMutationAsync(r, () => ["Local achievement, stats and playtime fingerprint sync; remote round follows the existing engine schedule."],
            () => AccountCabinetOperations.SyncAsync(cabinet!, _hostLifetime), ct);
    }
    private Task<Envelope> CabinetMutationAsync(AgentRequest r, Func<string[]> impacts, Func<Task> run, CancellationToken ct) =>
        MutateAsync(r, token => Snapshot(() =>
        {
            if (CabinetGate() is { } gate) throw new MutationRejectedException(gate, 5);
            string context = PlanContext, version = CabinetVersion;
            var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, version, [r.Command.Split(' ')[0]], impacts(), true);
            return new PreparedMutation(plan, commit => Snapshot(() =>
            {
                if (context != PlanContext || version != CabinetVersion) throw new MutationRejectedException("PLAN_STALE", 6);
                if (CabinetGate() is { } freshGate) throw new MutationRejectedException(freshGate, 5);
                string id = Jobs.Register(r.Command, async lifetime => { await run(); return Envelope.Success(new { completed = true }); }, _hostLifetime);
                return Envelope.Success(new { job_id = id });
            }, commit));
        }, token), ct);
    [AgentResource("cabinet-cloud")]
    private Task<Envelope> CabinetCloudAsync(AgentRequest r, CancellationToken ct)
    {
        var cloud = cabinet as ICabinetCloud;
        if (r.Command == "cabinet-cloud connect" && string.IsNullOrWhiteSpace(r.Secret)) return Task.FromResult(Envelope.Fail("SECRET_REQUIRED", 2, Resources.Strings.Agent_Account_Unavailable));
        if (r.Command == "cabinet-cloud connect")
        {
            try
            {
                if (CabinetConnectInput.Classify(r.Secret!) == CabinetConnectInputKind.Invalid) return Task.FromResult(Envelope.Fail("INVALID_SECRET", 2, Resources.Strings.Agent_Account_Unavailable));
                CabinetConnectInput.NormalizeServer(r.Flags.GetValueOrDefault("server"));
            }
            catch (Exception ex) when (ex is FormatException or CabinetConnectException) { return Task.FromResult(Envelope.Fail("INVALID_SERVER", 2, Resources.Strings.Agent_Account_Unavailable)); }
        }
        if (r.Command == "cabinet-cloud get") return Snapshot(() => cloud is null ? Envelope.Fail("SERVICE_UNAVAILABLE", 5, Resources.Strings.Agent_Account_Unavailable) : Envelope.Success(new
        {
            connected = cloud.Status.Connected, user_name = cloud.Status.UserName is { } name ? LogRedactor.Redact(name) : null,
            server_host = cloud.Status.ServerHost, last_sync = cloud.Status.LastSync, friend_labels = cloud.FriendLabels,
            version = CabinetVersion, available_actions = AvailableActions.For("cabinet-cloud", c => c.ReadOnly ? null : CabinetGate() ?? (cloud.Status.Connected || c.Verb == "connect" ? null : "NOT_CONNECTED")),
        }), ct);
        if (r.Command == "cabinet-cloud update" && r.Flags.GetValueOrDefault("friend-labels") != "true")
            return Task.FromResult(Envelope.Fail("UNSUPPORTED", 5, Resources.Strings.Agent_Account_Unavailable));
        if (cloud is null || (!cloud.Status.Connected && r.Command != "cabinet-cloud connect")) return Task.FromResult(Envelope.Fail("NOT_CONNECTED", 5, Resources.Strings.Agent_Account_Unavailable));
        return CabinetMutationAsync(r, () => [r.Command == "cabinet-cloud connect" ? "Pair this device and persist protected Cabinet credentials; enable ongoing presence and progress sharing. Cloud saves remain unchanged." : r.Command == "cabinet-cloud disconnect" ? "Remove local Cabinet and CloudRedirect credentials; no remote revocation." : "Enable Steam friend labels marker; restart Steam manually."],
            () => { if (r.Command == "cabinet-cloud connect") return AccountCabinetOperations.ConnectAsync(cabinet!, cloud, r.Secret ?? throw new InvalidOperationException(), r.Flags.GetValueOrDefault("server"), _hostLifetime); if (r.Command == "cabinet-cloud disconnect") return cloud.DisconnectAsync(_hostLifetime); cloud.FriendLabels = true; return Task.CompletedTask; }, ct);
    }
}
