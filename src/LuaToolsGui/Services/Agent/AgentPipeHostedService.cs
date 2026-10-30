using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using LuaTools.Agent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services.Agent;

/// <summary>Always-on local transport; each connection has an independent deadline and cancellation.</summary>
public sealed class AgentPipeHostedService(AgentCommandService commands, ILogger<AgentPipeHostedService> log) : BackgroundService
{
    private const int Connections = 8;
    private readonly SemaphoreSlim _slots = new(Connections);
    private readonly byte[] _capability = RandomNumberGenerator.GetBytes(32);
    private readonly string _pipe = "LuaTools.Agent.v1." + Guid.NewGuid().ToString("N");
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);
    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || LocalDiscovery.IsElevated()) { log.LogWarning("Agent pipe requires an unelevated Windows owner"); return; }
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var discovery = new Discovery(Protocol.Version, commands.InstanceId, _pipe, process.Id, process.StartTime.ToUniversalTime().Ticks, process.SessionId,
                Convert.ToBase64String(LocalDiscovery.Protect(_capability)));
            // Create a listener before advertising readiness. Publication is off the UI thread.
            var listener = CreatePipe();
            try { LocalDiscovery.Write(LocalDiscovery.Launcher, new LauncherRecord(Environment.ProcessPath!)); LocalDiscovery.Write(LocalDiscovery.Record, discovery); }
            catch { listener.Dispose(); throw; }
            var active = new List<Task>();
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await _slots.WaitAsync(stoppingToken).ConfigureAwait(false);
                    try { await listener.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false); }
                    catch { _slots.Release(); throw; }
                    var connected = listener;
                    active.RemoveAll(t => t.IsCompleted);
                    active.Add(RunConnectionAsync(connected, stoppingToken));
                    listener = CreatePipe();
                }
            }
            finally { listener.Dispose(); await Task.WhenAll(active).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { log.LogWarning("Agent pipe unavailable ({Type})", ex.GetType().Name); }
        finally
        {
            try
            {
                if (File.Exists(LocalDiscovery.Record) && LocalDiscovery.Read<Discovery>(LocalDiscovery.Record).InstanceId == commands.InstanceId) File.Delete(LocalDiscovery.Record);
            }
            catch { /* a failed CLI integration must not affect GUI shutdown */ }
            CryptographicOperations.ZeroMemory(_capability);
        }
    }
    private NamedPipeServerStream CreatePipe() => new(_pipe, PipeDirection.InOut, Connections + 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);

    internal AgentPipeHostedService(AgentCommandService commands, ILogger<AgentPipeHostedService> log, byte[] capability)
        : this(commands, log) { _capability = capability.ToArray(); }
    private async Task RunConnectionAsync(NamedPipeServerStream pipe, CancellationToken shutdown)
    {
        try { await ServeAsync(pipe, shutdown).ConfigureAwait(false); }
        finally { _slots.Release(); }
    }
    internal async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken shutdown)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var owned = pipe;
        try
        {
            LocalDiscovery.VerifyClient(pipe);
            var request = await Protocol.ReadAsync<AgentRequest>(pipe, Protocol.MaxRequest, deadline.Token).ConfigureAwait(false);
            var reply = await HandleAsync(request, deadline.Token).ConfigureAwait(false);
            await Protocol.WriteAsync(pipe, reply, Protocol.MaxFrame, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* Unauthenticated/malformed clients receive no information. Deadlines release the slot. */ }
        catch (Exception ex) { log.LogWarning("Agent request failed ({Type})", ex.GetType().Name); }
    }
    internal async Task<Envelope> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        byte[] supplied;
        try { supplied = Convert.FromBase64String(request.Capability); }
        catch (FormatException) { return Envelope.Fail("PIPE_IDENTITY_DENIED", 5, Resources.Strings.Agent_IdentityDenied); }
        bool allowed = CryptographicOperations.FixedTimeEquals(supplied, _capability);
        CryptographicOperations.ZeroMemory(supplied);
        if (!allowed) return Envelope.Fail("PIPE_IDENTITY_DENIED", 5, Resources.Strings.Agent_IdentityDenied);
        return await commands.ExecuteAsync(request, ct).ConfigureAwait(false);
    }
}
