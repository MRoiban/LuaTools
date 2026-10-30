using System.Text;
using System.Text.Json;
using LuaTools.Agent;
namespace LuaToolsCli;

/// <summary>Bounded JSON frames, never shell text. Secret frames are one-use and have no response payload.</summary>
public static class StdioSession
{
    public sealed record Frame(string Type, string Id, string[]? Args = null, string? Value = null);
    public static async Task<int> RunAsync(Parsed session, TextReader input, TextWriter output,
        Func<Parsed, CancellationToken, Task<Envelope>> transport, CancellationToken ct,
        Func<Parsed, CancellationToken, Action<Envelope>, Task<Envelope>>? streamingTransport = null)
    {
        string? secret = null, secretId = null;
        while (await ReadLineAsync(input, ct) is { } line)
        {
            Frame? frame;
            try { frame = JsonSerializer.Deserialize<Frame>(line, Protocol.Json); }
            catch (JsonException) { await WriteAsync(output, null, Envelope.Fail("INVALID_USAGE", 2, "Invalid session frame.")); continue; }
            if (frame is null || string.IsNullOrEmpty(frame.Id) || frame.Id.Length > 128) { await WriteAsync(output, null, Envelope.Fail("INVALID_USAGE", 2, "Invalid session frame.")); continue; }
            if (frame.Type == "secret")
            {
                if (frame.Args is not null || frame.Value is null || frame.Value.Length > 4096 || secret is not null)
                { secret = secretId = null; await WriteAsync(output, frame.Id, Envelope.Fail("INVALID_USAGE", 2, "Invalid secret frame.")); }
                else { secret = frame.Value; secretId = frame.Id; }
                continue;
            }
            var args = frame.Args ?? [];
            var parsed = CommandParser.Parse(args);
            Envelope result;
            string? supplied = secretId == frame.Id ? secret : null;
            secret = secretId = null;
            if (frame.Type != "command" || frame.Value is not null || parsed.Has("secret-stdin") || parsed.Has("code-stdin") || parsed.Has("ids-from") || parsed.Value("data") is "-" || parsed.Value("data")?.StartsWith('@') == true)
                result = Envelope.Fail("INVALID_USAGE", 2, "Session commands cannot read stdin or files; use a dedicated secret frame.");
            else
            {
                var flags = new Dictionary<string, string?>(parsed.Flags) { ["no-input"] = null };
                // Authority only narrows for the lifetime of this adapter. A command cannot clear this flag.
                foreach (var key in new[] { "read-only", "no-start", "expect-instance", "expect-steam" })
                    if (session.Has(key)) flags[key] = session.Value(key);
                if (supplied is not null)
                {
                    if (parsed.Command?.Key == "account login") flags["code-stdin"] = null;
                    else if (parsed.Command?.Key is "hubcap-key update" or "cabinet-cloud connect") flags["secret-stdin"] = null;
                    else { await WriteAsync(output, frame.Id, Envelope.Fail("INVALID_USAGE", 2, "This command does not accept a secret.")); continue; }
                    parsed = CommandParser.Parse(args.Concat(new[] { flags.ContainsKey("code-stdin") ? "--code-stdin" : "--secret-stdin" }).ToArray());
                }
                parsed = parsed with { Flags = flags, Secret = supplied };
                if (parsed.Problems.Length != 0) result = Envelope.Fail("INVALID_USAGE", 2, "Invalid command arguments.", problems: parsed.Problems);
                else if (parsed.Has("read-only") && !parsed.Command!.ReadOnly) result = Envelope.Fail("READ_ONLY", 5, "This command changes state.");
                else if (parsed.Command!.Status != "enabled" || parsed.Command.Key == "session") result = Envelope.Fail("NOT_IMPLEMENTED", 5, "This command is unavailable in a sequential session.");
                else if (parsed.Has("dry-run") && !parsed.Command.DryRun) result = Envelope.Fail("DRY_RUN_UNSUPPORTED", 5, "Planning is unavailable.");
                else if (parsed.Command.Key == "commands") result = Envelope.Success(CatalogueDocument.Create(Catalogue.Commands, parsed.Has("brief")));
                else if (parsed.Command.Key == "describe") result = Envelope.Success(CatalogueDocument.Create(Catalogue.Commands.Where(c => c.Resource == parsed.Arguments[0])));
                else if (parsed.Command.Key == "help") result = Envelope.Success(new { help = Rendering.Help(resource: parsed.Arguments.FirstOrDefault()) });
                else if (parsed.Command.Key == "version") result = Envelope.Success(new { version = typeof(Program).Assembly.GetName().Version?.ToString(), protocol = Protocol.Version });
                else
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(parsed.Timeout);
                    try
                    {
                        result = parsed.Command.Verb == "watch" && streamingTransport is not null
                            ? await streamingTransport(parsed, deadline.Token, value => output.WriteLine(JsonSerializer.Serialize(new { id = frame.Id, result = value }, Protocol.Json)))
                            : await transport(parsed, deadline.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result = Envelope.Fail("TIMEOUT", 7, "Call deadline exceeded."); }
                }
                result = Results.Project(result, parsed.Value("fields"));
            }
            await WriteAsync(output, frame.Id, result);
        }
        return 0;
    }
    private static Task WriteAsync(TextWriter writer, string? id, Envelope result) => writer.WriteLineAsync(JsonSerializer.Serialize(new { id, result }, Protocol.Json));
    private static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken ct)
    {
        var line = new StringBuilder(); char[] one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), ct) != 0)
        {
            if (one[0] == '\n') return line.ToString();
            if (line.Length >= Protocol.MaxRequest) throw new InvalidDataException("Session frame exceeds limit.");
            line.Append(one[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }
}
