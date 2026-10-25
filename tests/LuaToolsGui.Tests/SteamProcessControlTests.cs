using System.Diagnostics;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class SteamProcessControlTests
{
    private const string Steam = @"C:\Player One\Steam\steam.exe";
    private const string Launcher = @"C:\Portable Tools\PortableSteam.exe";
    private const string State = @"C:\Player One\Steam\portable-state";
    private static string? NoEnvironment(string _) => null;
    private static string? IsolatedEnvironment(string key) => key switch
    {
        SteamProcessControl.LauncherVariable => Launcher,
        SteamProcessControl.SteamVariable => Steam,
        SteamProcessControl.StateVariable => State,
        _ => null
    };
    private sealed class FakeProcess : ISteamProcess
    {
        public bool Exited;
        public bool HasExited => Exited;
        public int SessionId { get; init; } = 2;
        public string? Path { get; init; } = Steam;
        public bool Unreadable;
        public string? ExecutablePath
        {
            get
            {
                if (ExitDuringRead) { Exited = true; throw new InvalidOperationException("Process exited"); }
                return Unreadable ? throw new UnauthorizedAccessException() : Path;
            }
        }
        public bool Killed, Waited, Disposed;
        public bool WaitResult = true;
        public bool ExitDuringRead;
        public void KillTree() => Killed = true;
        public bool WaitForExit(int milliseconds) { Waited = true; Exited = WaitResult; return WaitResult; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void StopTargetsOnlyExactInstallAndSession()
    {
        var selected = new FakeProcess();
        var anotherFolder = new FakeProcess { Path = @"C:\Player Two\Steam\steam.exe" };
        var anotherSession = new FakeProcess { SessionId = 1, Unreadable = true };
        var exited = new FakeProcess { Exited = true };
        var processes = new[] { selected, anotherFolder, anotherSession, exited };
        SteamProcessControl.Stop(Steam, 2, processes, NoEnvironment, _ => true, _ => throw new Exception("No forwarding expected"));
        Assert.True(selected.Killed);
        Assert.True(selected.Waited);
        Assert.All(processes.Skip(1), p => Assert.False(p.Killed));
        Assert.All(processes, p => Assert.True(p.Disposed));
    }

    [Fact]
    public void UnknownLiveIdentityAbortsBeforeStoppingAnyClient()
    {
        var selected = new FakeProcess();
        var unknown = new FakeProcess { Unreadable = true };
        var later = new FakeProcess();
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.Stop(Steam, 2,
            new[] { selected, unknown, later }, NoEnvironment, _ => true, _ => { }));
        Assert.All(new[] { selected, unknown, later }, p => { Assert.False(p.Killed); Assert.True(p.Disposed); });
    }

    [Fact]
    public void MissingPathIsUnknownRatherThanSelected()
    {
        var unknown = new FakeProcess { Path = null };
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.Stop(Steam, 2,
            new[] { unknown }, NoEnvironment, _ => true, _ => { }));
        Assert.False(unknown.Killed);
    }

    [Fact]
    public void ProcessThatExitsDuringInspectionIsNeverTerminated()
    {
        var stale = new FakeProcess { ExitDuringRead = true };
        var selected = new FakeProcess();
        SteamProcessControl.Stop(Steam, 2, new[] { stale, selected }, NoEnvironment, _ => true, _ => { });
        Assert.False(stale.Killed);
        Assert.True(stale.Disposed);
        Assert.True(selected.Killed);
    }

    [Fact]
    public void RunningCheckIgnoresOtherPlayersAndDisposesAllHandles()
    {
        var elsewhere = new FakeProcess { Path = @"C:\Other\steam.exe" };
        var otherSession = new FakeProcess { SessionId = 1, Unreadable = true };
        Assert.False(SteamProcessControl.IsRunning(Steam, 2, new[] { elsewhere, otherSession }));
        Assert.True(elsewhere.Disposed);
        Assert.True(otherSession.Disposed);
        var selected = new FakeProcess();
        Assert.True(SteamProcessControl.IsRunning(Steam, 2, new[] { selected }));
        Assert.True(selected.Disposed);
    }

    [Fact]
    public void PortableShutdownUsesScopedLauncherAndNeverKillsCallerTree()
    {
        var selected = new FakeProcess();
        var another = new FakeProcess { Path = @"C:\Elsewhere\steam.exe" };
        ProcessStartInfo? sent = null;
        SteamProcessControl.Stop(Steam, 2, new[] { selected, another }, IsolatedEnvironment, _ => true, info => sent = info);
        Assert.NotNull(sent);
        Assert.Equal(Launcher, sent.FileName);
        Assert.False(sent.UseShellExecute);
        Assert.Equal(new[] { "--steam", Steam, "--state", State, "--", "-shutdown" }, sent.ArgumentList);
        Assert.False(selected.Killed);
        Assert.True(selected.Waited);
        Assert.False(another.Killed);
        Assert.False(another.Waited);
    }

    [Fact]
    public void PortableStopDoesNotLaunchWhenNoSelectedClientExists()
    {
        int forwards = 0;
        SteamProcessControl.Stop(Steam, 2, new[] { new FakeProcess { SessionId = 1 } },
            IsolatedEnvironment, _ => true, _ => forwards++);
        Assert.Equal(0, forwards);
    }

    [Fact]
    public void ForwardingFailureNeverFallsBackToKill()
    {
        var selected = new FakeProcess();
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.Stop(Steam, 2, new[] { selected },
            IsolatedEnvironment, _ => true, _ => throw new InvalidOperationException("forward failed")));
        Assert.False(selected.Killed);
        Assert.True(selected.Disposed);
    }

    [Fact]
    public void ShutdownTimeoutPreventsRestartAndFileMutation()
    {
        var selected = new FakeProcess { WaitResult = false };
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.Stop(Steam, 2, new[] { selected },
            IsolatedEnvironment, _ => true, _ => { }));
        Assert.False(selected.Killed);
        Assert.True(selected.Disposed);
    }

    [Theory]
    [InlineData(SteamProcessControl.LauncherVariable)]
    [InlineData(SteamProcessControl.SteamVariable)]
    [InlineData(SteamProcessControl.StateVariable)]
    public void IncompleteContextAbortsStopAndStart(string missing)
    {
        string? Env(string key) => key == missing ? null : IsolatedEnvironment(key);
        var selected = new FakeProcess();
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.Stop(Steam, 2, new[] { selected }, Env, _ => true, _ => { }));
        Assert.False(selected.Killed);
        Assert.True(selected.Disposed);
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.CreateStartInfo(Steam, Env, _ => true));
    }

    [Fact]
    public void SelectedDifferentInstallCannotUseInheritedRouteOrFallBack()
    {
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.CreateStartInfo(@"C:\Other\steam.exe", IsolatedEnvironment, _ => true));
    }

    [Fact]
    public void MissingLauncherCannotFallBackToPlainSteam()
    {
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.CreateStartInfo(Steam, IsolatedEnvironment, path => path != Launcher));
    }

    [Fact]
    public void RelativeIsolationStateIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => SteamProcessControl.CreateStartInfo(Steam,
            key => key == SteamProcessControl.StateVariable ? "relative" : IsolatedEnvironment(key), _ => true));
    }

    [Fact]
    public void IsolatedStartPreservesPathsAsIndividualArguments()
    {
        var info = SteamProcessControl.CreateStartInfo(Steam, IsolatedEnvironment, _ => true);
        Assert.Equal(Launcher, info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(new[] { "--steam", Steam, "--state", State }, info.ArgumentList);
        Assert.Equal("", info.Arguments);
    }

    [Fact]
    public void NormalStartRetainsExistingLaunchBehavior()
    {
        var info = SteamProcessControl.CreateStartInfo(Steam, NoEnvironment, _ => true);
        Assert.Equal(Steam, info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void CaseAndSeparatorsDoNotChangeSelectedIdentity()
    {
        Assert.True(SteamProcessControl.SamePath(Steam, "c:/player one/steam/steam.exe"));
        Assert.False(SteamProcessControl.SamePath(Steam, @"C:\Player One\Steam\steam.exe.other"));
    }
}
