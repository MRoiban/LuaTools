using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuaTools.Agent;
using Microsoft.Win32;
using Xunit;
namespace LuaToolsGui.Tests;

// Changes HKCU PATH temporarily. Keep all real-process tests in one nonparallel collection.
[CollectionDefinition("AgentProcess", DisableParallelization = true)]
public sealed class AgentProcessCollection;
[Collection("AgentProcess")]
public sealed class AgentProcessTests
{
    [Fact]
    public async Task PublishedAppAndCopiedClientWorkFromAnIsolatedProfile()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "LuaToolsGui.sln"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;
        string scratch = Path.Combine(Path.GetTempPath(), "agent-process-" + Guid.NewGuid().ToString("N"));
        string publish = Path.Combine(scratch, "source"), moved = Path.Combine(scratch, "updated-source");
        string roaming = Path.Combine(scratch, "roaming"), local = Path.Combine(scratch, "local");
        string bin = Path.Combine(local, "LuaToolsGui-SecurityHardened", "bin");
        string recordPath = Path.Combine(bin, "agent-v1.json");
        var children = new List<Process>();
        using var environment = Registry.CurrentUser.CreateSubKey("Environment", true);
        object? savedPath = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        RegistryValueKind savedKind = savedPath is null ? RegistryValueKind.ExpandString : environment.GetValueKind("Path");
        ProcessStartInfo Start(string exe, params string[] arguments)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = root };
            start.Environment["APPDATA"] = roaming; start.Environment["LOCALAPPDATA"] = local;
            start.Environment["PATH"] = bin + ";" + start.Environment["PATH"];
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            return start;
        }
        Process Spawn(ProcessStartInfo start) { var p = Process.Start(start)!; children.Add(p); p.StandardInput.Close(); return p; }
        async Task<(int Exit, string Output)> Run(ProcessStartInfo start, int seconds = 45)
        {
            var p = Spawn(start); var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds));
            return (p.ExitCode, await output + (p.ExitCode == 0 ? "" : await error));
        }
        async Task<JsonNode> Cli(params string[] args)
        {
            var response = await Run(Start(Path.Combine(bin, "luatools.exe"), args.Concat(new[] { "--json" }).ToArray()));
            Assert.True(response.Exit == 0, response.Output);
            return JsonNode.Parse(response.Output)!;
        }
        async Task<Discovery> Ready()
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(40))
            {
                if (File.Exists(recordPath) && File.Exists(Path.Combine(bin, "luatools.exe")))
                {
                    try { var value = JsonSerializer.Deserialize<Discovery>(File.ReadAllText(recordPath), Protocol.Json)!; using var proc = Process.GetProcessById(value.Pid); if (!proc.HasExited) return value; }
                    catch (IOException) { } catch (ArgumentException) { }
                }
                await Task.Delay(100);
            }
            var diagnostic = new List<string> { "Published GUI never advertised readiness." };
            foreach (var child in children)
            {
                try { diagnostic.Add($"PID {child.Id}: exited={child.HasExited}" + (child.HasExited ? $" code={child.ExitCode} stderr={await child.StandardError.ReadToEndAsync()} stdout={await child.StandardOutput.ReadToEndAsync()}" : "")); } catch { }
            }
            if (Directory.Exists(roaming)) foreach (string log in Directory.GetFiles(roaming, "*.jsonl", SearchOption.AllDirectories)) diagnostic.Add(string.Join('\n', File.ReadLines(log).TakeLast(12)));
            diagnostic.Add("bin files: " + (Directory.Exists(bin) ? string.Join(',', Directory.GetFiles(bin).Select(Path.GetFileName)) : "missing"));
            throw new TimeoutException(string.Join("\n", diagnostic));
        }
        async Task ExitOwner()
        {
            var record = await Ready(); using var p = Process.GetProcessById(record.Pid);
            await Cli("application", "exit", "--yes"); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        try
        {
            Directory.CreateDirectory(scratch);
            string state = Path.Combine(roaming, AppConfig.DataDirectoryName); Directory.CreateDirectory(state);
            // Never detect or operate on the runner's Steam installation.
            File.WriteAllText(Path.Combine(state, "settings.json"), JsonSerializer.Serialize(new { SteamPathOverride = Path.Combine(scratch, "no-steam"), AutoUpdateApps = false, CabinetEnabled = false, MinimizeToTray = true }));
            var built = await Run(Start("dotnet", "publish", Path.Combine(root, "src/LuaToolsGui/LuaToolsGui.csproj"), "-c", "Release", "--no-restore", "--self-contained", "false", "-o", publish), 120);
            Assert.True(built.Exit == 0, built.Output);
            // Normal first use installs the stable client and launcher. The next call starts from no owner.
            Spawn(Start(Path.Combine(publish, "LuaTools.exe"), "--minimized"));
            await Ready();
            var pathClock = Stopwatch.StartNew();
            while (!(environment.GetValue("Path") as string ?? "").Contains(bin, StringComparison.OrdinalIgnoreCase) && pathClock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(100);
            Assert.Contains(bin, environment.GetValue("Path") as string, StringComparison.OrdinalIgnoreCase);
            await ExitOwner();
            var ping = await Cli("ping"); Assert.True(ping["data"]!["ready"]!.GetValue<bool>()); Assert.True(ping["data"]!["minimized"]!.GetValue<bool>());
            Assert.Equal(LocalDiscovery.IsElevated(), ping["data"]!["elevated"]!.GetValue<bool>());
            var owner = await Ready(); children.Add(Process.GetProcessById(owner.Pid));
            foreach (var args in new[] { new[] { "status" }, ["doctor"], ["setting", "list"], ["download", "list"], ["activity-log", "list"] }) await Cli(args);
            await Task.WhenAll(Cli("status"), Cli("ping"));
            // Production CLI, production pipe, live settings gate, no direct state-file access by client.
            var dry = await Cli("setting", "update", "FastFetch", "--value", "true", "--dry-run");
            Assert.NotNull(dry["data"]!["plan"]);
            Assert.False((await Cli("setting", "get", "FastFetch"))["data"]!["value"]!.GetValue<bool>());
            await Cli("setting", "update", "FastFetch", "--value", "true", "--idempotency-key", "process-proof");
            await Cli("setting", "update", "FastFetch", "--value", "true", "--idempotency-key", "process-proof");
            Assert.True((await Cli("setting", "get", "FastFetch"))["data"]!["value"]!.GetValue<bool>());
            var denied = await Run(Start(Path.Combine(bin, "luatools.exe"), "setting", "update", "SteamlessEnabled", "--value", "true", "--json"));
            Assert.Equal(8, denied.Exit);
            await Cli("setting", "update", "SteamlessEnabled", "--value", "true", "--yes", "--actor-note", "process-proof");
            var log = await Cli("activity-log", "list", "--category", "AgentConsent");
            Assert.Contains("SteamlessEnabled", log.ToJsonString()); Assert.Contains("process-proof", log.ToJsonString());
            await Cli("setting", "update", "SteamlessEnabled", "--value", "false");
            foreach (byte[] bytes in new[] { new byte[] { 1, 0, 0, 0, 123 }, Size(Protocol.MaxRequest + 1) })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var pipe = new NamedPipeClientStream(".", owner.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(deadline.Token); await pipe.WriteAsync(bytes, deadline.Token);
                Assert.Equal(0, await pipe.ReadAsync(new byte[1], deadline.Token));
                await Cli("ping");
            }
            // A real process with a forged opposite-elevation descriptor must fail before any launch.
            string original = File.ReadAllText(recordPath);
            File.WriteAllText(recordPath, JsonSerializer.Serialize(owner with { Elevated = !owner.Elevated }, Protocol.Json));
            var mismatch = await Run(Start(Path.Combine(bin, "luatools.exe"), "ping", "--json"));
            Assert.Equal(5, mismatch.Exit); Assert.Contains("ELEVATION_MISMATCH", mismatch.Output);
            File.WriteAllText(recordPath, original);
            await ExitOwner();
            var absent = await Run(Start(Path.Combine(bin, "luatools.exe"), "ping", "--no-start", "--json"));
            Assert.Equal(9, absent.Exit); Assert.Contains("GUI_NOT_RUNNING", absent.Output);
            // Retain an ACL-protected stale descriptor and move the entire original publish folder.
            File.WriteAllText(recordPath, JsonSerializer.Serialize(owner, Protocol.Json));
            Directory.Move(publish, moved);
            // Normal first startup from the updated location refreshes only launcher/client distribution state.
            Spawn(Start(Path.Combine(moved, "LuaTools.exe"), "--minimized")); await Ready(); await ExitOwner();
            Assert.False(Directory.Exists(publish));
            File.WriteAllText(recordPath, JsonSerializer.Serialize(owner, Protocol.Json));
            var recovered = await Cli("ping"); Assert.True(recovered["data"]!["ready"]!.GetValue<bool>());
            children.Add(Process.GetProcessById((await Ready()).Pid));
            await ExitOwner();
        }
        finally
        {
            // Discovery catches auto-started owners even when an assertion failed before tracking them.
            if (File.Exists(recordPath))
            {
                try { var record = JsonSerializer.Deserialize<Discovery>(File.ReadAllText(recordPath), Protocol.Json)!; var p = Process.GetProcessById(record.Pid); if (p.StartTime.ToUniversalTime().Ticks == record.StartTime && p.MainModule?.FileName?.StartsWith(scratch, StringComparison.OrdinalIgnoreCase) == true) children.Add(p); }
                catch { }
            }
            foreach (var candidate in Process.GetProcessesByName("LuaTools"))
            {
                try { if (candidate.MainModule?.FileName?.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true) children.Add(candidate); else candidate.Dispose(); }
                catch { candidate.Dispose(); }
            }
            foreach (var child in children) { try { if (!child.HasExited) { child.Kill(true); child.WaitForExit(10000); } } catch { } child.Dispose(); }
            if (savedPath is null) environment.DeleteValue("Path", false); else environment.SetValue("Path", savedPath, savedKind);
            try { Directory.Delete(scratch, true); } catch { }
        }
    }
    private static byte[] Size(int length) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length); return bytes; }
}
