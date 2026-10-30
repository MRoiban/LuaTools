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
        if (LocalDiscovery.IsElevated()) return Envelope.Fail("PIPE_IDENTITY_DENIED", 5, "Run the agent and LuaTools unelevated in the same Windows session.");
        bool started = false;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            bool connected = false;
            try
            {
                var record = LocalDiscovery.Read<Discovery>(LocalDiscovery.Record);
                LocalDiscovery.VerifyProcess(record);
                await using var pipe = new NamedPipeClientStream(".", record.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(250, ct).ConfigureAwait(false);
                LocalDiscovery.VerifyServer(pipe, record);
                connected = true;
                byte[] capability = LocalDiscovery.Protect(Convert.FromBase64String(record.Capability), true);
                try
                {
                    var request = new AgentRequest(Protocol.Version, Convert.ToBase64String(capability), record.InstanceId, Guid.NewGuid().ToString("N"), parsed.Command!.Key, parsed.Arguments, parsed.Flags, parsed.Value("expect-steam"), typeof(AgentClient).Assembly.GetName().Version?.ToString() ?? "unknown");
                    await Protocol.WriteAsync(pipe, request, Protocol.MaxRequest, ct).ConfigureAwait(false);
                    return await Protocol.ReadAsync<Envelope>(pipe, Protocol.MaxFrame, ct).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(capability); }
            }
            catch (UnauthorizedAccessException) { return Envelope.Fail("PIPE_IDENTITY_DENIED", 5, "Local owner, session or elevation mismatch. Run LuaTools and the agent unelevated in the same Windows session."); }
            catch (Exception ex) when (ex is IOException or TimeoutException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or CryptographicException or System.Text.Json.JsonException or FormatException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (connected) return Envelope.Fail("TRANSPORT_FAILURE", 7, "Invalid or unavailable local pipe response.");
                if (parsed.Has("no-start")) return Unavailable();
                if (!started)
                {
                    try
                    {
                        var launcher = LocalDiscovery.Read<LauncherRecord>(LocalDiscovery.Launcher);
                        if (!Path.IsPathFullyQualified(launcher.Executable) || !Path.GetFileName(launcher.Executable).Equals("LuaTools.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(launcher.Executable)) return Unavailable();
                        Process.Start(new ProcessStartInfo(launcher.Executable) { UseShellExecute = false, Arguments = "--minimized", WorkingDirectory = Path.GetDirectoryName(launcher.Executable)! });
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
