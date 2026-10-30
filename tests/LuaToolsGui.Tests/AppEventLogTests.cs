using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LuaToolsGui.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class AppEventLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "event-log-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 30, 9, 30, 0, TimeSpan.Zero);

    private AppEventLog Log() => new(_dir, () => _now);
    private string[] Files(string prefix) => Directory.GetFiles(_dir, prefix + "-*.jsonl").Select(Path.GetFileName).Order().ToArray()!;
    private long TotalBytes() => Directory.GetFiles(_dir).Sum(f => new FileInfo(f).Length);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void EventsGoToTheStreamAndWarningsAlsoToTheLongerHistory()
    {
        var log = Log();
        log.Write(LogLevel.Debug, "Test", "routine");
        log.Write(LogLevel.Warning, "Test", "line one\nline two", new InvalidDataException("Unsafe archive path."));

        Assert.Equal(["events-20261030-09.jsonl"], Files("events"));
        Assert.Equal(["important-20261030.jsonl"], Files("important"));
        var lines = File.ReadAllLines(Path.Combine(_dir, "events-20261030-09.jsonl"));
        Assert.Equal(2, lines.Length); // a newline in a message cannot forge a second event
        Assert.Single(File.ReadAllLines(Path.Combine(_dir, "important-20261030.jsonl")));
        using var warning = JsonDocument.Parse(lines[1]);
        Assert.Equal("warn", warning.RootElement.GetProperty("l").GetString());
        Assert.Equal("line one\nline two", warning.RootElement.GetProperty("m").GetString());
        Assert.Contains("Unsafe archive path.", warning.RootElement.GetProperty("x").GetString());
    }

    [Fact]
    public void RetentionKeeps24HoursOfEventsAnd7DaysOfWarnings()
    {
        var log = Log();
        for (int hour = 0; hour < 24 * 10; hour++)
        {
            log.Write(LogLevel.Warning, "Test", $"hour {hour}");
            _now = _now.AddHours(1);
        }
        log.Write(LogLevel.Warning, "Test", "last");

        Assert.Equal(25, Files("events").Length);
        Assert.Equal(8, Files("important").Length);
        Assert.Contains(_now.AddHours(-23).ToString("yyyyMMdd-HH"), Files("events")[1]);
        Assert.Contains(_now.AddDays(-7).ToString("yyyyMMdd"), Files("important")[0]);
    }

    [Fact]
    public void AFloodStaysInsideItsFileAndCannotSilenceWarningsOrEvictHistory()
    {
        var log = Log();
        log.Write(LogLevel.Information, "Test", "before the flood");
        _now = _now.AddHours(1);
        string noise = new('x', 1000);
        for (int i = 0; i < 3000; i++) log.Write(LogLevel.Debug, "Test", noise);
        log.Write(LogLevel.Error, "Test", "the failure that matters");

        Assert.True(TotalBytes() < 2 * AppEventLog.MaxSegmentBytes);
        Assert.Contains("before the flood", File.ReadAllText(Path.Combine(_dir, "events-20261030-09.jsonl")));
        Assert.Contains("the failure that matters", File.ReadAllText(Path.Combine(_dir, "important-20261030.jsonl")));

        _now = _now.AddHours(1);
        log.Write(LogLevel.Information, "Test", "after");
        Assert.Contains("event(s) dropped", File.ReadAllText(Path.Combine(_dir, "events-20261030-11.jsonl")));
    }

    [Fact]
    public void AnOversizedMessageIsClipped()
    {
        Log().Write(LogLevel.Information, "Test", new string('x', 1_000_000), new Exception(new string('y', 1_000_000)));
        Assert.True(TotalBytes() < 16 * 1024);
    }

    [Fact]
    public void ANewSessionExpiresOldFilesAndRemovesTheUnboundedLog()
    {
        Directory.CreateDirectory(_dir);
        string legacy = Path.Combine(Path.GetDirectoryName(_dir)!, "plugin-backend.log");
        File.WriteAllText(Path.Combine(_dir, "events-20261001-09.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(_dir, "important-20261001.jsonl"), "{}\n");
        File.WriteAllText(legacy, "old");
        try
        {
            Log().Start("Started");
            Assert.Equal(["events-20261030-09.jsonl"], Files("events"));
            Assert.Empty(Files("important"));
            Assert.False(File.Exists(legacy));
        }
        finally { File.Delete(legacy); }
    }

    [Fact]
    public void AWriteFailureNeverReachesTheCaller()
    {
        File.WriteAllText(_dir, "a file where the directory should be");
        try { Log().Write(LogLevel.Error, "Test", "ignored"); }
        finally { File.Delete(_dir); }
    }

    [Theory]
    [InlineData("GET https://hubcapmanifest.com/api/v1/manifest/10?api_key=smm_abc failed", "smm_abc")]
    [InlineData("https://cdn.example/f.zip?X-Amz-Signature=deadbeef&X-Amz-Date=1", "deadbeef")]
    [InlineData("api_key=plainvalue", "plainvalue")]
    [InlineData("{\"access_token\":\"opaque-session\"}", "opaque-session")]
    [InlineData("Authorization: Bearer opaque-session", "opaque-session")]
    [InlineData("key smm_0123456789abcdef in text", "smm_0123456789abcdef")]
    [InlineData("cabinet://connect?server=https%3A%2F%2Fa.b&token=cab_aaaaaaaaaa", "cab_aaaaaaaaaa")]
    [InlineData("https://a.b/connect#cab_aaaaaaaaaa", "cab_aaaaaaaaaa")]
    [InlineData("session eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl", "eyJzdWIiOi")]
    [InlineData("addappid(1,0,\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\")", "0123456789abcdef0123456789abcdef")]
    [InlineData(@"C:\Users\Marius\AppData\Local\Temp\a.zip", "Marius")]
    [InlineData(@"D:\Steam\userdata\41234567\config\localconfig.vdf", "41234567")]
    [InlineData("steamid 76561198012345678", "76561198012345678")]
    public void SecretsAndIdentifiersNeverReachTheFile(string text, string secret)
    {
        var log = Log();
        log.Write(LogLevel.Warning, "Test", text);
        log.Write(LogLevel.Warning, "Test", "failed", new Exception(text));
        foreach (string file in Directory.GetFiles(_dir))
            Assert.DoesNotContain(secret, File.ReadAllText(file));
    }

    [Fact]
    public void RedactionKeepsWhatADiagnosisNeeds()
    {
        const string text = "Download Failed: manifest:3591180: Unsafe archive path. hubcapmanifest.com/api/v1/manifest/3591180 -> 200 in 12ms";
        Assert.Equal(text, LogRedactor.Redact(text));
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response(request));
    }

    [Fact]
    public async Task ARefusedRedirectIsRecordedWithoutItsCredentialQuery()
    {
        string appid = Random.Shared.NextInt64(1_000_000_000, 4_000_000_000).ToString();
        string key = "smm_" + new string('a', 96);
        using var http = AppHttp.CreateForTesting(TimeSpan.FromSeconds(5), new Stub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://not-allowed.example/file.zip");
            return response;
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync($"https://hubcapmanifest.com/api/v1/manifest/{appid}?api_key={key}"));

        string logged = string.Concat(Directory.GetFiles(AppEventLog.Shared.Directory, "important-*.jsonl").Select(path =>
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }));
        Assert.Contains($"hubcapmanifest.com/api/v1/manifest/{appid} failed", logged);
        Assert.DoesNotContain(key, logged);
    }

    [Fact]
    public void ILoggerCallsArriveWithAShortCategory()
    {
        ILogger logger = new AppEventLogProvider(Log()).CreateLogger("LuaToolsGui.Services.Downloads.DownloadQueue");
        logger.LogWarning(new InvalidDataException("boom"), "Download job failed: {Key}", "manifest:10");
        using var line = JsonDocument.Parse(File.ReadAllLines(Path.Combine(_dir, "important-20261030.jsonl"))[0]);
        Assert.Equal("DownloadQueue", line.RootElement.GetProperty("c").GetString());
        Assert.Equal("Download job failed: manifest:10", line.RootElement.GetProperty("m").GetString());
    }
}
