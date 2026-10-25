using System.Diagnostics;
using System.IO;

namespace LuaToolsGui.Services;

// Small test seam: tests never enumerate, start or stop the user's Steam.
internal interface ISteamProcess : IDisposable
{
    bool HasExited { get; }
    int SessionId { get; }
    string? ExecutablePath { get; }
    void KillTree();
    bool WaitForExit(int milliseconds);
}

internal sealed class SystemSteamProcess(Process process) : ISteamProcess
{
    public bool HasExited => process.HasExited;
    public int SessionId => process.SessionId;
    public string? ExecutablePath
    {
        get
        {
            // Pin a handle before reading identity or terminating. If the PID is reused,
            // this object must continue to refer to the original process.
            _ = process.SafeHandle;
            return process.MainModule?.FileName;
        }
    }
    public void KillTree() => process.Kill(entireProcessTree: true);
    public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
    public void Dispose() => process.Dispose();
}

internal static class SteamProcessControl
{
    internal const string LauncherVariable = "DUO_PORTABLE_LAUNCHER";
    internal const string SteamVariable = "DUO_PORTABLE_STEAM";
    internal const string StateVariable = "DUO_PORTABLE_STATE";

    // A GUI opened outside Steam does not inherit Falcon's environment. Recognize the
    // co-located Falcon entry point and launcher instead of launching raw steam.exe.
    internal static Func<string, string?> ResolveEnvironment(string steamExe,
        Func<string, string?> environment, Func<string, bool> exists)
    {
        if (HasIsolationContext(environment)) return environment;
        string root = Path.GetDirectoryName(CanonicalPath(steamExe))!;
        string launcher = Path.Combine(root, "PortableSteam.exe");
        if (!exists(Path.Combine(root, "SteamFL.exe"))) return environment;
        if (!exists(launcher)) throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_FalconIncomplete);
        return key => key switch
        {
            LauncherVariable => launcher,
            SteamVariable => steamExe,
            StateVariable => Path.Combine(root, "portable-state"),
            _ => environment(key)
        };
    }

    internal static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_AbsolutePaths);
        return Path.GetFullPath(path.Replace('/', '\\'));
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(CanonicalPath(a), CanonicalPath(b), StringComparison.OrdinalIgnoreCase);

    internal static ProcessStartInfo CreateStartInfo(string steamExe,
        Func<string, string?> environment, Func<string, bool> fileExists, bool shutdown = false)
    {
        steamExe = CanonicalPath(steamExe);
        if (!fileExists(steamExe)) throw new FileNotFoundException(LuaToolsGui.Resources.Strings.SteamIsolation_SteamMissing, steamExe);
        string? launcher = environment(LauncherVariable);
        string? inheritedSteam = environment(SteamVariable);
        string? state = environment(StateVariable);
        bool isolated = launcher is not null || inheritedSteam is not null || state is not null;
        if (!isolated)
        {
            if (shutdown) throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_ShutdownRoute);
            return new ProcessStartInfo(steamExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(steamExe)! };
        }
        if (string.IsNullOrWhiteSpace(launcher) || string.IsNullOrWhiteSpace(inheritedSteam) || string.IsNullOrWhiteSpace(state))
            throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_ContextIncomplete);
        launcher = CanonicalPath(launcher);
        state = CanonicalPath(state);
        if (!SamePath(inheritedSteam, steamExe) || SamePath(launcher, steamExe) || !fileExists(launcher))
            throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_ContextMismatch);
        var info = new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(launcher)! };
        info.ArgumentList.Add("--steam");
        info.ArgumentList.Add(steamExe);
        info.ArgumentList.Add("--state");
        info.ArgumentList.Add(state);
        if (shutdown)
        {
            info.ArgumentList.Add("--");
            info.ArgumentList.Add("-shutdown");
        }
        return info;
    }

    internal static bool HasIsolationContext(Func<string, string?> environment) =>
        environment(LauncherVariable) is not null || environment(SteamVariable) is not null || environment(StateVariable) is not null;

    private static List<ISteamProcess> SelectProcesses(string steamExe, int sessionId, IEnumerable<ISteamProcess> processes)
    {
        var selected = new List<ISteamProcess>();
        foreach (var process in processes)
        {
            try
            {
                // Session metadata is available from enumeration even when opening another
                // user's process handle would be denied. Exclude those before querying it.
                if (process.SessionId != sessionId || process.HasExited) continue;
                string? path = process.ExecutablePath;
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_IdentityUnknown);
                if (SamePath(path, steamExe)) selected.Add(process);
            }
            catch (Exception ex)
            {
                // Exit between enumeration and inspection is harmless; an inaccessible live
                // process is ambiguous, so fail before any termination or file mutation.
                if (process.HasExited) continue;
                throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_IdentityUnsafe, ex);
            }
        }
        return selected;
    }

    internal static bool IsRunning(string steamExe, int sessionId, IEnumerable<ISteamProcess> processes)
    {
        var all = processes.ToList();
        try { return SelectProcesses(CanonicalPath(steamExe), sessionId, all).Count != 0; }
        finally { foreach (var process in all) process.Dispose(); }
    }

    internal static void Stop(string steamExe, int sessionId, IEnumerable<ISteamProcess> processes,
        Func<string, string?> environment, Func<string, bool> fileExists, Action<ProcessStartInfo> forwardShutdown)
    {
        var all = processes.ToList();
        try
        {
            steamExe = CanonicalPath(steamExe);
            // Validate the inherited route BEFORE any process is touched. A partial or mismatched
            // context must never fall back to a raw Steam command or a tree kill.
            ProcessStartInfo? shutdown = HasIsolationContext(environment)
                ? CreateStartInfo(steamExe, environment, fileExists, shutdown: true) : null;
            var owned = SelectProcesses(steamExe, sessionId, all);
            if (owned.Count == 0) return;
            if (shutdown is not null) forwardShutdown(shutdown);
            foreach (var process in owned)
            {
                if (process.HasExited) continue;
                if (shutdown is null) process.KillTree();
                if (!process.WaitForExit(15000))
                    throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_ShutdownTimeout);
            }
        }
        finally { foreach (var process in all) process.Dispose(); }
    }

    internal static void ForwardShutdown(ProcessStartInfo info)
    {
        using var process = Process.Start(info) ?? throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_LauncherFailed);
        if (!process.WaitForExit(15000) || process.ExitCode != 0)
            throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_ShutdownFailed);
    }
}
