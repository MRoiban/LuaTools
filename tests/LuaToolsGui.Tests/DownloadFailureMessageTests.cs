using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>A failed download says why, instead of a bare "check your connection".</summary>
public sealed class DownloadFailureMessageTests
{
    [Fact]
    public void UserFacingErrorsPassThrough()
    {
        Assert.Equal("Daily limit reached.", DownloadQueue.FailureMessage(new ApiException("Daily limit reached.")));
        Assert.Equal("Sign in again.", DownloadQueue.FailureMessage(new AuthException("Sign in again.")));
    }

    [Fact]
    public void UnexpectedErrorsKeepTheirInnermostReason()
    {
        var ex = new HttpRequestException("An error occurred while sending the request.",
            new IOException("Unable to read data from the transport connection.",
                new SocketException((int)SocketError.ConnectionReset)));
        string message = DownloadQueue.FailureMessage(ex);
        Assert.Equal(string.Format(LuaToolsGui.Resources.Strings.Add_Err_DownloadReason,
            new SocketException((int)SocketError.ConnectionReset).Message), message);
    }

    [Fact]
    public void QueryStringsAreNeverShown()
    {
        string message = DownloadQueue.FailureMessage(
            new InvalidDataException("Bad response from https://r2.example/f.zip?X-Amz-Signature=secret\nretry"));
        Assert.DoesNotContain("secret", message);
        Assert.Contains("https://r2.example/f.zip retry", message);
    }
}
