using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using LuaTools.Agent;

namespace LuaToolsCli;

public sealed class AgentClient
{
    public async Task<Envelope> InvokeAsync(Parsed parsed, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Unavailable();
        if (parsed.Command?.Key == "application exit" && parsed.Has("drain") && !parsed.Has("dry-run"))
        {
            while (true)
            {
                var status = await InvokeAsync(new(Catalogue.Find("status"), [], new() { ["no-start"] = null }, []), ct);
                if (!status.Ok) return status;
                if (status.Data?["queue"]?["active"]?.GetValue<int>() == 0 && status.Data?["queue"]?["operations"]?.GetValue<int>() == 0 && status.Data?["helpers"]?.GetValue<int>() == 0) break;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        bool started = false;
        string? expectedSteam = parsed.Value("expect-steam"), expectedInstance = null;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            bool connected = false;
            try
            {
                var record = LocalDiscovery.Read<Discovery>(LocalDiscovery.Record);
                if (record.Protocol != Protocol.Version) return Envelope.Fail("PROTOCOL_MISMATCH", 6, "The running LuaTools uses a different agent protocol. Use its bundled CLI.");
                LocalDiscovery.VerifyProcess(record);
                await using var pipe = new NamedPipeClientStream(".", record.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(250, ct).ConfigureAwait(false);
                LocalDiscovery.VerifyServer(pipe, record);
                connected = true;
                byte[] capability = LocalDiscovery.Protect(Convert.FromBase64String(record.Capability), true);
                try
                {
                    if (!parsed.Command!.ReadOnly && expectedSteam is null)
                    {
                        var hello = new AgentRequest(Protocol.Version, Convert.ToBase64String(capability), record.InstanceId, Guid.NewGuid().ToString("N"), "ping", [], []);
                        await Protocol.WriteAsync(pipe, hello, Protocol.MaxRequest, ct).ConfigureAwait(false);
                        var context = await Protocol.ReadAsync<Envelope>(pipe, Protocol.MaxFrame, ct).ConfigureAwait(false);
                        if (!context.Ok) return context;
                        expectedSteam = context.Data?["steam_identity"]?.ToString(); expectedInstance = record.InstanceId;
                        if (expectedSteam is null) return Envelope.Fail("PROTOCOL_MISMATCH", 6, "The owner did not advertise mutation context.");
                        continue;
                    }
                    var request = new AgentRequest(Protocol.Version, Convert.ToBase64String(capability), expectedInstance ?? record.InstanceId, Guid.NewGuid().ToString("N"), parsed.Command!.Key, parsed.Arguments, parsed.Flags, expectedSteam, typeof(AgentClient).Assembly.GetName().Version?.ToString() ?? "unknown");
                    await Protocol.WriteAsync(pipe, request, Protocol.MaxRequest, ct).ConfigureAwait(false);
                    var reply = await Protocol.ReadAsync<Envelope>(pipe, Protocol.MaxFrame, ct).ConfigureAwait(false);
                    if (parsed.Command.Key == "job wait" && reply.Ok && reply.Data?["state"]?.ToString() is { } state)
                    {
                        if (state == "awaiting_confirmation") return Envelope.Fail("CONSENT_REQUIRED", 8, "The job needs a decision.");
                        if (state is "failed" or "cancelled") return Envelope.Fail(state == "failed" ? "JOB_FAILED" : "JOB_CANCELLED", 1, "The job did not complete successfully.");
                        if (state != "completed") { await Task.Delay(100, ct).ConfigureAwait(false); continue; }
                    }
                    return reply;
                }
                finally { CryptographicOperations.ZeroMemory(capability); }
            }
            catch (ElevationMismatchException ex) { return Envelope.Fail("ELEVATION_MISMATCH", 5, ex.Message); }
            catch (CryptographicException) { return Envelope.Fail("PIPE_IDENTITY_DENIED", 5, "The local discovery capability is invalid."); }
            catch (FormatException) { return Envelope.Fail("PIPE_IDENTITY_DENIED", 5, "The local discovery capability is invalid."); }
            catch (UnauthorizedAccessException) { return Envelope.Fail("PIPE_IDENTITY_DENIED", 5, "Local owner, session or elevation mismatch. Run LuaTools and the agent at matching elevation in the same Windows session."); }
            catch (Exception ex) when (ex is IOException or TimeoutException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (connected) return Envelope.Fail("TRANSPORT_FAILURE", 7, "Invalid or unavailable local pipe response.");
                if (parsed.Has("no-start")) return Unavailable();
                if (!started && !LocalDiscovery.OwnerRunning())
                {
                    try
                    {
                        var launcher = LocalDiscovery.Read<LauncherRecord>(LocalDiscovery.Launcher);
                        if (!Path.IsPathFullyQualified(launcher.Executable) || !Path.GetFileName(launcher.Executable).Equals("LuaTools.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(launcher.Executable)) return Unavailable();
                        WindowsGuiLauncher.Start(launcher.Executable);
                        started = true;
                    }
                    catch (Exception startError) when (startError is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException) { return Unavailable(); }
                }
                if (watch.Elapsed > TimeSpan.FromSeconds(25)) return Unavailable();
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }
    public static Envelope Unavailable() => Envelope.Fail("GUI_NOT_RUNNING", 9, "LuaTools could not be started or discovered.", "Launch the installed LuaTools.exe once. Expected launcher: %LOCALAPPDATA%\\LuaToolsGui-SecurityHardened\\bin\\gui-path.json; retry without --no-start.");

    // Transport-only seam used by Linux fake-host tests; production discovery never accepts a host override.
    public static async Task<Envelope> ExchangeAsync(string pipeName, AgentRequest request, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(250, ct);
        await Protocol.WriteAsync(pipe, request, Protocol.MaxRequest, ct);
        return await Protocol.ReadAsync<Envelope>(pipe, Protocol.MaxFrame, ct);
    }
}
