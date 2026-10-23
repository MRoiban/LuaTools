using System.IO;
using LuaToolsGui.Services.OnlineFixMe;
using Xunit;

namespace LuaToolsGui.Tests;

public class OnlineFixMeListingTests
{
    [Theory]
    [InlineData("palworld-fix-repair.html", "Palworld", "Palworld_Fix_Repair_GDK_Generic.rar", "8M", "21-Mar-2025 00:31")]
    [InlineData("raft-fix-repair.html", "Raft", "Raft_Fix_Repair_Steam.rar", "11M", "23-Jan-2026 10:31")]
    public void SavedAutoindexHasExactArchiveAndColumns(string fixture, string game, string name, string size, string date)
    {
        string html = Fixture(fixture);
        Assert.True(OnlineFixMeListing.LooksLikeIndex(html));
        Uri folder = OnlineFixMeListing.FolderFromName(game)!;
        var archive = Assert.Single(OnlineFixMeListing.ParseFixRepairListing(html, folder));
        Assert.Equal(name, archive.Name);
        Assert.Equal(size, archive.SizeLabel);
        Assert.Equal(date, archive.DateLabel);
        Assert.Equal(new Uri(folder, "Fix%20Repair/" + name), archive.Url);
    }

    [Fact]
    public void FullGameFolderAndMissingRootCannotSupplyArchives()
    {
        Uri folder = OnlineFixMeListing.FolderFromName("Palworld")!;
        Assert.Empty(OnlineFixMeListing.ParseFixRepairListing(Fixture("palworld-game-folder.html"), folder));
        Assert.False(OnlineFixMeListing.LooksLikeIndex(Fixture("uploads-root-401.html")));
        Assert.Empty(OnlineFixMeListing.ParseFixRepairListing(Fixture("uploads-root-401.html"), folder));
    }

    [Fact]
    public void ChangedAttributesSortAndDeduplicateExactNamesAndOnlyReadDoubleQuotes()
    {
        var archives = OnlineFixMeListing.ParseFixRepairListing(Fixture("changed-autoindex.html"), Folder());
        Assert.Equal(["A_Fix_Repair.zip", "Raft Fix_Repair.rar", "Z_Fix_Repair.7z", "a_fix_repair.zip"],
            archives.Select(a => a.Name).ToArray());
        Assert.Null(archives[0].SizeLabel);
        Assert.Null(archives[0].DateLabel);
        Assert.Equal("11M", archives[2].SizeLabel);
        Assert.Equal("23-Jan-2026 10:31", archives[2].DateLabel);
        Assert.EndsWith("/Fix%20Repair/Raft%20Fix_Repair.rar", archives[1].Url.AbsoluteUri);
    }

    [Fact]
    public void HostileLinksAndMultipartOrFullGameArchivesAreSkipped()
    {
        var archive = Assert.Single(OnlineFixMeListing.ParseFixRepairListing(Fixture("hostile-autoindex.html"), Folder()));
        Assert.Equal("Safe_Fix_Repair.rar", archive.Name);
    }

    [Theory]
    [InlineData("23-Jan-2026 10:31", null, "23-Jan-2026 10:31")]
    [InlineData("11M", "11M", null)]
    [InlineData("23-Jan-2026 10:31 -", null, "23-Jan-2026 10:31")]
    [InlineData("changed columns", null, null)]
    public void PartialOrChangedMetadataIsBestEffort(string columns, string? size, string? date)
    {
        var archive = Assert.Single(OnlineFixMeListing.ParseFixRepairListing($"<a href=\"Game_Fix_Repair.rar\">patch</a> {columns}", Folder()));
        Assert.Equal(size, archive.SizeLabel);
        Assert.Equal(date, archive.DateLabel);
    }

    [Fact]
    public void OnlyFirst512HrefsAreExaminedEvenWhenTheyAreIneligible()
    {
        var archives = OnlineFixMeListing.ParseFixRepairListing(Fixture("many-hrefs.html"), Folder());
        Assert.Equal(512, archives.Count);
        Assert.Equal("0000_Fix_Repair.rar", archives[0].Name);
        Assert.Equal("0511_Fix_Repair.rar", archives[^1].Name);
        string html = string.Concat(Enumerable.Repeat("<a href=\"../\">parent</a>\n", 512))
            + "<a href=\"TooLate_Fix_Repair.rar\">patch</a>";
        Assert.Empty(OnlineFixMeListing.ParseFixRepairListing(html, Folder()));
    }

    [Fact]
    public void ListingAndNameBoundsRejectOversizedInput()
    {
        string html = "<title>Index of /uploads/Raft/Fix Repair/</title>" + new string('x', 1024 * 1024);
        Assert.False(OnlineFixMeListing.LooksLikeIndex(html));
        Assert.Empty(OnlineFixMeListing.ParseFixRepairListing(html, Folder()));
        Assert.Empty(OnlineFixMeListing.ParseFixRepairListing($"<a href=\"{new string('a', 201)}_Fix_Repair.rar\">patch</a>", Folder()));
        string unicode = "<title>Index of /</title>" + new string('é', 600000);
        Assert.False(OnlineFixMeListing.LooksLikeIndex(unicode));
        Assert.Empty(OnlineFixMeListing.ParseFixRepairListing(unicode, Folder()));
    }

    [Fact]
    public void CloudflareChallengeNeedsAnIndexTitle()
    {
        Assert.False(OnlineFixMeListing.LooksLikeIndex(Fixture("cloudflare-challenge.html")));
        Assert.False(OnlineFixMeListing.LooksLikeIndex("<h1>Index of /</h1>"));
        Assert.True(OnlineFixMeListing.LooksLikeIndex("<TITLE>Index of /uploads/Raft/Fix Repair/</TITLE>"));
    }

    [Fact]
    public void MatchingPreservesTitleThenTriesColonAndApostropheFallbacks()
    {
        Assert.Equal(["Raft"], OnlineFixMeListing.CandidateFolderNames("Raft"));
        Assert.Equal("https://uploads.online-fix.me:2053/uploads/Raft/", Folder().AbsoluteUri);
        Assert.Equal(["Hollow Knight: Silksong", "Hollow Knight Silksong"],
            OnlineFixMeListing.CandidateFolderNames("Hollow Knight: Silksong"));
        Assert.Equal(["Sid Meier's Civilization VII", "Sid Meiers Civilization VII"],
            OnlineFixMeListing.CandidateFolderNames("Sid Meier's Civilization VII"));
        Assert.Equal(["Sid Meier's: Civilization VII", "Sid Meier's Civilization VII", "Sid Meiers Civilization VII"],
            OnlineFixMeListing.CandidateFolderNames("Sid Meier's: Civilization VII"));
        Assert.Equal(["Tom Clancy’s Game", "Tom Clancys Game"], OnlineFixMeListing.CandidateFolderNames("Tom Clancy’s Game"));
        Assert.Equal(["Raft Game"], OnlineFixMeListing.CandidateFolderNames("  Raft™\t Game® ©  "));
        Assert.Equal(["R.E.P.O."], OnlineFixMeListing.CandidateFolderNames("R.E.P.O."));
        Assert.Equal([":Game", "Game"], OnlineFixMeListing.CandidateFolderNames(":Game"));
        Assert.Equal("https://uploads.online-fix.me:2053/uploads/Caf%C3%A9%20%26%20A%2BB%3A%20%27Game%27/",
            OnlineFixMeListing.FolderFromName("Café & A+B: 'Game'")!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a\0b")]
    public void InvalidTitleCannotBecomeFolder(string? name)
    {
        Assert.Empty(OnlineFixMeListing.CandidateFolderNames(name));
        Assert.Null(OnlineFixMeListing.FolderFromName(name!));
    }

    [Fact]
    public void FolderNamesHave200CharacterLimit()
    {
        Assert.NotNull(OnlineFixMeListing.FolderFromName(new string('a', 200)));
        Assert.Null(OnlineFixMeListing.FolderFromName(new string('a', 201)));
        Assert.Empty(OnlineFixMeListing.CandidateFolderNames(new string('a', 201)));
    }

    [Theory]
    [InlineData("Raft")]
    [InlineData("Raft/")]
    [InlineData("Raft/Fix%20Repair")]
    [InlineData("Raft/Fix%20Repair/")]
    [InlineData("Raft/Fix%20Repair/Raft_Fix_Repair_Steam.rar")]
    public void ManualFolderAcceptsOneGameOrItsRepairFolderOrFile(string suffix) =>
        Assert.Equal(Folder(), OnlineFixMeListing.FolderFromUserUrl(AppConfig.OnlineFixMeUploadsBase + suffix));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example:2053/uploads/Raft/")]
    [InlineData("https://uploads.online-fix.me.evil.example:2053/uploads/Raft/")]
    [InlineData("https://uploads.online-fix.me:443/uploads/Raft/")]
    [InlineData("http://uploads.online-fix.me:2053/uploads/Raft/")]
    [InlineData("https://user@uploads.online-fix.me:2053/uploads/Raft/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Other/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/?x=1")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/#fragment")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft%2FGame/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft%5CGame/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/../Raft/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/%2e%2e/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft%/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft%FF/")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/../escape.rar")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/..")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/file.rar?x=1")]
    [InlineData("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/file%2Frar")]
    public void ManualFolderRejectsOtherOriginsAndUnsafeOrExtraSegments(string? url) =>
        Assert.Null(OnlineFixMeListing.FolderFromUserUrl(url));

    [Theory]
    [InlineData("Game_FiX_RePaIr.rar", true)]
    [InlineData("Game_Fix_Repair.ZIP", true)]
    [InlineData("Game_Fix_Repair.7z", true)]
    [InlineData("Game_Fix_Repair.part1.rar", false)]
    [InlineData("Game_Fix_Repair.part123.RAR", false)]
    [InlineData("Game_Fix_Repair.r01", false)]
    [InlineData("Game_Fix_Repair.z01", false)]
    [InlineData("Game_Fix_Repair.7z.001", false)]
    [InlineData("FullGame.rar", false)]
    [InlineData("Game_Fix_Repair.rar.exe", false)]
    [InlineData("../Game_Fix_Repair.rar", false)]
    public void EligibilityRequiresSingleVolumePatchArchive(string name, bool eligible) =>
        Assert.Equal(eligible, OnlineFixMeListing.IsEligibleArchiveName(name));

    [Fact]
    public void RepairUrlRejectsNonGameFolders()
    {
        Assert.Equal("https://uploads.online-fix.me:2053/uploads/Raft/Fix%20Repair/", OnlineFixMeListing.FixRepairUrl(Folder()).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => OnlineFixMeListing.FixRepairUrl(new Uri("https://evil.example/")));
        Assert.Throws<ArgumentException>(() => OnlineFixMeListing.FixRepairUrl(new Uri(Folder(), "Fix%20Repair/")));
        Assert.Empty(OnlineFixMeListing.ParseFixRepairListing(Fixture("raft-fix-repair.html"), new Uri("https://evil.example/")));
    }

    internal static string Fixture(string name)
    {
        using var stream = typeof(OnlineFixMeListingTests).Assembly.GetManifestResourceStream("LuaToolsGui.Tests.Fixtures.OnlineFixMe." + name)
            ?? throw new InvalidOperationException("Missing Online-Fix.me fixture.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Uri Folder() => OnlineFixMeListing.FolderFromName("Raft")!;
}
