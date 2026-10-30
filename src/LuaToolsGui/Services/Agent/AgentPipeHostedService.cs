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
        void OnLog(string json) => commands.Events.Publish("activity-log", "entry", System.Text.Json.Nodes.JsonNode.Parse(json));
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            commands.BindHost(stoppingToken);
            AppEventLog.Shared.EntryWritten += OnLog;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var discovery = new Discovery(Protocol.Version, commands.InstanceId, _pipe, process.Id, process.StartTime.ToUniversalTime().Ticks, process.SessionId,
                Convert.ToBase64String(LocalDiscovery.Protect(_capability)), LocalDiscovery.IsElevated(), commands.SteamIdentity);
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
        catch (Exception ex) { log.LogWarning("Agent pipe unavailable ({Type}): {Reason}", ex.GetType().Name, LogRedactor.Redact(ex.Message)); }
        finally
        {
            AppEventLog.Shared.EntryWritten -= OnLog;
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
            // Authenticate before accepting any secret, upload bytes or scratch files.
            if (!Authenticate(request))
            {
                await Protocol.WriteAsync(pipe, Envelope.Fail("PIPE_IDENTITY_DENIED", 5, Resources.Strings.Agent_IdentityDenied), Protocol.MaxFrame, deadline.Token);
                return;
            }
            if (request.Flags.ContainsKey("secret-stdin") || request.Flags.ContainsKey("code-stdin"))
            {
                // Dedicated secret input is never part of request flags or receipts.
                var secret = await Protocol.ReadAsync<SecretFrame>(pipe, 8192, deadline.Token).ConfigureAwait(false);
                if (secret.Value.Length > 4096) return;
                request = request with { Secret = secret.Value };
            }
            using var upload = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            upload.CancelAfter(TimeSpan.FromSeconds(30));
            using var staging = await AgentInputStaging.ReceiveAsync(pipe, request, upload.Token);
            // Framing/identity is bounded separately from authenticated execution and reply writing.
            Envelope reply;
            using (var execution = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
            {
                execution.CancelAfter(RequestDeadlines.For(request));
                try { reply = await HandleAsync(request, execution.Token).WaitAsync(execution.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
                { reply = Envelope.Fail("TIMEOUT", 7, Resources.Strings.Agent_Timeout); }
            }
            using var write = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            write.CancelAfter(TimeSpan.FromSeconds(5));
            try { await Protocol.WriteAsync(pipe, reply, Protocol.MaxFrame, write.Token).ConfigureAwait(false); }
            finally { commands.AfterReply(request); }
            if (reply.Ok && request.Command is "activity-log watch" or "download watch")
            {
                using var watch = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                watch.CancelAfter(CommandParser.Duration(request.Flags.GetValueOrDefault("timeout") ?? "24h") ?? TimeSpan.FromHours(24));
                string category = request.Command.Split(' ')[0];
                await foreach (var frame in commands.Events.Watch(request.Flags.GetValueOrDefault("cursor"), watch.Token))
                {
                    string? kind = frame.Data?["event"]?["kind"]?.ToString();
                    if (frame.Ok && kind is not ("GAP" or "heartbeat") && frame.Data?["event"]?["category"]?.ToString() != category) continue;
                    using var frameWrite = CancellationTokenSource.CreateLinkedTokenSource(watch.Token);
                    frameWrite.CancelAfter(TimeSpan.FromSeconds(5));
                    await Protocol.WriteAsync(pipe, frame, Protocol.MaxFrame, frameWrite.Token).ConfigureAwait(false);
                    if (!frame.Ok || kind == "GAP") break;
                }
            }

        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* Unauthenticated/malformed clients receive no information. Deadlines release the slot. */ }
        catch (Exception ex) { log.LogWarning("Agent request failed ({Type})", ex.GetType().Name); }
    }
    private bool Authenticate(AgentRequest request)
    {
        byte[] supplied;
        try { supplied = Convert.FromBase64String(request.Capability); } catch (FormatException) { return false; }
        try { return CryptographicOperations.FixedTimeEquals(supplied, _capability); }
        finally { CryptographicOperations.ZeroMemory(supplied); }
    }
    internal async Task<Envelope> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        if (!Authenticate(request)) return Envelope.Fail("PIPE_IDENTITY_DENIED", 5, Resources.Strings.Agent_IdentityDenied);
        return await commands.ExecuteAsync(request, ct).ConfigureAwait(false);
    }
    private bool Authenticate(AgentRequest request)
    {
        byte[] supplied;
        try { supplied = Convert.FromBase64String(request.Capability); }
        catch (FormatException) { return false; }
        bool allowed = CryptographicOperations.FixedTimeEquals(supplied, _capability);
        CryptographicOperations.ZeroMemory(supplied);
        return allowed;
    }
}
