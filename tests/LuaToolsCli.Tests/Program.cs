using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LuaTools.Agent;
using LuaToolsCli;
using Cli = LuaToolsCli.Program;

static AgentRequest Request(Parsed? p = null) => new(1, "fake-test-capability", "fake-instance", Guid.NewGuid().ToString("N"), p?.Command?.Key ?? "ping", p?.Arguments ?? [], p?.Flags ?? []);
if (args.FirstOrDefault() == "--probe")
    return await Cli.RunAsync(["ping", "--json", "--no-start"], (p, ct) => AgentClient.ExchangeAsync(args[1], Request(p), ct));
int passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
async Task AsyncTest(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
string root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "docs/cli/capability-map.md"))) root = Directory.GetParent(root)?.FullName ?? throw new Exception("Run inside the repository.");
Test("ledger parity", () =>
{
    var ids = new HashSet<string>();
    foreach (string row in File.ReadLines(Path.Combine(root, "docs/cli/capability-map.md")).Where(l => l.StartsWith("| C-", StringComparison.Ordinal)))
    {
        string[] cells = Regex.Split(row.Trim().Trim('|'), @"(?<!\\)\|").Select(x => x.Trim()).ToArray();
        Assert(cells.Length == 11, "Expected exactly 11 cells: " + row);
        Assert(ids.Add(cells[0]), "Duplicate " + cells[0]);
        Assert(new[] { "mapped", "needs-refactor", "not-applicable", "owner-decision", "implemented" }.Contains(cells[10]), "Invalid status " + cells[0]);
        if (cells[9] == "—") continue;
        foreach (string text in System.Net.WebUtility.HtmlDecode(cells[9]).Split(';'))
        {
            var m = Regex.Match(text.Trim(), @"^luatools ([a-z-]+)(?: ([a-z][a-z-]*))?");
            Assert(m.Success, "Missing or malformed command " + cells[0]);
            string key = (m.Groups[1].Value + " " + m.Groups[2].Value).Trim();
            Assert(Catalogue.Find(key) is { } command && command.Resource == m.Groups[1].Value && command.Verb == m.Groups[2].Value, "Exact command not found: " + key);
        }
    }
});
Test("grammar parity", () =>
{
    string grammar = File.ReadAllText(Path.Combine(root, "docs/cli/design.md")).Split("```text")[1].Split("```")[0];
    foreach (Match m in Regex.Matches(grammar, @"(?m)^luatools ([a-z-]+)(?: ([a-z][a-z-]*))?")) Assert(Catalogue.Find((m.Groups[1].Value + " " + m.Groups[2].Value).Trim()) is not null, m.Value);
});
foreach (var c in Catalogue.Commands)
{
    Test("descriptor " + c.Key, () =>
    {
        Assert(c.Purpose.Trim().TrimEnd('.').ToLowerInvariant() != c.Key && c.Purpose.EndsWith('.') && c.Purpose.Split(' ').Length >= 4, "Placeholder purpose: " + c.Key);
        Assert(!c.SideEffects.Any(s => s.Contains("disabled in this slice", StringComparison.OrdinalIgnoreCase) || s.Contains("See capability-map", StringComparison.OrdinalIgnoreCase)), "Placeholder effects " + c.Key);
        Assert(c.Flags.Length > 0 && c.Examples.Distinct().Count() >= 2 && c.SeeAlso.Length > 0 && c.SideEffects.Length > 0 && c.Preconditions.Length > 0 && (c.ReadOnly || c.Reversible is not null), "Missing guide part: " + c.Key);
        foreach (var flag in c.Flags) Assert(flag.Name.Length > 0 && flag.Type.Length > 0 && flag.Default.ValueKind != JsonValueKind.Undefined && flag.AllowedValues is not null, "Incomplete flag " + c.Key);
        Assert(c.Status is "enabled" or "disabled", c.Key);
        foreach (string example in c.Examples) Assert(CommandParser.Parse(example.Split(' ').Skip(1).ToArray()).Problems.Length == 0, "Example invalid: " + example);
    });
}
Test("parser aggregates", () => Assert(CommandParser.Parse(["activity-log", "list", "--limit", "0", "--severity", "bad", "--quiet", "--verbose", "--since", "yesterday"]).Problems.Length == 4, "all problems"));
Test("unknown suggestions", () => Assert(CommandParser.Parse(["statuz"]).Problems.Any(p => p.Message.Contains("status")), "suggestion"));
Test("enum allowed values", () => Assert(CommandParser.Parse(["completion", "bad"]).Problems.Single().AllowedValues!.Contains("bash"), "values"));
Test("format conflicts", () => Assert(CommandParser.Parse(["ping", "--json", "--format", "csv"]).Problems.Length == 1, "format"));
Test("mutating flags on reads", () => Assert(CommandParser.Parse(["setting", "list", "--yes"]).Problems.Length == 1, "yes"));
Test("no TTY defaults", () => Assert(CommandParser.Parse(["ping"]).Format(false) == "json" && CommandParser.Parse(["ping"]).Format(true) == "table", "defaults"));
Test("NO_COLOR", () => Assert(!Rendering.UseColor(true, CommandParser.Parse(["ping"]), "") && !Rendering.UseColor(true, CommandParser.Parse(["ping", "--no-color"]), null), "color"));
Test("durations", () => Assert(CommandParser.Duration("250ms") == TimeSpan.FromMilliseconds(250) && CommandParser.Duration("25h") is null && CommandParser.Duration("NaNs") is null, "duration"));
Test("private projection", () => Assert(CommandParser.Parse(["setting", "get", "Language", "--fields", "HubcapApiKey"]).Problems.Length == 1, "secret"));
Test("envelope and exit codes", () =>
{
    for (int i = 0; i < 10; i++) { var result = Envelope.Fail("TEST", i, "test"); var json = JsonSerializer.Serialize(result, Protocol.Json); Assert(JsonSerializer.Deserialize<Envelope>(json, Protocol.Json)!.Error!.ExitCode == i, "exit " + i); }
    var keys = JsonNode.Parse(JsonSerializer.Serialize(Envelope.Success(new { a = 1 }), Protocol.Json))!.AsObject().Select(k => k.Key).ToArray();
    Assert(keys.SequenceEqual(new[] { "ok", "data", "page", "warnings" }), "envelope");
});
Test("nested fields preserve metadata", () =>
{
    var e = Envelope.Success(new { a = new { b = 1, secret = 2 }, other = 3 }, new(50, "cursor", true, 100), new Warning("TEST", "test"));
    var projected = Results.Project(e, "a.b"); Assert(projected.Data!.ToJsonString() == "{\"a\":{\"b\":1}}" && projected.Page == e.Page && projected.Warnings == e.Warnings, "project");
});
Test("snapshot paging and changed query", () =>
{
    var pager = new SnapshotPager(); var data = new JsonArray(Enumerable.Range(1, 6).Select(i => JsonValue.Create(i) as JsonNode).ToArray());
    var first = pager.List("x", data, new() { ["limit"] = "2" });
    var next = pager.List("x", [], new() { ["limit"] = "2", ["cursor"] = first.Page!.NextCursor });
    Assert(next.Data![0]!.GetValue<int>() == 3 && next.Page!.Total == 6, "snapshot");
    Assert(pager.List("y", [], new() { ["cursor"] = first.Page.NextCursor }).Error!.ExitCode == 3, "changed query");
});
Test("completion shells", () => { foreach (string shell in new[] { "bash", "fish", "zsh", "powershell" }) Assert(Rendering.Completion(shell).Contains("luatools"), shell); });
Test("resource help flags", () => Assert(CommandParser.Parse(["setting", "--help", "--format", "json"]).Problems.Length == 0, "resource help"));
Test("unknown resource help", () => Assert(CommandParser.Parse(["help", "bogus"]).Problems.Length == 1, "resource help"));
Test("strict unknown protocol fields", () =>
{
    bool rejected = false;
    try { JsonSerializer.Deserialize<AgentRequest>("{\"protocol\":1,\"unknown\":true}", Protocol.Json); } catch (JsonException) { rejected = true; }
    Assert(rejected, "unknown fields");
});
Test("help start and exits", () => Assert(Rendering.Help().Contains("Start here:") && Rendering.Help().Contains("9 GUI not running"), "help"));
await AsyncTest("absent GUI has exit 9 without stdin", async () => { if (!OperatingSystem.IsWindows()) Assert((await new AgentClient().InvokeAsync(CommandParser.Parse(["ping", "--no-start"]), default)).Error!.ExitCode == 9, "absent"); });
async Task<Exception?> BrokenFrame(Func<NamedPipeServerStream, CancellationToken, Task> send)
{
    string name = "LuaTools-test-" + Guid.NewGuid().ToString("N");
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
    await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var host = Task.Run(async () => { await server.WaitForConnectionAsync(cts.Token); await Protocol.ReadAsync<AgentRequest>(server, Protocol.MaxRequest, cts.Token); await send(server, cts.Token); });
    Exception? error = null;
    try { await AgentClient.ExchangeAsync(name, Request(), cts.Token); } catch (Exception ex) { error = ex; }
    try { await host; } catch (OperationCanceledException) { }
    return error;
}
await AsyncTest("oversized frame rejected", async () => Assert(await BrokenFrame(async (s, ct) => { byte[] h = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(h, Protocol.MaxFrame + 1); await s.WriteAsync(h, ct); }) is InvalidDataException, "oversize"));
await AsyncTest("malformed JSON rejected", async () => Assert(await BrokenFrame(async (s, ct) => { byte[] b = [1, 0, 0, 0, 123]; await s.WriteAsync(b, ct); }) is JsonException, "json"));
await AsyncTest("stalled frame deadline", async () => Assert(await BrokenFrame(async (s, ct) => { await s.WriteAsync(new byte[] { 1, 0 }, ct); await Task.Delay(2000, ct); }) is OperationCanceledException, "stall"));
await AsyncTest("fresh-process fake-host ping", async () =>
{
    var times = new List<double>();
    for (int i = 0; i < 12; i++)
    {
        string name = "LuaTools-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var host = Task.Run(async () => { await server.WaitForConnectionAsync(cts.Token); var req = await Protocol.ReadAsync<AgentRequest>(server, Protocol.MaxRequest, cts.Token); Assert(req.Command == "ping" && req.Flags.ContainsKey("no-input"), "ping request is noninteractive"); await Protocol.WriteAsync(server, Envelope.Success(new { protocol = 1, ready = true }), Protocol.MaxFrame, cts.Token); });
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); psi.ArgumentList.Add("--probe"); psi.ArgumentList.Add(name);
        var clock = Stopwatch.StartNew(); using var child = Process.Start(psi)!; child.StandardInput.Close(); string output = await child.StandardOutput.ReadToEndAsync(); string errors = await child.StandardError.ReadToEndAsync(); await child.WaitForExitAsync(cts.Token); times.Add(clock.Elapsed.TotalMilliseconds); await host;
        Assert(child.ExitCode == 0 && JsonNode.Parse(output)!["ok"]!.GetValue<bool>() && errors == "", output + errors);
    }
    times.Sort(); Console.WriteLine($"MEASURE fresh-process fake-host CLI ping n=12 median_ms={times[6]:F2} p95_ms={times[11]:F2} min_ms={times[0]:F2}");
});
foreach (var (arguments, expected) in new (string[], int)[]
{
    (["application", "start", "--dry-run", "--json"], 5), (["version", "--json"], 0), (["help", "setting", "--json"], 0), (["commands", "--json"], 0),
    (["completion", "bash", "--json"], 0), (["setting", "update", "Language", "--value", "fr", "--no-start", "--json"], 9),
    (["statuz", "--json"], 2), (["activity-log", "list", "--severity", "bad", "--limit", "0", "--json"], 2),
    (["ping", "--no-start", "--json"], 9),
})
await AsyncTest("entrypoint " + string.Join(' ', arguments), async () =>
{
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(typeof(Cli).Assembly.Location);
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var child = Process.Start(start)!; child.StandardInput.Close();
    string output = await child.StandardOutput.ReadToEndAsync(); string error = await child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Assert(child.ExitCode == expected, output + error);
    var envelope = JsonNode.Parse(output)!;
    Assert(envelope["ok"]!.GetValue<bool>() == (expected == 0), output);
    if (arguments[0] == "commands")
    {
        var keys = envelope["data"]!["commands"]!.AsArray().Select(c => c!["key"]!.GetValue<string>()).ToHashSet();
        foreach (string line in File.ReadLines(Path.Combine(root, "docs/cli/capability-map.md")).Where(l => l.StartsWith("| C-", StringComparison.Ordinal)))
        foreach (Match m in Regex.Matches(line.Split(" | ")[^2], @"luatools ([a-z-]+)(?: ([a-z][a-z-]*))?"))
            Assert(keys.Contains((m.Groups[1].Value + " " + m.Groups[2].Value).Trim()), m.Value);
    }
    if (arguments[0] == "activity-log") Assert(envelope["error"]!["problems"]!.AsArray().Count == 2, output);
});
foreach (string[] helpArgs in new[] { new[] { "--help" }, ["help", "setting"], ["setting", "update", "--help"] })
await AsyncTest("redirected readable help " + string.Join(' ', helpArgs), async () =>
{
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(typeof(Cli).Assembly.Location); foreach (string argument in helpArgs) start.ArgumentList.Add(argument);
    using var child = Process.Start(start)!; string text = await child.StandardOutput.ReadToEndAsync(); await child.WaitForExitAsync();
    Assert(child.ExitCode == 0 && text.StartsWith("LuaTools agent CLI") && !text.Contains("\\n"), text);
    if (helpArgs.Length == 1) Assert(!text.Contains("luatools game get") && text.Contains("Global flags:"), "short help");
});
Test("per-command guide order", () =>
{
    string help = Rendering.Help(Catalogue.Find("setting update"));
    string[] sections = ["Usage:", "Flags:", "Side effects:", "Reversibility:", "Preconditions:", "Examples:", "See also:"];
    Assert(sections.Select(s => help.IndexOf(s, StringComparison.Ordinal)).SequenceEqual(sections.Select(s => help.IndexOf(s, StringComparison.Ordinal)).OrderBy(i => i)) && sections.All(help.Contains), help);
});
Test("job wait preserves failed outcomes and pending decisions", () =>
{
    foreach (string state in new[] { "failed", "cancelled", "awaiting_confirmation" })
    {
        var outcome = AgentClient.WaitOutcome(Envelope.Success(new { id = "batch", state, result = new { results = new[] { new { ok = false } } }, pending_decision = new { readable = false } }))!;
        Assert(!outcome.Ok && outcome.Error!.Details!["id"]!.ToString() == "batch" && outcome.Error.Details["result"] is not null, "job result details");
    }
    Assert(AgentClient.WaitOutcome(Envelope.Success(new { state = "running" })) is null, "continue observation");
});
Test("depot repeated selection is canonical and entry zero is valid", () =>
{
    var parsed = CommandParser.Parse(["depot", "plan", "570", "--depot", "571", "--depot", "572"]);
    Assert(parsed.Problems.Length == 0 && parsed.Flags["depot"] == "571,572", "depot selection");
    Assert(CommandParser.Parse(["launch-option", "delete", "570", "--entry", "0"]).Problems.Length == 0, "zero-based entry");
});
Test("bulk input and command deadlines", () =>
{
    var p = CommandParser.Parse(["job", "cancel", "--ids", "one,two", "--dry-run"]); Assert(p.Problems.Length == 0, "bulk identity");
    Assert(CommandParser.Parse(["job", "wait", "one"]).Timeout == TimeSpan.FromMinutes(5), "wait budget");
    Assert(RequestDeadlines.For(Request(CommandParser.Parse(["doctor"]))) == TimeSpan.FromSeconds(30), "doctor budget");
    Assert(RequestDeadlines.For(Request(CommandParser.Parse(["ping", "--timeout", "10ms"]))) == TimeSpan.FromMilliseconds(10), "requested budget");
});
await AsyncTest("event replay from empty initial cursor", async () =>
{
    var hub = new AgentEventHub("initial"); string cursor = hub.CurrentCursor;
    hub.Publish("download", "queued", new { id = "one" });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await using var reader = hub.Watch(cursor, timeout.Token).GetAsyncEnumerator();
    Assert(await reader.MoveNextAsync() && reader.Current.Ok && reader.Current.Data!["event"]!["kind"]!.ToString() == "queued", "initial replay");
});
await AsyncTest("event retention rejects expired cursor", async () =>
{
    var hub = new AgentEventHub("test"); for (int i = 0; i < 1001; i++) hub.Publish("test", "state", new { value = i });
    await using var stream = hub.Watch("test:1", default).GetAsyncEnumerator();
    Assert(await stream.MoveNextAsync() && stream.Current.Error?.Code == "RESET_REQUIRED", "retention");
});
Test("bulk selection bounds and JSON schema", () =>
{
    var request = Request(CommandParser.Parse(["setting", "update", "--where", "type=bool", "--value", "false"]));
    var ids = BulkSelection.Select(request, new JsonArray(Protocol.Node(new { key = "FastFetch", type = "bool" }), Protocol.Node(new { key = "Language", type = "string" })), "key", ["type"]);
    Assert(ids.SequenceEqual(new[] { "FastFetch" }), "selection");
    bool rejected = false; try { BulkSelection.Data(request with { Flags = new() { ["data"] = "{\"secret\":true}" } }, "value"); } catch (JsonException) { rejected = true; }
    Assert(rejected, "unknown data field");
});

await AsyncTest("slow event consumer gets GAP without blocking publisher", async () =>
{
    using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var hub = new AgentEventHub("overflow");
    await using var events = hub.Watch(null, ct.Token).GetAsyncEnumerator();
    var first = events.MoveNextAsync().AsTask();
    hub.Publish("download", "state", new { id = "one" });
    Assert(await first, "subscribed");
    var watch = Stopwatch.StartNew();
    for (int i = 0; i < 513; i++) hub.Publish("download", "state", new { id = i });
    Assert(watch.Elapsed < TimeSpan.FromSeconds(1), "publisher blocked");
    Assert(await events.MoveNextAsync() && events.Current.Data?["event"]?["kind"]?.ToString() == "GAP", "gap");
});
await AsyncTest("download watch stops at GAP instead of reconnecting", async () =>
{
    int calls = 0;
    await LuaToolsCli.Program.RunAsync(["download", "watch", "--json"], (_, _) => { calls++; return Task.FromResult(Envelope.Success(new { @event = new { kind = "GAP" } })); });
    Assert(calls == 1, "reconnected after GAP");
});
await AsyncTest("session read-only is inherited and cannot be cleared", async () =>
{
    int mutations = 0;
    using var input = new StringReader("{\"type\":\"command\",\"id\":\"a\",\"args\":[\"setting\",\"update\",\"FastFetch\",\"--value\",\"true\"]}\n{\"type\":\"command\",\"id\":\"b\",\"args\":[\"setting\",\"update\",\"FastFetch\",\"--value\",\"true\",\"--read-only=false\"]}\n");
    using var output = new StringWriter();
    await StdioSession.RunAsync(CommandParser.Parse(["session", "--stdio", "--read-only"]), input, output, (p,c) => { mutations++; return Task.FromResult(Envelope.Success(null)); }, default);
    Assert(mutations == 0 && output.ToString().Contains("READ_ONLY") && output.ToString().Contains("INVALID_USAGE"), "session authority");
});
await AsyncTest("dedicated session secret is one use and never echoed", async () =>
{
    const string secret = "smm_fake_secret_not_an_account";
    using var input = new StringReader(JsonSerializer.Serialize(new { type = "secret", id = "s", value = secret }) + "\n" + JsonSerializer.Serialize(new { type = "command", id = "s", args = new[] { "hubcap-key", "update", "--yes" } }) + "\n" + JsonSerializer.Serialize(new { type = "command", id = "bad", args = new[] { "hubcap-key", "update", "--secret-stdin" } }) + "\n");
    using var output = new StringWriter(); int calls = 0;
    await StdioSession.RunAsync(CommandParser.Parse(["session", "--stdio"]), input, output, (p,c) => { Assert(p.Secret == secret && !p.Flags.Values.Contains(secret), "secret channel"); calls++; return Task.FromResult(Envelope.Success(new { accepted = true })); }, default);
    Assert(calls == 1 && !output.ToString().Contains(secret) && output.ToString().Contains("INVALID_USAGE"), "no echo or stdin");
});
await AsyncTest("real pipe secret is absent from request and reply", async () =>
{
    using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    const string secret = "smm_fake_pipe_secret";
    string name = "lt-s-" + Guid.NewGuid().ToString("N");
    await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var host = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(ct.Token);
        var request = await Protocol.ReadAsync<AgentRequest>(server, Protocol.MaxRequest, ct.Token);
        Assert(request.Secret is null && !JsonSerializer.Serialize(request, Protocol.Json).Contains(secret), "request is secret-free");
        Assert((await Protocol.ReadAsync<SecretFrame>(server, 8192, ct.Token)).Value == secret, "dedicated frame");
        await Protocol.WriteAsync(server, Envelope.Success(new { accepted = true }), Protocol.MaxFrame, ct.Token);
    });
    var result = await AgentClient.ExchangeAsync(name, Request() with { Secret = secret }, ct.Token);
    await host;
    Assert(result.Ok && !JsonSerializer.Serialize(result, Protocol.Json).Contains(secret), "receipt redacted");
});

await AsyncTest("session watch uses correlated streaming transport", async () =>
{
    using var input = new StringReader("{\"type\":\"command\",\"id\":\"watch-one\",\"args\":[\"activity-log\",\"watch\"]}\n");
    using var output = new StringWriter();
    await StdioSession.RunAsync(CommandParser.Parse(["session", "--stdio"]), input, output,
        (p,c) => throw new Exception("watch must stream"), default,
        (p,c,emit) => { emit(Envelope.Success(new { @event = new { kind = "heartbeat" } })); return Task.FromResult(Envelope.Success(new { @event = new { kind = "GAP" } })); });
    var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert(lines.Length == 2 && lines.All(l => JsonNode.Parse(l)?["id"]?.ToString() == "watch-one"), "correlated events");
});

Test("resource summaries and global flag isolation", () =>
{
    foreach (var group in Catalogue.Commands.GroupBy(c => c.Resource))
    {
        Assert(ResourceSummaries.All.TryGetValue(group.Key, out var summary) && !group.Any(c => c.Purpose == summary), "Missing independent summary: " + group.Key);
        foreach (var c in group)
        {
            string help = Rendering.Help(c);
            Assert(help.Contains(c.Purpose) && help.Contains("Flags:") && help.Contains("Global flags: see luatools --help."), c.Key);
            foreach (string global in Rendering.GlobalFlags) Assert(!help.Contains("  --" + global + ":"), "Repeated global row: " + c.Key + " " + global);
            foreach (var flag in c.Flags.Where(f => !Rendering.GlobalFlags.Contains(f.Name))) Assert(help.Contains("  --" + flag.Name + ":"), "Missing flag: " + c.Key);
            foreach (string example in c.Examples) Assert(help.Contains(example), "Missing example: " + c.Key);
            Assert(CommandParser.Parse(c.Key.Split(' ').Concat(new[] { "--help" }).ToArray()).Problems.Length == 0, "Help parse: " + c.Key);
        }
    }
});
Test("embedded agent guide and recipes", () =>
{
    string guide = AgentGuide.Text;
    Assert(guide.Split('\n').Length < 150, "Guide too long");
    Assert(File.ReadAllText(Path.Combine(root, "docs/cli/AGENTS.md")).Replace("\r\n", "\n").TrimEnd() == guide.Replace("\r\n", "\n").TrimEnd(), "Regenerate docs/cli/AGENTS.md from luatools guide");
    foreach (string line in guide.Split('\n').Where(l => l.StartsWith("luatools ")))
        Assert(CommandParser.Parse(line.TrimEnd().Split(' ').Skip(1).ToArray()).Problems.Length == 0, "Invalid recipe: " + line);
});
foreach (var command in Catalogue.Commands)
await AsyncTest("fake host contract " + command.Key, async () =>
{
    // Exercise the actual entrypoint and rendering. The fake host models admission only;
    // Windows service tests remain responsible for real coordinator effects and live gates.
    var arguments = command.Examples[0].Split(' ').Skip(1).ToList();
    // Use bounded explicit fixture input so file and secret commands exercise transport too.
    string fixture = Path.Combine(Path.GetTempPath(), "cli-contract-" + Guid.NewGuid().ToString("N") + ".lua");
    bool needsFile = arguments.Any(x => x is "--file" or "--diff-file");
    if (needsFile)
    {
        File.WriteAllText(fixture, "addappid(570)\n");
        for (int i = 0; i < arguments.Count - 1; i++)
            if (arguments[i] is "--file" or "--diff-file") arguments[i + 1] = fixture;
    }
    arguments.Remove("--json"); arguments.Remove("--yes"); arguments.Remove("--dry-run"); arguments.Add("--json");
    int mutations = 0; bool called = false;
    Task<Envelope> Host(Parsed parsed, CancellationToken ct)
    {
        called = true;
        Assert(parsed.Has("no-input") || !Console.IsInputRedirected && !Console.IsOutputRedirected, "Redirected calls must not prompt");
        if (parsed.Command!.Destructive && !parsed.Has("yes") && !parsed.Has("dry-run")) return Task.FromResult(Envelope.Fail("CONSENT_REQUIRED", 8, "Fake host consent"));
        if (!parsed.Command.ReadOnly && !parsed.Has("dry-run")) mutations++;
        return Task.FromResult(Envelope.Success(new { plan = parsed.Has("dry-run") }));
    }
    var oldOut = Console.Out; var oldErr = Console.Error; var oldIn = Console.In;
    using var input = new StringReader(command.Key == "session" ? "{\"type\":\"command\",\"id\":\"probe\",\"args\":[\"ping\"]}\n" : "fake-contract-secret");
    using var output = new StringWriter(); using var errors = new StringWriter();
    try
    {
        Console.SetOut(output); Console.SetError(errors); Console.SetIn(input);
        int exit = await Cli.RunAsync(arguments.ToArray(), Host).WaitAsync(TimeSpan.FromSeconds(3));
        if (called && command.Destructive) Assert(exit == 8 && mutations == 0, "Missing consent: " + command.Key);
        Assert(JsonNode.Parse(output.ToString()) is JsonObject && !output.ToString().Contains('\u001b'), command.Key + output);
        if (!command.ReadOnly && command.DryRun && !arguments.Contains("--help"))
        {
            output.GetStringBuilder().Clear(); mutations = 0;
            await Cli.RunAsync(arguments.Concat(new[] { "--dry-run" }).Distinct().ToArray(), Host).WaitAsync(TimeSpan.FromSeconds(3));
            Assert(mutations == 0 && JsonNode.Parse(output.ToString()) is JsonObject, "Dry run mutation: " + command.Key);
        }
    }
    finally { Console.SetOut(oldOut); Console.SetError(oldErr); Console.SetIn(oldIn); if (needsFile) File.Delete(fixture); }
});

Console.WriteLine($"CLI TEST PASS: {passed}"); return 0;
