using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;
public sealed partial class AgentCommandService
{
    internal static Envelope HumanActionRequired(string id) => Envelope.Fail("HUMAN_ACTION_REQUIRED", 8, Resources.Strings.Agent_Account_Unavailable) with
    { Error = new("HUMAN_ACTION_REQUIRED", 8, Resources.Strings.Agent_Account_Unavailable, Hint: "luatools job wait " + id, Details: Protocol.Node(new { job_id = id })) };
    [AgentResource("account")]
    private Task<Envelope> AccountAsync(AgentRequest r, CancellationToken ct)
    {
        if (r.Command == "account get") return Snapshot(Whoami, ct);
        return _mutations.ExecuteAsync(r, token => Snapshot(() =>
        {
            string context = PlanContext;
            bool signedIn = auth.IsSignedIn;
            bool login = r.Command == "account login";
            if (login && !r.Flags.ContainsKey("browser") && string.IsNullOrWhiteSpace(r.Secret)) throw new MutationRejectedException("SECRET_REQUIRED", 2);
            var plan = new MutationPlan(Guid.NewGuid().ToString("N"), context, MutationCoordinator.Hash(signedIn.ToString()), ["account"], [login ? "Authenticate Discord session; browser requires human action." : "Clear local session; no remote revocation."], login && !r.Flags.ContainsKey("browser"));
            return new PreparedMutation(plan, commit => Snapshot(() =>
            {
                if (context != PlanContext) throw new MutationRejectedException("PLAN_STALE", 6);
                if (login)
                {
                    if (auth.IsSignedIn) return Whoami();
                    string id = Jobs.Register("account login", async lifetime =>
                    {
                        if (r.Flags.ContainsKey("browser")) await auth.SignInAsync(lifetime);
                        else await AccountCabinetOperations.RedeemCodeAsync(auth, r.Secret!, lifetime);
                        return Whoami();
                    }, _hostLifetime);
                    return r.Flags.ContainsKey("browser") ? HumanActionRequired(id) : Envelope.Success(new { job_id = id });
                }
                auth.SignOut();
                return Envelope.Success(new { signed_out = true });
            }, commit));
        }, token), ct);
    }
}
