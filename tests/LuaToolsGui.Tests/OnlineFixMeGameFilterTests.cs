using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class OnlineFixMeGameFilterTests
{
    private static FixGameCardVm Card(string id, string name, string tag = "generic") => new(new DenuvoGameListing
    {
        AppId = id, Name = name, FixCount = 2, Tags = [new DenuvoTag { Id = tag, Name = tag }],
    });

    [Fact]
    public void ProviderCardsUseOnlyInstalledGamesReuseListingCardsAndDeduplicateAppIds()
    {
        var listed = Card("620", "Portal 2");
        var cards = FixesViewModel.BuildOnlineFixMeCards([listed, Card("999", "Not installed")],
        [
            new(620, "Appmanifest title", "local"), new(391540, "Undertale", "local"),
            new(620, "Duplicate library entry", "other"),
        ]);
        Assert.Equal(2, cards.Count);
        Assert.Same(listed, cards[0]);
        Assert.False(cards[0].IsSynthetic);
        Assert.True(cards[1].IsSynthetic);
        Assert.Equal("Undertale", cards[1].Name);
        Assert.Equal(Resources.Strings.Fixes_OnlineFixMe_Provider, cards[1].FixCountLabel);
        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/391540/header.jpg", cards[1].HeaderImage);
    }

    [Theory]
    [InlineData(" portal ", "620")]
    [InlineData("391540", "391540")]
    public void ProviderPillSearchesInstalledCardsRegardlessOfTheirListingTags(string query, string expected)
    {
        var cards = FixesViewModel.BuildOnlineFixMeCards([Card("620", "Portal 2")],
            [new(620, "Portal 2", "local"), new(391540, "Undertale", "local")]);
        var shown = FixesViewModel.FilterGameCards([], cards, FixesViewModel.OnlineFixMeTagId, false, new HashSet<long>(), query);
        Assert.Equal(expected, Assert.Single(shown).AppId);
    }

    [Fact]
    public void AllMyGamesAndOtherTagsContinueToFilterTheOriginalListing()
    {
        var listed = Card("620", "Portal 2");
        var other = Card("999", "Other", "other");
        var installed = FixesViewModel.BuildOnlineFixMeCards([], [new(391540, "Undertale", "local")]);
        var all = FixesViewModel.FilterGameCards([listed, other], installed, null, false, new HashSet<long>(), "");
        Assert.Equal(new[] { listed, other }, all);
        var mine = FixesViewModel.FilterGameCards([listed, other], installed, null, true, new HashSet<long> { 620, 391540 }, "");
        Assert.Same(listed, Assert.Single(mine));
        var tagged = FixesViewModel.FilterGameCards([listed, other], installed, "other", false, new HashSet<long>(), "");
        Assert.Same(other, Assert.Single(tagged));
    }

    [Fact]
    public void EmptyInstalledLibraryDoesNotFallBackToThePublicListing() =>
        Assert.Empty(FixesViewModel.FilterGameCards([Card("620", "Portal 2")], [],
            FixesViewModel.OnlineFixMeTagId, false, new HashSet<long>(), ""));

    [Fact]
    public void SteamRuntimesAreNotListedAsGames()
    {
        var cards = FixesViewModel.BuildOnlineFixMeCards([],
            [new(228980, "Steamworks Common Redistributables", "local"), new(391540, "Undertale", "local")]);
        Assert.Equal(["Undertale"], cards.Select(c => c.Name));
    }
}
