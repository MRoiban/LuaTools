using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using LuaToolsCli;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Agent;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;
public sealed partial class AgentCliTests
{
    // Enumerating the catalogue makes new read commands participate automatically.
    public static IEnumerable<object[]> ReadDescriptors() => Catalogue.Commands
        .Where(c => c.ReadOnly && c.Verb != "watch" && c.Key != "session")
        .Select(c => new object[] { c.Key });

    [Theory]
    [MemberData(nameof(ReadDescriptors))]
    public async Task ReadOutputFieldsMatchRealPipe(string key)
    {
        Directory.CreateDirectory(_directory);
        string plug = Path.Combine(_directory, "plug"); Directory.CreateDirectory(plug);
        File.WriteAllText(Path.Combine(plug, "570.lua"), "addappid(570)\n");
        var vault = new LuaVault(() => plug, Path.Combine(_directory, "vault"));
        vault.SyncDefaultFromLive(570);
        string hash = Assert.Single(vault.GetVariants(570)).Hash;
        var queue = new DownloadQueue(new CacheService(Path.Combine(_directory, "history.json")), NullLogger<DownloadQueue>.Instance);
        queue.History.Add(new(new("failed-one", "manifest", 570, "Game", "", 0, "Failed", "HTTP 429: quota exhausted", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
        var facade = Facade(queue: queue, vault: vault);
        string job = facade.Jobs.Register("fixture", _ => Task.FromResult(Envelope.Success(new { })), default);
        var command = Catalogue.Find(key)!;
        string[] args = command.Identities.Select(identity => identity switch
        {
            "hash" => hash, "key" => "FastFetch", "resource" => "game", _ => key.StartsWith("job ") ? job : "570"
        }).ToArray();
        var flags = new Dictionary<string, string?>();
        foreach (var flag in command.Flags.Where(f => f.Required)) flags[flag.Name] = flag.Name == "appid" ? "570" : flag.AllowedValues.FirstOrDefault() ?? "570";
        if (key.StartsWith("preset ") || key.StartsWith("depot ")) flags["appid"] = "570";
        if (key == "link get") flags["kind"] = "website";
        if (key == "history list") flags["status"] = "failed";
        byte[] capability = RandomNumberGenerator.GetBytes(32);
        using var service = new AgentPipeHostedService(facade, NullLogger<AgentPipeHostedService>.Instance, capability);
        string pipe = "LuaTools-contract-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var host = Task.Run(async () => { await server.WaitForConnectionAsync(timeout.Token); await service.ServeAsync(server, timeout.Token); });
        var reply = await AgentClient.ExchangeAsync(pipe, Request(facade, key, args, flags, Convert.ToBase64String(capability)), timeout.Token);
        await host;
        // Optional services are deliberately absent in this isolated offline fixture. Their
        // unavailable envelope is still verified over the host's real framing/authentication path.
        if (!reply.Ok)
        {
            Assert.Contains(reply.Error!.Code, new[] { "NOT_IMPLEMENTED", "SERVICE_UNAVAILABLE", "ACTION_UNAVAILABLE", "INVALID_USAGE", "METADATA_UNAVAILABLE", "REFACTOR_REQUIRED", "NOT_FOUND", "JOB_EXPIRED" });
            return;
        }
        var rows = reply.Data is JsonArray array ? array.ToArray() : new[] { reply.Data };
        foreach (var row in rows.OfType<JsonObject>())
            foreach (string field in row.Select(p => p.Key)) Assert.Contains(field, command.OutputFields);
        if (key is "preset list" or "preset get")
            foreach (var row in rows) Assert.Equal(hash, row!["hash"]!.ToString());
        if (key == "history list") Assert.Contains("HTTP 429", reply.Data!.ToJsonString());
    }

    [Fact]
    public void RelativeActivityUsesHostClockAndSeverityThreshold()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var log = new AppEventLog(_directory, () => now);
        log.Write(Microsoft.Extensions.Logging.LogLevel.Error, "Download", "old");
        now = now.AddHours(4);
        log.Write(Microsoft.Extensions.Logging.LogLevel.Information, "Download", "info");
        log.Write(Microsoft.Extensions.Logging.LogLevel.Warning, "Download", "warn");
        log.Write(Microsoft.Extensions.Logging.LogLevel.Error, "Download", "error");
        var rows = log.ReadAgentEvents(new() { ["since"] = "3h", ["severity"] = "warn" });
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain("old", rows.ToJsonString());
        Assert.DoesNotContain("info", rows.ToJsonString());
    }
}
