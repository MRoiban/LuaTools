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
    private static readonly object SecretKeyGate = new();
    private byte[]? _secretDigestKey;
    private byte[] SecretDigestKey()
    {
        lock (SecretKeyGate)
        {
            if (_secretDigestKey is not null) return _secretDigestKey;
            string path = _path + ".digest-key";
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 8192) throw new IOException();
                _secretDigestKey = LocalDiscovery.Protect(File.ReadAllBytes(path), true);
                if (_secretDigestKey.Length != 32) throw new IOException();
                return _secretDigestKey;
            }
            // Persist only a DPAPI CurrentUser protected random HMAC key. A missing or
            // corrupt key fails closed; no unkeyed digest of short login codes is stored.
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] protectedKey = LocalDiscovery.Protect(key, false);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(protectedKey); file.Flush(true); }
            return _secretDigestKey = key;
        }
    }
    private readonly SemaphoreSlim _admission = new(1);
    private readonly Dictionary<string, (DateTimeOffset Time, MutationPlan Plan, string Digest)> _plans = [];
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
            string digest = Hash(JsonSerializer.Serialize(new { request.Command, request.Arguments, request.ExpectedSteam, request.Files, secret_digest = request.Secret is null ? null : Convert.ToHexString(HMACSHA256.HashData(SecretDigestKey(), Encoding.UTF8.GetBytes(request.Secret))),
                flags = request.Flags.Where(f => f.Key is not ("yes" or "dry-run" or "plan-id" or "json" or "format" or "no-input" or "timeout" or "no-start" or "verbose" or "quiet" or "no-color" or "expect-instance" or "expect-steam" or "fields" or "idempotency-key")).OrderBy(f => f.Key).ToDictionary(f => f.Key, f => CanonicalFlag(f.Key, f.Value)) }, Protocol.Json));
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
                if (saved.Digest != digest || saved.Plan.Context != plan.Context || saved.Plan.Version != plan.Version || !saved.Plan.Targets.SequenceEqual(plan.Targets) || !saved.Plan.Impact.SequenceEqual(plan.Impact)) return Failure("PLAN_STALE", 6, plan);
                plan = saved.Plan;
            }
            if (request.Flags.GetValueOrDefault("if-version") is { } version && version != plan.Version) return Failure("VERSION_CONFLICT", 6, new { current_version = plan.Version });
            if (_plans.Count >= 1000) _plans.Remove(_plans.Keys.First());
            _plans[plan.Id] = (DateTimeOffset.UtcNow, plan, digest);
            if (request.Flags.ContainsKey("dry-run"))
            {
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
            receipt = JobReceipts.Normalize(receipt);
            journal[^1] = intent with { State = "accepted", Receipt = receipt }; Save(journal);
            AppEventLog.Shared.Write(LogLevel.Information, "Agent", $"actor=agent command={request.Command} outcome={(receipt.Ok ? "success" : receipt.Error?.Code)} note={LogRedactor.Redact(request.Flags.GetValueOrDefault("actor-note") ?? "")}");
            return receipt;
        }
        catch (MutationRejectedException ex) { return Failure(ex.Code, ex.ExitCode); }
        catch (JsonException) { return Failure("INVALID_DATA", 2); }
        catch (ArgumentException) { return Failure("INVALID_USAGE", 2); }
        catch (InvalidOperationException) { return Failure("INVALID_DATA", 2); }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { return Failure("JOURNAL_UNAVAILABLE", 7); }
        finally { _admission.Release(); }
    }
    public async Task<Envelope?> FindAsync(string idempotencyKey, CancellationToken ct)
    {
        await _admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            if (new FileInfo(_path).Length > 16 * 1024 * 1024) return Failure("JOURNAL_UNAVAILABLE", 7);
            var entry = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Protocol.Json)?.FirstOrDefault(e => e.Key == Hash(idempotencyKey));
            return entry is null ? null : entry.Time < DateTimeOffset.UtcNow.AddDays(-7) ? Failure("REQUEST_EXPIRED", 3)
                : entry.State == "accepted" ? entry.Receipt : Failure("OUTCOME_UNKNOWN", 7);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return Failure("JOURNAL_UNAVAILABLE", 7); }
        finally { _admission.Release(); }
    }
    private static string? CanonicalFlag(string name, string? value)
    {
        if (name != "data" || value is null) return value;
        try { return Canonical(JsonNode.Parse(value))?.ToJsonString(); }
        catch (JsonException) { return value; } // validation reports shape without echoing content
    }
    private static JsonNode? Canonical(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()), _ => value?.DeepClone(),
    };
    private void Save(List<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".new";
        string json = JsonSerializer.Serialize(entries, Protocol.Json);
        while (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024 && entries.Count > 1)
        { entries.RemoveAt(0); json = JsonSerializer.Serialize(entries, Protocol.Json); }
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new IOException();
        File.WriteAllText(temp, json); File.Move(temp, _path, true);
    }
}
public sealed class MutationRejectedException(string code, int exitCode) : Exception
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}
