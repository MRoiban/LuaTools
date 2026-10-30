using System.Diagnostics;
using System.Text;
using LuaTools.Agent;

namespace LuaToolsCli;

public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args, (parsed, ct) => new AgentClient().InvokeAsync(parsed, ct));
    public static async Task<int> RunAsync(string[] args, Func<Parsed, CancellationToken, Task<Envelope>> transport)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        bool tty = !Console.IsOutputRedirected;
        var parsed = CommandParser.Parse(args);
        if (Console.IsInputRedirected || !tty) parsed.Flags["no-input"] = null;
        var clock = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(parsed.Timeout);
        bool interrupted = false;
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; interrupted = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        Envelope result;
        try
        {
            if (parsed.Problems.Length > 0) result = Envelope.Fail("INVALID_USAGE", 2, "Invalid command arguments.", problems: parsed.Problems);
            else if (parsed.Has("help") || parsed.Command!.Key == "help")
            {
                Command? target = parsed.Has("help") ? parsed.Command!.Key == "help" ? null : parsed.Command : parsed.Arguments.Length > 0 ? Catalogue.Find(string.Join(' ', parsed.Arguments)) : null;
                string? resource = target is null ? parsed.Arguments.FirstOrDefault() : null;
                string help = Rendering.Help(target, resource);
                if (!parsed.Has("json") && parsed.Value("format") is not ("json" or "jsonl")) { Console.WriteLine(help); return 0; }
                result = Envelope.Success(new { help });
            }
            else if (parsed.Command.Key == "version") result = Envelope.Success(new { version = typeof(Program).Assembly.GetName().Version?.ToString(), protocol = Protocol.Version });
            else if (parsed.Command.Key == "commands") result = Envelope.Success(new { version = Protocol.Version, commands = Catalogue.Commands }, null, new Warning("RUNTIME_NOT_QUERIED", "Compiled catalogue; query ping for authenticated runtime availability.", new { runtime_available = false }));
            else if (parsed.Command.Key == "describe")
            {
                var commands = Catalogue.Commands.Where(c => c.Resource == parsed.Arguments[0]).ToArray();
                result = commands.Length == 0 ? Envelope.Fail("NOT_FOUND", 3, "Unknown resource.") : Envelope.Success(new { resource = parsed.Arguments[0], commands, settings = parsed.Arguments[0] == "setting" ? SettingSchema.All : null }, null, new Warning("RUNTIME_NOT_QUERIED", "Compiled schema; runtime_available:false."));
            }
            else if (parsed.Command.Key == "completion")
            {
                string script = Rendering.Completion(parsed.Arguments[0]);
                if (parsed.Format(tty) is "text" or "table") { Console.WriteLine(script); return 0; }
                result = Envelope.Success(new { shell = parsed.Arguments[0], script });
            }
            else if (parsed.Has("read-only") && !parsed.Command.ReadOnly) result = Envelope.Fail("READ_ONLY", 5, "This command changes state.");
            else if (parsed.Command.Status != "enabled" || parsed.Has("fix")) result = Envelope.Fail("NOT_IMPLEMENTED", 5, "This action is not implemented in this release.");
            else
            {
                parsed = await CommandInput.LoadAsync(parsed, cancellation.Token);
                result = parsed.Problems.Length > 0 ? Envelope.Fail("INVALID_USAGE", 2, "Invalid input data.", problems: parsed.Problems) : await transport(parsed, cancellation.Token);
            }
            result = Results.Project(result, parsed.Value("fields"));
        }
        catch (OperationCanceledException) { result = interrupted ? Envelope.Fail("INTERRUPTED", 130, "Interrupted.") : Envelope.Fail("TIMEOUT", 7, "Call deadline exceeded.") with
        { Error = new("TIMEOUT", 7, "Call deadline exceeded.", Retryable: true, Details: Protocol.Node(new { job_id = parsed.Command?.Key == "job wait" ? parsed.Arguments.FirstOrDefault() : null, outcome_unknown = parsed.Command?.ReadOnly == false })) }; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { result = Envelope.Fail("TRANSPORT_FAILURE", 7, "Invalid or unavailable local pipe response."); }
        catch (Exception) { result = Envelope.Fail("CLIENT_FAILURE", 1, "Unexpected client failure."); }
        finally { Console.CancelKeyPress -= handler; }
        if (!result.Ok) Console.Error.WriteLine(result.Error!.Code + ": " + result.Error.Message);
        if (parsed.Has("verbose")) Console.Error.WriteLine($"luatools: {clock.ElapsedMilliseconds}ms; protocol {Protocol.Version}");
        Console.WriteLine(Rendering.Render(result, parsed.Format(tty)));
        return result.Error?.ExitCode ?? 0;
    }
}
