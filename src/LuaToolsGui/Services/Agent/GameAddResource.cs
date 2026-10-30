using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsGui.Services.Downloads;
namespace LuaToolsGui.Services.Agent;

public sealed partial class AgentCommandService
{
    private AcquisitionServices Acquisition => acquisition ?? throw new MutationRejectedException("SERVICE_UNAVAILABLE", 7);
    private static long AddAppId(string? value)
    {
        if (!ApiContract.IsAppId(value) || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
            throw new MutationRejectedException("INVALID_APPID", 2);
        return id;
    }
    private static void AddNoData(AgentRequest r)
    {
        try { BulkSelection.Data(r); }
        catch (JsonException) { throw new MutationRejectedException("INVALID_DATA", 2); }
        if (r.Flags.ContainsKey("ids") || r.Flags.ContainsKey("where") || r.Flags.ContainsKey("data") && r.Arguments.Length == 0) throw new MutationRejectedException("INVALID_SELECTION", 2);
    }
    private string AddVersion(long id) => Acquisition.Overwrites.Version(id);
    private void AddSteamGate()
    {
        if (!steam.IsValid) throw new MutationRejectedException("STEAM_PATH_INVALID", 6);
    }
    private static Envelope AddFailure(string code, int exit) => Envelope.Fail(code, exit, Resources.Strings.Agent_Add_Rejected);

    private Task<Envelope> AddRemoteAsync(AgentRequest r, bool dlc, CancellationToken ct) => _mutations.ExecuteAsync(r, async token =>
    {
        AddNoData(r);
        if (r.Arguments.Length != 1) throw new MutationRejectedException("INVALID_APPID", 2);
        long id = AddAppId(r.Arguments[0]);
        var context = await Snapshot(() => (Context: PlanContext, Version: AddVersion(id)), token);
        var sources = await Acquisition.Sources.CheckAsync(id, ct: token);
        string selected = dlc ? PluginAddService.DlcSourceName : r.Flags.GetValueOrDefault("source") ?? "auto";
        var row = selected == "auto" ? sources.Sources.FirstOrDefault(s => s.CanDownload)
            : sources.Sources.FirstOrDefault(s => string.Equals(s.Name, selected, StringComparison.OrdinalIgnoreCase) || string.Equals(s.DisplayName, selected, StringComparison.OrdinalIgnoreCase));
        if (row is null) throw new MutationRejectedException("SOURCE_UNAVAILABLE", 6);
        bool isDlc = dlc || sources.Details?.IsDlc == true;
        string? parent = isDlc ? sources.Details?.BaseAppId : null;
        if (isDlc && !ApiContract.IsAppId(parent)) throw new MutationRejectedException("BASE_APP_REQUIRED", 6);
        var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context.Context, context.Version, [id.ToString()],
            [Resources.Strings.Agent_Add_Impact, "source=" + row.Name, "existing_sha256=" + context.Version], true);
        return new PreparedMutation(plan, async commitToken =>
        {
            if (!row.CanDownload) throw new MutationRejectedException(row.DisabledReasons.FirstOrDefault(code => code != "SOURCE_UNKNOWN" && code != "SOURCE_UNAVAILABLE") ?? "SOURCE_UNAVAILABLE", row.DisabledReasons.Any(code => code == "LOGIN_REQUIRED" || code.StartsWith("HUBCAP_KEY_", StringComparison.Ordinal)) ? 4 : 7);
            var fresh = await Acquisition.Sources.CheckAsync(id, ct: commitToken);
            var current = fresh.Sources.FirstOrDefault(s => s.Name == row.Name);
            return await Snapshot(() =>
            {
                AddSteamGate();
                if (PlanContext != context.Context || AddVersion(id) != context.Version || current?.CanDownload != true || fresh.Details?.BaseAppId != sources.Details?.BaseAppId)
                    throw new MutationRejectedException("PLAN_STALE", 6);
                var activeIds = queue.Items.Where(i => i.IsActive).Select(i => i.Id).ToHashSet();
                string approvedVersion = context.Version;
                var item = Acquisition.Sources.Enqueue(id, sources.Details?.Name, current, parent,
                    confirm: async (file, queued, lifetime) =>
                    {
                        if (AddVersion(id) == approvedVersion) return true;
                        var diff = await Acquisition.Overwrites.InspectAsync(file.FilePath, id, ct: lifetime);
                        if (!await ConsentCoordinator.WaitAsync(queued.Id, diff, lifetime)) return false;
                        approvedVersion = diff.Version;
                        return AddVersion(id) == approvedVersion;
                    }, installVersion: () => approvedVersion);
                return Envelope.Success(new { id = item.Id, job_id = item.Id, state = "queued", deduplicated = activeIds.Contains(item.Id), source = row.Name });
            }, commitToken);
        });
    }, ct);
}
