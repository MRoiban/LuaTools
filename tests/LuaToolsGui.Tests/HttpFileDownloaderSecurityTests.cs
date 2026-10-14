using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

public class HttpFileDownloaderSecurityTests
{
    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("..\\escape.zip")]
    [InlineData("C:\\escape.zip")]
    [InlineData("file.zip:stream")]
    [InlineData("..")]
    [InlineData("CON.zip")]
    [InlineData("nul.manifest")]
    [InlineData("LPT1.lua")]
    public async Task RejectsUntrustedFilename(string name)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = name };
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() =>
            HttpFileDownloader.SaveResponseAsync(response, "fallback.zip", null, default));
    }

    [Fact]
    public async Task RejectsOversizedDeclaredDownload()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentLength = 512L * 1024 * 1024 + 1;
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() =>
            HttpFileDownloader.SaveResponseAsync(response, "manifest.zip", null, default));
    }

    [Fact]
    public async Task RejectsTruncatedDownload()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentLength = 100;
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() =>
            HttpFileDownloader.SaveResponseAsync(response, "manifest.zip", null, default));
    }
}
