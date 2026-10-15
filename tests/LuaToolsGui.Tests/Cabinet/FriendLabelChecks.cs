using System.Diagnostics;
using System.IO;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;
using Xunit;

namespace LuaToolsGui.Tests.Cabinet;

public sealed class FriendLabelChecks
{
    [Theory]
    [InlineData("a\n\t\u007f\u0085b", "ab")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void NamesAreCleaned(string? input, string expected) => Assert.Equal(expected, FriendLabels.CleanName(input));

    [Fact]
    public void TruncationPreservesSurrogatePairs() => Assert.Equal(99, FriendLabels.CleanName(new string('a', 99) + "\ud83d\ude00").Length);

    [Fact]
    public void OwnStaleFutureAndInvalidPresenceIsExcluded()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        object Row(uint id, string app, long time) => new Dictionary<string, object>
        { ["accountId"] = id, ["appId"] = app, ["name"] = "Fixture\nGame", ["updatedAt"] = time };
        var data = new Dictionary<string, object> { ["players"] = new[] { Row(1, "100", now), Row(2, "100", now), Row(3, "100", now - 180001), Row(4, "bad", now), Row(5, "100", now + 99999) } };
        var labels = FriendLabels.ReadPlayers(data, 1); Assert.Single(labels); Assert.Equal("FixtureGame", labels["2"].name);
        FriendLabels.Update(data, 1); Assert.True(FriendLabels.Active);
        Assert.DoesNotContain("s.expires=Date.now()+180000", FriendLabels.Script);
        FriendLabels.Clear(); Assert.False(FriendLabels.Active);
    }
    [Theory]
    [InlineData("ws://127.0.0.1:8080/devtools/page/abc", true)]
    [InlineData("ws://localhost:8080/devtools/page/abc", false)]
    [InlineData("ws://127.0.0.1:8080/devtools/page/../abc", false)]
    [InlineData("ws://evil.example:8080/devtools/page/abc", false)]
    [InlineData("ws://127.0.0.1:8080/devtools/page/abc?token=x", false)]
    public void ExistingDebuggerEndpointValidationRemainsStrict(string endpoint, bool allowed) => Assert.Equal(allowed, CefInjectorService.IsAllowedDebuggerEndpoint(endpoint));

    [Fact]
    public async Task BundledScriptReappliesFailsSafelyRestoresAndExpiresInSyntheticSteamStore()
    {
        string path = Path.Combine(Path.GetTempPath(), "cabinet-script-" + Guid.NewGuid().ToString("N") + ".cjs");
        string script = FriendLabels.BuildScript(new() { ["123"] = new() { name = "Real \"quoted\" game", app = "100" } });
        string clear = FriendLabels.BuildScript(new());
        string mock = """
            const assert=require('node:assert/strict');global.window=global;
            let timers=[],timeouts=[];global.setInterval=f=>{timers.push(f);return timers.length;};
            global.clearInterval=i=>{timers[i-1]=null;};global.setTimeout=f=>{timeouts.push(f);return timeouts.length;};global.clearTimeout=i=>{timeouts[i-1]=null;};
            class Persona {constructor(app,name){this.m_unGamePlayedAppID=app;this.m_gameid=String(app);this.m_strGameExtraInfo=name;this.connect='+connect_lobby 123';}
            UpdateFromMessage(app,name){this.m_unGamePlayedAppID=app;this.m_gameid=String(app);this.m_strGameExtraInfo=name;return 42;}
            GetCurrentGameIconURL(){return 'original-icon';}}
            const p=new Persona(480,'Spacewar'),other=new Persona(200,'Other');
            const original=Persona.prototype.UpdateFromMessage;
            global.g_FriendsUIApp={FriendStore:{all_friends:new Set([{accountid:123,persona:p},{accountid:456,persona:other}])}};
            """;
        string behavior = """
            const patched=Persona.prototype.UpdateFromMessage;
            assert.notEqual(patched,original);assert.equal(p.m_strGameExtraInfo,'Real "quoted" game');assert.equal(p.m_gameid,'100');
            assert.equal(p.m_unGamePlayedAppID,480);assert.equal(p.connect,'+connect_lobby 123');assert.equal(other.m_strGameExtraInfo,'Other');
            assert.equal(p.UpdateFromMessage(480,'Fresh Steam name'),42);assert.equal(p.m_strGameExtraInfo,'Real "quoted" game');
            const one=__cabinetLabels.one;__cabinetLabels.one=()=>{throw Error('synthetic failure');};
            assert.equal(p.UpdateFromMessage(480,'Server name'),42);assert.equal(p.m_strGameExtraInfo,'Server name');
            assert.equal(__cabinetLabels.errors.update,'synthetic failure');__cabinetLabels.one=one;__cabinetLabels.apply();
            """;
        string restored = """
            assert.equal(p.m_strGameExtraInfo,'Server name');assert.equal(p.m_gameid,'480');assert.equal(Persona.prototype.UpdateFromMessage,original);
            assert.equal(window.__cabinetLabels,undefined);assert.equal(p.connect,'+connect_lobby 123');
            """;
        string expiry = """
            p.m_strGameExtraInfo='External';p.m_gameid='999';__cabinetLabels.expires=0;__cabinetLabels.apply();
            assert.equal(p.m_strGameExtraInfo,'External');assert.equal(p.m_gameid,'999');assert.equal(window.__cabinetLabels,undefined);
            assert.equal(Persona.prototype.UpdateFromMessage,original);assert.ok(timers.every(x=>x===null));
            """;
        try
        {
            await File.WriteAllTextAsync(path, mock + script + ";\n" + behavior + clear + ";\n" + restored + script + ";\n" + expiry);
            var info = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            info.ArgumentList.Add(path);
            using var process = Process.Start(info)!;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(); throw; }
            Assert.True(process.ExitCode == 0, await stderr);
        }
        finally { File.Delete(path); }
    }
}
