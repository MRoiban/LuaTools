using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CabinetEngine;

namespace LuaToolsGui.Services.GameTools;

/// <summary>
/// Starts the hidden helper processes that talk to Steam over IPC, so steamclient64.dll is never loaded into
/// the GUI process and Steam sees the game's own AppId: <c>--game-stats</c> (one live edit) and Cabinet's
/// <c>--schema</c> (ask Steam to download a missing achievement list). Both use Cabinet's helper contract:
/// parent pid + start time (the helper exits when this process does) and a named stop event.
/// </summary>
internal static class GameStatsHelperClient
{
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan SchemaTimeout = TimeSpan.FromSeconds(15);

    internal static string BuildRequest(GameStatsEdit edit, GameStatsSchema schema)
    {
        var floats = schema.Stats.GroupBy(s => s.ApiName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().IsFloat, StringComparer.Ordinal);
        var request = new GameStatsHelper.Request
        {
            Achievements = new Dictionary<string, bool>(edit.Achievements, StringComparer.Ordinal),
            Stats = edit.Stats.Where(s => floats.ContainsKey(s.Key)).ToDictionary(
                s => s.Key, s => new GameStatsHelper.StatValue { Value = s.Value, Float = floats[s.Key] }, StringComparer.Ordinal),
        };
        return JsonSerializer.Serialize(request, GameStatsHelper.Json);
    }

    /// <summary>Parse the helper's one-line answer; anything unreadable counts as a rejection.</summary>
    internal static StatsSaveResult ParseResponse(string? line)
    {
        GameStatsHelper.Response? response = null;
        try { if (!string.IsNullOrWhiteSpace(line)) response = JsonSerializer.Deserialize<GameStatsHelper.Response>(line, GameStatsHelper.Json); }
        catch (JsonException) { }
        if (response is null) return new(StatsSaveOutcome.LiveRejected, "helper-failed");
        if (response.Ok) return new(StatsSaveOutcome.SavedLive);
        string error = response.Error ?? "helper-failed";
        if (error == "refused" && response.Refused.Count > 0) error += ":" + string.Join(", ", response.Refused.Take(5));
        else if (error is "store-failed" or "stats-unavailable" && response.SteamResult != 0) error += ":" + response.SteamResult.ToString(CultureInfo.InvariantCulture);
        return new(StatsSaveOutcome.LiveRejected, error);
    }

    /// <summary>The helper's reply: the text after <see cref="GameStatsHelper.ReplyPrefix"/> on the first line
    /// carrying it. Other lines (steamclient's own console output) are skipped. Null at end of output.</summary>
    internal static async Task<string?> ReadReplyAsync(TextReader output, CancellationToken ct)
    {
        while (await output.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            int at = line.IndexOf(GameStatsHelper.ReplyPrefix, StringComparison.Ordinal);
            if (at >= 0) return line[(at + GameStatsHelper.ReplyPrefix.Length)..];
        }
        return null;
    }

    /// <summary>Live edit through the helper, bound to the Steam install at <paramref name="steamPath"/>.</summary>
    internal static async Task<StatsSaveResult> SaveLiveAsync(string steamPath, long appId, GameStatsEdit edit, GameStatsSchema schema, CancellationToken ct)
    {
        string request = BuildRequest(edit, schema);
        try
        {
            return await RunAsync("--game-stats", appId, LiveTimeout, request, ct, steamPath).ConfigureAwait(false) is { } line
                ? ParseResponse(line)
                : new(StatsSaveOutcome.LiveRejected, "timeout");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(StatsSaveOutcome.Failed, ex.Message); }
    }

    /// <summary>Runs Cabinet's schema helper (SchemaFill.Fetch) and waits for it to finish.</summary>
    internal static async Task FetchSchemaAsync(long appId, CancellationToken ct) =>
        await RunAsync("--schema", appId, SchemaTimeout, null, ct).ConfigureAwait(false);

    /// <summary>Returns the helper's reply (null on timeout or for helpers that write none).</summary>
    private static async Task<string?> RunAsync(string mode, long appId, TimeSpan timeout, string? stdin, CancellationToken ct, string? steamPath = null)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("LuaTools.exe not found");
        string stopName = "Local\\LuaTools-game-tools-" + Guid.NewGuid().ToString("N");
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        using var self = Process.GetCurrentProcess();
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = stdin is not null,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        if (stdin is not null) { info.StandardInputEncoding = new UTF8Encoding(false); info.StandardOutputEncoding = new UTF8Encoding(false); }
        foreach (string arg in new[] { mode, appId.ToString(CultureInfo.InvariantCulture), self.Id.ToString(CultureInfo.InvariantCulture),
                     self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), stopName })
            info.ArgumentList.Add(arg);
        if (steamPath is not null) info.ArgumentList.Add(Path.GetFullPath(steamPath));

        using var process = Process.Start(info) ?? throw new InvalidOperationException("helper did not start");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            string? line = null;
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                line = await ReadReplyAsync(process.StandardOutput, deadline.Token).ConfigureAwait(false) ?? ""; // ended without a reply
            }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return line;
        }
        catch (OperationCanceledException)
        {
            // Ask the helper to stop (it disconnects from Steam cleanly), then make sure it is gone.
            stop.Set();
            try { if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }
}
