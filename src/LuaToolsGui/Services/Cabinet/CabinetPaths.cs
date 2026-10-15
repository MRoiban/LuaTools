using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LuaToolsGui.Services.Cabinet;

/// <summary>All engine roots and OS probes are injectable; fixtures never discover live Steam.</summary>
public sealed class CabinetPaths
{
    private static readonly AsyncLocal<CabinetPaths?> Context = new();
    internal static CabinetPaths Current => Context.Value ?? Default;
    internal static readonly CabinetPaths Default = new();
    public string DataRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirectoryName, "Cabinet");
    public string StandaloneDataRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cabinet");
    public string OnlineFixRoot { get; init; } = @"C:\Users\Public\Documents\OnlineFix";
    public string? SteamRoot { get; init; }
    public Func<bool> SteamRunning { get; init; } = () => ProcessRunning("steam");
    public Func<bool> StandaloneRunning { get; init; } = () => ProcessRunning("Cabinet");
    public Func<string?>? AccountId { get; init; }
    public Func<string, bool>? SteamCounting { get; init; }
    public Func<HashSet<string>>? RunningApps { get; init; }

    internal IDisposable Enter()
    {
        var previous = Context.Value;
        Context.Value = this;
        return new Scope(() => Context.Value = previous);
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private static bool ProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    internal static void Numeric(string value)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0 || value != id.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("Invalid canonical app/account ID");
    }

    /// <summary>Copy only non-spendable engine state; never executable, playtime or cloud ledgers.</summary>
    internal void ImportStandalone()
    {
        CabinetFiles.EnsureSafePath(DataRoot);
        Directory.CreateDirectory(DataRoot);
        var marker = Path.Combine(DataRoot, "standalone-imported.txt");
        CabinetFiles.EnsureSafePath(marker);
        if (File.Exists(marker)) return;
        if (Directory.Exists(StandaloneDataRoot))
        {
            CabinetFiles.EnsureSafePath(StandaloneDataRoot);
            // Standalone retains ownership of all playtime.txt fields: pending/inflight time,
            // LastPlayed and cloud receipts/provenance are one recovery ledger. Splitting it
            // could replay credit or suppress recovery. Do not copy cloud.json/cloud-state.json
            // either: sharing a device identity with independently advancing receipt journals
            // permits both apps to spend the same remote credit. Cloud requires a fresh explicit
            // connection; automatic import is limited to the non-spendable state below.
            foreach (var name in new[] { "state.txt", "sync-times.txt", "workshop-links.json", "workshop-disabled.txt" })
            {
                var source = Path.Combine(StandaloneDataRoot, name);
                if (File.Exists(source)) CopyFile(source, Path.Combine(DataRoot, name));
            }
            foreach (var name in new[] { "backup", "schemas" })
            {
                var source = Path.Combine(StandaloneDataRoot, name);
                if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(DataRoot, name));
            }
        }
        WorkshopEngine.AtomicWrite(marker, DateTimeOffset.UtcNow.ToString("O"));
    }
    private static void CopyFile(string source, string destination)
    {
        CabinetFiles.EnsureSafePath(source);
        CabinetFiles.EnsureSafePath(destination);
        if (File.Exists(destination))
        {
            // Preserve local state instead of overwriting it with a conflicting import.
            if (!CabinetFiles.ReadBytes(source).SequenceEqual(CabinetFiles.ReadBytes(destination)))
                throw new InvalidDataException("Conflicting standalone Cabinet state: " + Path.GetFileName(source));
            return;
        }
        File.Copy(source, destination);
    }
    private static void CopyDirectory(string source, string destination)
    {
        CabinetFiles.EnsureSafePath(source);
        CabinetFiles.EnsureSafePath(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}

// State format compatibility with Cabinet's public-field DTOs, implemented solely with System.Text.Json.
internal sealed class CabinetJson
{
    public int MaxJsonLength { get; init; } = 32 * 1024 * 1024;
    public int RecursionLimit { get; init; } = 64;
    private JsonSerializerOptions Options => new() { IncludeFields = true, MaxDepth = RecursionLimit };
    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public T? Deserialize<T>(string value)
    {
        if (value.Length > MaxJsonLength) throw new InvalidDataException("Oversized Cabinet state");
        return JsonSerializer.Deserialize<T>(value, Options);
    }
    public object? DeserializeObject(string value)
    {
        var element = Deserialize<JsonElement>(value);
        return ConvertElement(element);
    }
    private static object? ConvertElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertElement(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertElement).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

// Diagnostic text only. Public notifications use CabinetNotification, never these source messages.
internal static class L { public static string T(string en, string fr) => en; }
