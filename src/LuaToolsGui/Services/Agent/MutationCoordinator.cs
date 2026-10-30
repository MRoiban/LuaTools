using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using Microsoft.Extensions.Logging;
namespace LuaToolsGui.Services.Agent;

public sealed record MutationPlan(string Id, string Context, string Version, string[] Targets, string[] Impact,
    bool RequiresConfirmation, bool RequiresSteamRestart = false);
public sealed record PreparedMutation(MutationPlan Plan, Func<CancellationToken, Task<Envelope>> Commit);

/// <summary>One admission boundary for all resources. Work returned by Commit must be short or return a job.</summary>
public sealed class MutationCoordinator(string? journalPath = null)
{
    private readonly SemaphoreSlim _admission = new(1);
    private readonly Dictionary<string, (DateTimeOffset Time, MutationPlan Plan)> _plans = [];
    private readonly string _path = journalPath ?? Path.Combine(DataPaths.Local, AppConfig.DataDirectoryName, "agent-journal.json");
    private sealed record Entry(string Key, string Digest, string Instance, DateTimeOffset Time, string State, Envelope? Receipt);
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static Envelope Failure(string code, int exit, object? details = null) => Envelope.Fail(code, exit, Resources.Strings.Agent_MutationRejected) with
    { Error = new(code, exit, Resources.Strings.Agent_MutationRejected, Details: Protocol.Node(details)) };
    public async Task<Envelope> ExecuteAsync(AgentRequest request, Func<CancellationToken, Task<PreparedMutation>> prepare, CancellationToken ct)
    {
        if (request.Flags.ContainsKey("read-only")) return Failure("READ_ONLY", 5);
        await _admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Fingerprints exclude rendering and consent, but include all operands and actor attribution.
            string digest = Hash(JsonSerializer.Serialize(new { request.Command, request.Arguments, request.ExpectedSteam,
                flags = request.Flags.Where(f => f.Key is not ("yes" or "dry-run" or "plan-id" or "json" or "format" or "no-input" or "timeout" or "no-start" or "verbose" or "quiet" or "fields" or "idempotency-key")).OrderBy(f => f.Key) }, Protocol.Json));
            string key = Hash(request.Flags.GetValueOrDefault("idempotency-key") ?? request.RequestId);
            List<Entry> journal = [];
            if (!request.Flags.ContainsKey("dry-run"))
            {
                if (File.Exists(_path))
                {
                    if (new FileInfo(_path).Length > 16 * 1024 * 1024) return Failure("JOURNAL_UNAVAILABLE", 7);
                    journal = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Protocol.Json) ?? [];
                }
                var old = journal.FirstOrDefault(e => e.Key == key);
                if (old is not null)
                {
                    if (old.Time < DateTimeOffset.UtcNow.AddDays(-7)) return Failure("REQUEST_EXPIRED", 3);
                    if (old.Digest != digest) return Failure("REQUEST_ID_CONFLICT", 6);
                    return old.State == "accepted" && old.Receipt is not null ? old.Receipt : Failure("OUTCOME_UNKNOWN", 7);
                }
            }
            var prepared = await prepare(ct).ConfigureAwait(false);
            var plan = prepared.Plan;
            foreach (string expired in _plans.Where(p => p.Value.Time < DateTimeOffset.UtcNow.AddMinutes(-5)).Select(p => p.Key).ToArray()) _plans.Remove(expired);
            if (request.Flags.GetValueOrDefault("plan-id") is { } planId)
            {
                if (!_plans.TryGetValue(planId, out var saved)) return Failure("PLAN_EXPIRED", 3);
                if (saved.Plan.Context != plan.Context || saved.Plan.Version != plan.Version || !saved.Plan.Targets.SequenceEqual(plan.Targets) || !saved.Plan.Impact.SequenceEqual(plan.Impact)) return Failure("PLAN_STALE", 6, plan);
                plan = saved.Plan;
            }
            if (request.Flags.GetValueOrDefault("if-version") is { } version && version != plan.Version) return Failure("VERSION_CONFLICT", 6, new { current_version = plan.Version });
            if (_plans.Count >= 1000) _plans.Remove(_plans.Keys.First());
            _plans[plan.Id] = (DateTimeOffset.UtcNow, plan);
            if (request.Flags.ContainsKey("dry-run"))
            {
                if (_plans.Count >= 1000) _plans.Remove(_plans.Keys.First());
                _plans[plan.Id] = (DateTimeOffset.UtcNow, plan);
                return Envelope.Success(new { plan });
            }
            if (plan.RequiresConfirmation && !request.Flags.ContainsKey("yes")) return Failure("CONSENT_REQUIRED", 8, new { plan });
            if (plan.RequiresSteamRestart && !request.Flags.ContainsKey("restart-steam")) return Failure("STEAM_RUNNING", 6, new { plan, required_flags = new[] { "--restart-steam", "--yes" } });
            ct.ThrowIfCancellationRequested();
            var intent = new Entry(key, digest, request.InstanceId, DateTimeOffset.UtcNow, "intent", null);
            journal.RemoveAll(e => e.Time < DateTimeOffset.UtcNow.AddDays(-7));
            while (journal.Count >= 10000) journal.RemoveAt(0);
            journal.Add(intent); Save(journal); // durable intent precedes service action; crash never replays
            Envelope receipt;
            try { receipt = await prepared.Commit(ct).ConfigureAwait(false); }
            catch (MutationRejectedException ex) { receipt = Failure(ex.Code, ex.ExitCode); }
            catch (OperationCanceledException) { throw; } // keep intent: outcome may be unknown
            catch { receipt = Failure("MUTATION_FAILED", 1); }
            // Only allowlisted, secret-free receipts belong here. Never journal request payloads.
            journal[^1] = intent with { State = "accepted", Receipt = receipt }; Save(journal);
            AppEventLog.Shared.Write(LogLevel.Information, "Agent", $"actor=agent command={request.Command} outcome={(receipt.Ok ? "success" : receipt.Error?.Code)} note={LogRedactor.Redact(request.Flags.GetValueOrDefault("actor-note") ?? "")}");
            return receipt;
        }
        catch (MutationRejectedException ex) { return Failure(ex.Code, ex.ExitCode); }
        finally { _admission.Release(); }
    }
    private void Save(List<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Protocol.Json)); File.Move(temp, _path, true);
    }
}
public sealed class MutationRejectedException(string code, int exitCode) : Exception
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}
