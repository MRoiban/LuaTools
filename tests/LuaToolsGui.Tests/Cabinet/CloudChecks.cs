using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;
using Xunit;

namespace LuaToolsGui.Tests.Cabinet;

public sealed class CloudChecks
{
    private const string Origin = "https://cabinet.example";
    private const string Token = "cab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Link = Origin + "/connect#" + Token;
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(respond(request)); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "cabinet-cloud-" + Guid.NewGuid().ToString("N"));
        internal CabinetPaths Paths;
        internal Fixture()
        {
            Paths = new CabinetPaths { DataRoot = Path.Combine(Root, "data"), StandaloneDataRoot = Path.Combine(Root, "standalone"),
                OnlineFixRoot = Path.Combine(Root, "onlinefix"), SteamRoot = Path.Combine(Root, "steam"),
                SteamRunning = () => false, StandaloneRunning = () => false, AccountId = () => "123", SteamCounting = _ => false,
                RunningApps = () => [] };
            Directory.CreateDirectory(Root);
        }
        internal CabinetCloudClient Client(Handler handler, Func<string, long>? minutes = null) => new(Paths.DataRoot, Paths.OnlineFixRoot, minutes ?? (_ => 0), _ => { })
        { Transport = uri => AppHttp.CreateCabinetCloud(uri, handler) };
        internal void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        public void Dispose() { FriendLabels.Clear(); Directory.Delete(Root, true); }
    }
    [Fact]
    public void BothOriginalLinkFormsParseWithoutExposingTokenInDestination()
    {
        foreach (var link in new[] { Link, "cabinet://connect?server=" + Uri.EscapeDataString(Origin) + "&token=" + Token })
        {
            CabinetCloudClient.ParseLink(link, out var server, out var token);
            Assert.Equal(Origin, server); Assert.Equal(Token, token); Assert.DoesNotContain(Token, server);
        }
    }
    [Theory]
    [InlineData("http://cabinet.example/connect#")]
    [InlineData("file://cabinet.example/connect#")]
    [InlineData("https://user@cabinet.example/connect#")]
    [InlineData("https://cabinet.example:444/connect#")]
    [InlineData("https://localhost/connect#")]
    [InlineData("https://127.0.0.1/connect#")]
    [InlineData("https://cabinet.example/connect?extra=1#")]
    [InlineData("https://cabinet.example/not-connect#")]
    [InlineData("https://cabinet.example/connect/abc#")]
    [InlineData("garbage")]
    public void InvalidLinksFailClosed(string prefix) => Assert.Throws<FormatException>(() => CabinetCloudClient.ParseLink(prefix + Token, out _, out _));

    [Fact]
    public void ProtocolRejectsDuplicateFieldsAndInsecureNestedOrigin()
    {
        foreach (var link in new[] { "cabinet://connect?server=http%3A%2F%2Flocalhost&token=" + Token,
            "cabinet://connect?server=" + Origin + "&token=" + Token + "&token=" + Token,
            "cabinet://user@connect?server=" + Origin + "&token=" + Token, Link + "%0a" })
            Assert.Throws<FormatException>(() => CabinetCloudClient.ParseLink(link, out _, out _));
    }
    [Theory]
    [InlineData(302)] [InlineData(307)]
    public void RedirectNeverReplaysCredentialsAndErrorDoesNotContainSecrets(int status)
    {
        using var f = new Fixture();
        var handler = new Handler(req => { Assert.Equal(Token, req.Headers.Authorization?.Parameter); return new((HttpStatusCode)status)
            { Headers = { Location = new Uri("https://other.example/" + Token) } }; });
        var error = Assert.Throws<IOException>(() => f.Client(handler).Connect(Link));
        Assert.Equal(1, handler.Calls); Assert.DoesNotContain(Token, error.ToString()); Assert.DoesNotContain("other.example", error.ToString());
    }
    [Fact]
    public async Task CloudTransportRejectsOtherOriginBeforeHandler()
    {
        var handler = new Handler(_ => throw new Exception("Must not transport"));
        using var client = AppHttp.CreateCabinetCloud(new Uri(Origin), handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://other.example/api/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(req)); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public void ResponseLimitIsAppliedBeforeBufferingAndErrorsAreSanitized()
    {
        using var f = new Fixture();
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]) });
        var error = Assert.Throws<IOException>(() => f.Client(handler).Connect(Link));
        Assert.DoesNotContain(Token, error.ToString()); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task DisconnectClearsStatusLabelsAndPersistsProtectedCredentialsOnly()
    {
        using var f = new Fixture();
        var settings = new SettingsService(new AppSettings { CabinetEnabled = true });
        int markers = 0;
        var handler = new Handler(_ => Json(new { user = new { id = "fixture", name = "Tester" } }));
        var cloud = new CabinetCloud(settings, f.Paths, () => markers++, uri => AppHttp.CreateCabinetCloud(uri, handler));
        cloud.FriendLabels = true; Assert.False(cloud.FriendLabels); Assert.Equal(0, markers);
        await cloud.ConnectAsync(Link); Assert.True(cloud.Status.Connected);
        string saved = File.ReadAllText(Path.Combine(f.Paths.DataRoot, "cloud.json")); Assert.DoesNotContain(Token, saved); Assert.Contains("dpapi:v1:", saved);
        cloud.FriendLabels = true; Assert.True(cloud.FriendLabels); Assert.Equal(1, markers);
        await cloud.DisconnectAsync(); Assert.False(cloud.Status.Connected); Assert.False(cloud.FriendLabels); Assert.False(FriendLabels.Active);
        Assert.False(f.Client(handler).Connected); Assert.False(File.Exists(Path.Combine(f.Paths.DataRoot, "cloud-consent.txt")));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void StandaloneCloudIdentityAndReceiptsAreNeverImported(bool secure)
    {
        using var f = new Fixture(); var json = new CabinetJson();
        var stored = new CloudSettings { server = secure ? Origin : "http://localhost", deviceId = "original-device", deviceName = "Fixture",
            userId = "fixture", token = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Token), null, DataProtectionScope.CurrentUser)) };
        string source = Path.Combine(f.Paths.StandaloneDataRoot, "cloud.json"); f.Put(source, json.Serialize(stored));
        f.Put(Path.Combine(f.Paths.StandaloneDataRoot, "cloud-state.json"), "{\"apps\":{}}");
        var before = File.ReadAllBytes(source); f.Paths.ImportStandalone(); Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(f.Paths.DataRoot, "cloud-consent.txt")));
        Assert.False(File.Exists(Path.Combine(f.Paths.DataRoot, "cloud.json")));
        Assert.False(File.Exists(Path.Combine(f.Paths.DataRoot, "cloud-state.json")));
    }
    [Fact]
    public async Task PresencePublishesOnlyAccountAppAndSanitizedNameAndDeletesOnStop()
    {
        using var f = new Fixture(); var bodies = new List<JsonElement>(); var methods = new List<HttpMethod>();
        var handler = new Handler(req =>
        {
            Assert.Equal(Origin, req.RequestUri!.GetLeftPart(UriPartial.Authority)); Assert.Equal(Token, req.Headers.Authorization?.Parameter);
            Assert.Equal(Origin, req.Headers.GetValues("Origin").Single()); Assert.DoesNotContain(Token, req.RequestUri.ToString());
            if (req.RequestUri.AbsolutePath == "/api/me") return Json(new { user = new { id = "fixture", name = "Tester" } });
            if (req.RequestUri.AbsolutePath == "/api/state") return Json(new { apps = new { } });
            methods.Add(req.Method);
            if (req.Content is not null) { using var doc = JsonDocument.Parse(req.Content.ReadAsStringAsync().Result); bodies.Add(doc.RootElement.Clone()); }
            return Json(new { players = Array.Empty<object>() });
        });
        var cloud = new CabinetCloud(new SettingsService(new AppSettings { CabinetEnabled = true }), f.Paths, () => { }, uri => AppHttp.CreateCabinetCloud(uri, handler));
        await cloud.ConnectAsync(Link);
        using var context = f.Paths.Enter(); var playtime = new Playtime(Path.Combine(f.Paths.DataRoot, "playtime.txt"), _ => { });
        cloud.Tick(playtime, "123", ["20", "100"], _ => "Real\nGame", _ => false, false, default);
        Assert.Single(bodies); Assert.Equal(new[] { "accountId", "appId", "name" }, bodies[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(123, bodies[0].GetProperty("accountId").GetInt32()); Assert.Equal("100", bodies[0].GetProperty("appId").GetString()); Assert.Equal("RealGame", bodies[0].GetProperty("name").GetString());
        cloud.Tick(playtime, "123", [], _ => "", _ => false, false, default); Assert.Contains(HttpMethod.Delete, methods);
    }
    [Fact]
    public void RemoteMinutesUseIdempotentReceiptAndExcludeRunningAppsAcrossRestart()
    {
        using var f = new Fixture();
        var handler = new Handler(req => req.RequestUri!.AbsolutePath == "/api/me" ? Json(new { user = new { id = "fixture", name = "Tester" } }) :
            Json(new { apps = new Dictionary<string, object> { ["100"] = new { achievements = new { }, stats = new { }, statsUpdatedAt = 0, playtime = new { remote = 12 }, save = (object?)null } } }));
        var client = f.Client(handler); client.Connect(Link); client.Round(_ => false);
        string journal = File.ReadAllText(Path.Combine(f.Paths.DataRoot, "cloud-state.json"));
        var receipts = new HashSet<string>(); int added = 0;
        bool Add(string receipt, string app, int minutes) { Assert.Equal("100", app); if (receipts.Add(receipt)) added += minutes; return true; }
        client.Deliver(Add, _ => true); Assert.Equal(0, added);
        client.Deliver(Add, _ => false); Assert.Equal(12, added);
        File.WriteAllText(Path.Combine(f.Paths.DataRoot, "cloud-state.json"), journal);
        f.Client(handler).Deliver(Add, _ => false); Assert.Equal(12, added); Assert.Single(receipts);
    }
    [Fact]
    public void SaveZipTraversalIsRejectedBeforeReplacingLocalData()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Paths.OnlineFixRoot, "100", "Saves", "slot"); f.Put(path, "original");
        using var bytes = new MemoryStream(); using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) using (var writer = new StreamWriter(zip.CreateEntry("../escape").Open())) writer.Write("bad");
        Assert.Throws<InvalidDataException>(() => f.Client(new Handler(_ => throw new Exception())).ReplaceSaves("100", bytes.ToArray(), _ => false));
        Assert.Equal("original", File.ReadAllText(path)); Assert.False(File.Exists(Path.Combine(f.Paths.OnlineFixRoot, "escape")));
    }

    [Fact]
    public void ProgressMergeAndSaveConflictKeepCommentsBackupsAndOwnPlaytime()
    {
        using var f = new Fixture();
        string stats = Path.Combine(f.Paths.OnlineFixRoot, "100", "Stats");
        f.Put(Path.Combine(stats, "Achievements.ini"), "; keep\n[WIN]\nachieved=true\ntimestamp=200\nextra=keep\n");
        f.Put(Path.Combine(stats, "Stats.ini"), "; keep stats\n[Stats]\nscore=1\n[Other]\nkeep=yes\n");
        File.SetLastWriteTimeUtc(Path.Combine(stats, "Stats.ini"), DateTime.UtcNow.AddHours(-2));
        string slot = Path.Combine(f.Paths.OnlineFixRoot, "100", "Saves", "slot"); f.Put(slot, "old");
        byte[] winner;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) using (var writer = new StreamWriter(zip.CreateEntry("slot").Open())) writer.Write("new");
            winner = stream.ToArray();
        }
        string hash = CabinetCloudClient.Hash(winner); long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int pushes = 0, uploads = 0; JsonElement progress = default;
        var handler = new Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/me") return Json(new { user = new { id = "fixture", name = "Tester" } });
            if (req.RequestUri.AbsolutePath.EndsWith("/progress"))
            { pushes++; using var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result); progress = doc.RootElement.Clone(); return Json(new { }); }
            var meta = new { sha256 = hash, updatedAt = now, deviceId = "remote", deviceName = "Fixture", size = winner.Length };
            if (req.Method == HttpMethod.Put) { uploads++; var response = Json(new { current = meta }); response.StatusCode = HttpStatusCode.Conflict; return response; }
            if (req.RequestUri.AbsolutePath.EndsWith("/save")) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(winner) };
            return Json(new { apps = new Dictionary<string, object> { ["100"] = new { achievements = new { WIN = 100 }, stats = new { score = 2 }, statsUpdatedAt = now, playtime = new { remote = 12 }, save = meta } } });
        });
        var client = f.Client(handler, _ => 7); client.Connect(Link); client.Round(_ => false);
        Assert.Equal(1, pushes); Assert.Equal(1, uploads); Assert.Equal(7, progress.GetProperty("playtimeMinutes").GetInt64());
        Assert.Equal(200, progress.GetProperty("achievements").GetProperty("WIN").GetInt64());
        Assert.Contains("timestamp=100", File.ReadAllText(Path.Combine(stats, "Achievements.ini"))); Assert.Contains("extra=keep", File.ReadAllText(Path.Combine(stats, "Achievements.ini")));
        Assert.Contains("; keep stats", File.ReadAllText(Path.Combine(stats, "Stats.ini"))); Assert.Contains("keep=yes", File.ReadAllText(Path.Combine(stats, "Stats.ini")));
        Assert.Equal("new", File.ReadAllText(slot));
        var backup = Directory.GetFiles(Path.Combine(f.Paths.DataRoot, "backup", "saves", "100"), "*.zip").Single();
        using (var zip = ZipFile.OpenRead(backup)) using (var reader = new StreamReader(zip.GetEntry("slot")!.Open())) Assert.Equal("old", reader.ReadToEnd());
        client.Round(_ => true); Assert.Equal(1, pushes); Assert.Equal(1, uploads);
    }

    private sealed class UnknownLengthContent(bool stall) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        { if (stall) await Task.Delay(Timeout.Infinite, ct); else await stream.WriteAsync(new byte[65], ct); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnknownLengthBodiesAreBoundedByActualBytesAndTime(bool stall)
    {
        using var client = AppHttp.CreateCabinetCloud(new Uri(Origin), new Handler(_ => new(HttpStatusCode.OK) { Content = new UnknownLengthContent(stall) }));
        using var req = new HttpRequestMessage(HttpMethod.Get, Origin + "/api/me"); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        new HttpResponseBudget(64, TimeSpan.FromMilliseconds(100)).Apply(req);
        var error = await Record.ExceptionAsync(() => client.SendAsync(req)); Assert.NotNull(error); Assert.DoesNotContain(Token, error.ToString());
    }

    [Fact]
    public async Task CloudStatusFlowsToCabinetChipAndMasterOffBlocksNetwork()
    {
        using var f = new Fixture(); var settings = new SettingsService(new AppSettings { CabinetEnabled = true });
        var handler = new Handler(_ => Json(new { user = new { id = "fixture", name = "Tester" } }));
        var cloud = new CabinetCloud(settings, f.Paths, () => { }, uri => AppHttp.CreateCabinetCloud(uri, handler));
        using var service = new CabinetService(settings, f.Paths, _ => { }, cloud);
        await cloud.ConnectAsync(Link); Assert.True(service.Status.Cloud.Connected);
        await cloud.DisconnectAsync(); Assert.Equal(CabinetCloudStatus.Disconnected, service.Status.Cloud);
        int calls = handler.Calls; settings.CabinetEnabled = false; await cloud.ConnectAsync(Link); await cloud.SyncNowAsync(); cloud.FriendLabels = true;
        Assert.Equal(calls, handler.Calls); Assert.False(cloud.FriendLabels);
    }
}
