using System.IO;
using LuaTools.Agent;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace LuaToolsGui.Tests;
public sealed partial class AgentCliTests
{
    [Fact]
    public async Task LibraryQueriesAndGuiAdaptersUseTheSameScanFilterAndPaging()
    {
        Directory.CreateDirectory(_directory);
        string steamRoot = Path.Combine(_directory, "steam"), dir = Path.Combine(steamRoot, "config", "stplug-in");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "570.lua"), "-- Created by Test\n-- Header name\naddappid(570)");
        File.WriteAllText(Path.Combine(dir, "571.lua"), "addappid(571)");
        File.WriteAllText(Path.Combine(dir, "Steamtools.lua"), "support");
        var settings = new SettingsService(new AppSettings { SteamPathOverride = steamRoot });
        var cache = new CacheService(Path.Combine(_directory, "library.json"));
        var info = new SteamAppInfoCache(cache, NullLogger<SteamAppInfoCache>.Instance, new LibraryNoNetwork(), Path.Combine(_directory, "gui-details"));
        var covers = new CoverCache(new LibraryNoNetwork(), Path.Combine(_directory, "covers"));
        var names = Names(); var steam = new SteamService(settings);
        var entries = await LibraryCoordinator.ScanAsync(dir, names, info);
        Assert.Equal(2, entries.Count); Assert.Equal("Alpha", entries.Single(e => e.AppId == 570).Name);
        Assert.Equal("Header name", LibraryCoordinator.ParseLuaName(Path.Combine(dir, "570.lua")));
        var manage = new ManageViewModel(steam, names, info, covers, null!, settings, null!);
        await manage.LoadAsync(); Assert.Equal(entries.Select(e => e.AppId), manage.Items.Select(e => e.AppId));
        manage.SearchText = " alpha "; Assert.Equal(570, Assert.Single(manage.Items).AppId);
        manage.SelectedType = "game"; Assert.Empty(manage.Items); // unknown cache excluded under active categories
        manage.SelectedType = "Any"; manage.SearchText = ""; manage.SelectedSort = "Name (A–Z)";
        Assert.Equal(new[] { "Alpha", "Beta" }, manage.Items.Select(e => e.Name));
        var home = new HomeViewModel(steam, new AuthService(_http, Path.Combine(_directory, "home-auth")), names, info, covers, null!, null!, null!, null!);
        await home.RefreshLibraryAsync(); Assert.Equal(2, home.GameCount); Assert.Equal(entries.Select(e => e.AppId), home.Recent.Select(e => e.AppId));
        var facade = Facade(settings);
        foreach (var flags in new Dictionary<string, string?>[] { new(), new() { ["refresh"] = null }, new() { ["pending-added"] = null }, new() { ["type"] = "game" }, new() { ["genre"] = "Action" }, new() { ["year"] = "2020" }, new() { ["price"] = "free" }, new() { ["content"] = "hide-adult" }, new() { ["sort"] = "name" }, new() { ["count"] = null }, new() { ["limit"] = "1" } })
        {
            var reply = await facade.ExecuteAsync(Request(facade, "game list", flags: flags), default);
            Assert.True(reply.Ok, string.Join(',', flags.Keys) + ": " + System.Text.Json.JsonSerializer.Serialize(reply, Protocol.Json));
        }
        Assert.Single((await facade.ExecuteAsync(Request(facade, "game search", flags: new() { ["installed"] = null, ["query"] = "Alpha" }), default)).Data!.AsArray());
        Assert.Equal("570", (await facade.ExecuteAsync(Request(facade, "game get", ["570"], new() { ["fields"] = "id" }), default)).Data!["id"]!.ToString());
        Assert.Equal("WORKFLOW_DISABLED", (await facade.ExecuteAsync(Request(facade, "game remove-drm", ["570"], new() { ["yes"] = null }), default)).Error!.Code);
        var plan = await facade.ExecuteAsync(Request(facade, "game delete", ["570"], new() { ["dry-run"] = null }), default);
        Assert.True(plan.Ok); Assert.True(File.Exists(Path.Combine(dir, "570.lua")));
        Assert.Equal("CONSENT_REQUIRED", (await facade.ExecuteAsync(Request(facade, "game delete", ["570"]), default)).Error!.Code);
        Assert.Equal("READ_ONLY", (await facade.ExecuteAsync(Request(facade, "game delete", ["570"], new() { ["read-only"] = null }), default)).Error!.Code);
        Assert.Equal("VERSION_CONFLICT", (await facade.ExecuteAsync(Request(facade, "game delete", ["570"], new() { ["yes"] = null, ["if-version"] = "old" }), default)).Error!.Code);
        File.AppendAllText(Path.Combine(dir, "570.lua"), "\n-- changed");
        Assert.Equal("PLAN_STALE", (await facade.ExecuteAsync(Request(facade, "game delete", ["570"], new() { ["yes"] = null, ["plan-id"] = plan.Data!["plan"]!["id"]!.ToString() }), default)).Error!.Code);
        string currentVersion = (await facade.ExecuteAsync(Request(facade, "game get", ["570"], new() { ["fields"] = "id,version" }), default)).Data!["version"]!.ToString();
        var flagsDelete = new Dictionary<string, string?> { ["yes"] = null, ["idempotency-key"] = "library-delete", ["if-version"] = currentVersion };
        Assert.True((await facade.ExecuteAsync(Request(facade, "game delete", ["570"], flagsDelete), default)).Ok);
        Assert.True((await facade.ExecuteAsync(Request(facade, "game delete", ["570"], flagsDelete), default)).Ok);
        Assert.False(File.Exists(Path.Combine(dir, "570.lua")));
        Assert.Equal("PARTIAL_FAILURE", (await facade.ExecuteAsync(Request(facade, "game delete", flags: new() { ["ids"] = "571,999", ["yes"] = null }), default)).Error!.Code);
    }
    [Fact]
    public async Task GuiTileNameAndFilterOptionsRetainCachedDataRules()
    {
        Directory.CreateDirectory(_directory);
        string detailsDir = Path.Combine(_directory, "tile-details"); Directory.CreateDirectory(detailsDir);
        File.WriteAllText(Path.Combine(detailsDir, "570.json"), "{\"steam_appid\":570,\"name\":\"Resolved\",\"type\":\"game\",\"genres\":[{\"id\":\"1\",\"description\":\"Action\"}],\"release_date\":{\"date\":\"1 Jan, 2020\"}}");
        var cache = new CacheService(Path.Combine(_directory, "tile-cache.json"));
        var info = new SteamAppInfoCache(cache, NullLogger<SteamAppInfoCache>.Instance, new LibraryNoNetwork(), detailsDir);
        var covers = new CoverCache(new LibraryNoNetwork(), Path.Combine(_directory, "tile-covers")); covers.MarkMissing(570);
        var tile = new LuaTileViewModel(570, "", DateTime.UtcNow, "App 570", true);
        await tile.EnsureResolvedAsync(info, covers); Assert.Equal("Resolved", tile.Name); Assert.Null(tile.Cover);
        var options = LibraryCoordinator.FilterOptions([info.GetFilterData(570), null]);
        Assert.Equal(new[] { "game" }, options.Types); Assert.Equal(new[] { "Action" }, options.Genres); Assert.Equal(new[] { "2020" }, options.Years);
        string dir = Path.Combine(_directory, "option-steam", "config", "stplug-in"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "570.lua"), "addappid(570)");
        var settings = new SettingsService(new AppSettings { SteamPathOverride = Path.Combine(_directory, "option-steam") });
        var vm = new ManageViewModel(new SteamService(settings), Names(), info, covers, null!, settings, null!);
        await vm.LoadAsync(); Assert.Contains("game", vm.TypeOptions); Assert.Contains("Action", vm.GenreOptions); Assert.Contains("2020", vm.YearOptions);
        vm.SelectedGenre = "action"; Assert.Equal(570, Assert.Single(vm.Items).AppId);
    }
    private sealed class LibraryStore : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(request.RequestUri!.AbsolutePath.Contains("featuredcategories")
                ? "{\"top_sellers\":{\"items\":[{\"id\":570,\"name\":\"Alpha\",\"type\":0,\"large_capsule_image\":\"https://example.com/cover.jpg\"},{\"id\":570,\"name\":\"Alpha\",\"type\":0,\"large_capsule_image\":\"https://example.com/cover.jpg\"}]}}"
                : "{\"items\":[{\"id\":570,\"name\":\"Alpha\"},{\"id\":571,\"name\":\"Hardware\"}]}") });
    }
    [Fact]
    public async Task PublicSearchAndFeaturedUseExistingCatalogueAndHardwareRules()
    {
        Directory.CreateDirectory(_directory);
        var cache = new CacheService(Path.Combine(_directory, "public.json"));
        cache.SaveHardwareAppIds([571], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var info = new SteamAppInfoCache(cache, NullLogger<SteamAppInfoCache>.Instance, new LibraryNoNetwork(), Path.Combine(_directory, "public-details"));
        var auth = new AuthService(_http, Path.Combine(_directory, "public-auth"));
        using var http = new System.Net.Http.HttpClient(new LibraryStore());
        var api = new LuaToolsApiClient(auth, info, new CoverCache(), http);
        var facade = Facade(new SettingsService(new AppSettings { SteamPathOverride = Path.Combine(_directory, "absent-steam") }), api: api, hardware: new HardwareAppIdService(new GithubProxy(), cache));
        var search = await facade.ExecuteAsync(Request(facade, "game search", flags: new() { ["query"] = "Alpha" }), default);
        Assert.Equal("570", Assert.Single(search.Data!.AsArray())!["id"]!.ToString());
        var featured = await facade.ExecuteAsync(Request(facade, "game list", flags: new() { ["featured"] = null }), default);
        Assert.Equal("top-sellers", Assert.Single(featured.Data!.AsArray())!["category"]!.ToString());
    }
    private sealed class LibraryPages : PagedListViewModel<int> { public void Set(IEnumerable<int> values, bool reset = true) => SetFiltered(values, reset); }
    [Fact]
    public void GuiPagingRetainsClampsAndSlicesAfterExtraction()
    {
        var vm = new LibraryPages(); vm.SelectedPageSize = "12"; vm.Set(Enumerable.Range(1, 30));
        vm.NextPageCommand.Execute(null); Assert.Equal(Enumerable.Range(13, 12), vm.Items);
        vm.Set(Enumerable.Range(1, 5), false); Assert.Equal(1, vm.CurrentPage); Assert.Equal(1, vm.TotalPages);
        vm.SelectedPageSize = "All"; Assert.Equal(5, vm.Items.Count); Assert.False(vm.ShowPager);
    }
    [Theory]
    [InlineData("website", null, true)] [InlineData("hubcap", null, true)]
    [InlineData("markdown", "https://example.com/help", true)] [InlineData("markdown", "file:///secret", false)]
    [InlineData("steamdb", null, false)]
    public async Task LinkPolicyReturnsDataWithoutLaunching(string kind, string? url, bool expected)
    {
        var facade = Facade(); var flags = new Dictionary<string, string?> { ["kind"] = kind }; if (url is not null) flags["url"] = url;
        Assert.Equal(expected, (await facade.ExecuteAsync(Request(facade, "link get", flags: flags), default)).Ok);
    }
}
