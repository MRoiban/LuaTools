using System.IO;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Runs the embedded Cabinet engine's own end-to-end self-test: two PCs against an in-memory
/// localhost server, temporary roots only. Saves belong to CloudRedirect, so it also asserts the engine
/// never uploads, downloads or replaces a save folder.</summary>
public sealed class CabinetEngineSelfTestTests
{
    [Fact]
    public void CloudRoundsSyncProgressAndPlaytimeButNeverSaves()
    {
        var output = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(output);
        try { CabinetEngine.CloudSelfTest.Run(); }
        finally { Console.SetOut(previous); }
        Assert.Contains("PASS: no save upload or download", output.ToString());
        Assert.Contains("PASS: local saves left untouched", output.ToString());
        Assert.Contains("Cabinet Cloud self-test passed", output.ToString());
    }
}
