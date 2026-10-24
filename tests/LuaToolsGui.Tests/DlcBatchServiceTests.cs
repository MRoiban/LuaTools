using System.IO;
using System.Net;
using System.Text;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>"Add all DLC" (Steam plugin): verify everything first, write nothing until Apply.</summary>
public class DlcBatchServiceTests
{
    // Shaped on Total War: WARHAMMER III (1142710), verified live 2026-10-24.
    private const long Game = 1142710, Wh2 = 594570;
    private const long NoDepots = 1670540, Keyed = 1374300, Owned = 3858050, Added = 3589670;
    private const long LegacyWh2 = 1556110, NotDlc = 999001, MissingKeys = 3589680;

    private sealed class Fake
    {
        public bool Guest;
        public GameDetails? GameDetails = new() { AppId = Game, Name = "Total War: WARHAMMER III", Type = "game" };
        public List<long> Declared = [NoDepots, Keyed, Owned, Added, MissingKeys];
        public List<long> Store = [NoDepots, LegacyWh2, NotDlc, Game];
        public readonly List<(string Dlc, string Base)> InfoCalls = [];
        public readonly List<(string Dlc, string Base)> GenerateCalls = [];
        public readonly Dictionary<long, string> Installed = [];
        public Func<string, string, DlcInfo>? Info;
        public Exception? GenerateError;

        public DlcBatchService Service() => new(new DlcBatchService.Ports(
            () => Guest,
            (id, _) => Task.FromResult(id == Game ? GameDetails
                : id == LegacyWh2 ? new GameDetails { AppId = id, Name = "The Silence & The Fury", Type = "dlc", BaseAppId = Wh2.ToString() }
                : id == NotDlc ? new GameDetails { AppId = id, Name = "Soundtrack bundle", Type = "music" } : null),
            (_, _) => Task.FromResult<IReadOnlyList<long>>(Declared),
            _ => Store,
            id => id == Added,
            (dlc, b, _) =>
            {
                lock (InfoCalls) InfoCalls.Add((dlc, b));
                if (Info is not null) return Task.FromResult<DlcInfo?>(Info(dlc, b));
                return Task.FromResult<DlcInfo?>(dlc == Keyed.ToString()
                    ? new DlcInfo { AppId = b, DepotCount = 3, HaveCount = 3, MissingCount = 0 }
                    : dlc == MissingKeys.ToString()
                        ? new DlcInfo { AppId = b, DepotCount = 1, HaveCount = 0, MissingCount = 1 }
                        : new DlcInfo { AppId = b });
            },
            (dlc, b, _) =>
            {
                GenerateCalls.Add((dlc, b));
                if (GenerateError is not null) throw GenerateError;
                string path = Path.Combine(Path.GetTempPath(), $"dlcbatch-{Guid.NewGuid():N}.lua");
                File.WriteAllText(path, $"addappid({dlc})\naddappid(1143201,1,\"{new string('a', 64)}\")\n");
                return Task.FromResult(new DownloadedFile(path, $"{dlc}.lua"));
            },
            (path, id) =>
            {
                ManifestContentPolicy.ValidateLuaFile(path, id); // the installer's own content gate
                Installed[id] = File.ReadAllText(path);
                return new InstallResult(true, 0, [], null);
            }));
    }

    private static async Task<DlcBatchService.Batch> CheckAsync(DlcBatchService svc, params long[] owned)
    {
        Assert.True(svc.Check(Game, owned));
        var batch = svc.GetState(Game)!;
        await batch.Work.WaitAsync(TimeSpan.FromSeconds(10));
        return batch;
    }

    private static string StateOf(DlcBatchService.Batch b, long id) => b.Items.Single(i => i.AppId == id).State;

    [Fact]
    public async Task CheckVerifiesEveryDlcAgainstItsOwnParentAndWritesNothing()
    {
        var fake = new Fake();
        var batch = await CheckAsync(fake.Service(), Owned);

        Assert.Equal("ready", batch.Phase);
        Assert.Equal("ready", StateOf(batch, NoDepots));
        Assert.False(batch.Items.Single(i => i.AppId == NoDepots).Download);
        Assert.True(batch.Items.Single(i => i.AppId == Keyed).Download);
        Assert.Equal("owned", StateOf(batch, Owned));
        Assert.Equal("added", StateOf(batch, Added));
        Assert.Equal("unavailable", StateOf(batch, MissingKeys));
        Assert.Equal("not-dlc", StateOf(batch, NotDlc));
        Assert.DoesNotContain(batch.Items, i => i.AppId == Game);       // the game itself is never a DLC row
        Assert.Single(batch.Items, i => i.AppId == NoDepots);            // listed twice, checked once

        // The WARHAMMER II pack the WARHAMMER III store page lists is verified against WARHAMMER II.
        var legacy = batch.Items.Single(i => i.AppId == LegacyWh2);
        Assert.Equal(("ready", Wh2), (legacy.State, legacy.BaseAppId));
        Assert.Contains((LegacyWh2.ToString(), Wh2.ToString()), fake.InfoCalls);
        Assert.DoesNotContain(fake.InfoCalls, c => c.Dlc == Owned.ToString() || c.Dlc == Added.ToString());
        Assert.Empty(fake.Installed);
        Assert.Empty(fake.GenerateCalls);
    }

    [Fact]
    public async Task ApplyWritesNoDepotDlcLocallyAndGeneratesOnlyKeyedOnes()
    {
        var fake = new Fake();
        var svc = fake.Service();
        await CheckAsync(svc, Owned);
        Assert.True(svc.Apply(Game));
        var batch = svc.GetState(Game)!;
        await batch.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("done", batch.Phase);
        Assert.Equal($"addappid({NoDepots})\n", fake.Installed[NoDepots]);
        Assert.Equal($"addappid({LegacyWh2})\n", fake.Installed[LegacyWh2]);
        Assert.Equal([(Keyed.ToString(), Game.ToString())], fake.GenerateCalls); // one download for the whole batch
        Assert.Contains("addappid(1143201,1,", fake.Installed[Keyed]);
        Assert.Equal(new[] { NoDepots, Keyed, LegacyWh2 }.Order(), fake.Installed.Keys.Order());
        Assert.All(new[] { NoDepots, Keyed, LegacyWh2 }, id => Assert.Equal("installed", StateOf(batch, id)));
        Assert.Equal("owned", StateOf(batch, Owned));
        Assert.Equal("unavailable", StateOf(batch, MissingKeys));
        Assert.False(svc.Apply(Game)); // nothing left that verified as ready
    }

    [Fact]
    public async Task DailyLimitOnlyStopsKeyedDlc()
    {
        var fake = new Fake { GenerateError = new ApiException("limit", HttpStatusCode.TooManyRequests) };
        var svc = fake.Service();
        await CheckAsync(svc);
        Assert.True(svc.Apply(Game));
        var batch = svc.GetState(Game)!;
        await batch.Work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("limit", StateOf(batch, Keyed));
        Assert.Equal("installed", StateOf(batch, NoDepots));
        Assert.Equal("installed", StateOf(batch, Owned)); // nothing reported it owned this time
    }

    [Fact]
    public async Task GuestsAndDlcPagesAreRefusedBeforeAnyLookup()
    {
        var guest = new Fake { Guest = true };
        var batch = await CheckAsync(guest.Service());
        Assert.Equal(("failed", LuaToolsGui.Resources.Strings.Add_SignIn_Dlc), (batch.Phase, batch.Error));
        Assert.Empty(guest.InfoCalls);

        var dlcPage = new Fake { GameDetails = new GameDetails { AppId = Game, Name = "x", Type = "dlc", BaseAppId = "1" } };
        batch = await CheckAsync(dlcPage.Service());
        Assert.Equal(("failed", LuaToolsGui.Resources.Strings.DlcBatch_Err_NotAGame), (batch.Phase, batch.Error));
        Assert.Empty(dlcPage.InfoCalls);
    }

    [Fact]
    public async Task OneUndescribableDlcIsSkippedButAnExpiredSessionStopsTheBatch()
    {
        var odd = new Fake { Info = (dlc, b) => dlc == NoDepots.ToString() ? throw new ApiException("Invalid DLC response.") : new DlcInfo { AppId = b } };
        var batch = await CheckAsync(odd.Service());
        Assert.Equal("ready", batch.Phase);
        Assert.Equal("unavailable", StateOf(batch, NoDepots));
        Assert.Equal("ready", StateOf(batch, Keyed));

        var expired = new Fake { Info = (_, _) => throw new ApiException("Session expired.", HttpStatusCode.Unauthorized) };
        batch = await CheckAsync(expired.Service());
        Assert.Equal(("failed", "Session expired."), (batch.Phase, batch.Error));
    }

    [Fact]
    public async Task ApplyNeedsAFinishedCheckAndCancelStopsIt()
    {
        var fake = new Fake();
        var svc = fake.Service();
        Assert.False(svc.Apply(Game));
        await CheckAsync(svc);
        Assert.True(svc.Cancel(Game));
        Assert.Equal("cancelled", svc.GetState(Game)!.Phase);
        Assert.False(svc.Apply(Game));
        Assert.Empty(fake.Installed);
    }

    [Fact]
    public void LocalLuaPassesTheInstallerContentPolicyAndStoreListParses()
    {
        ManifestContentPolicy.ValidateLua(Encoding.UTF8.GetBytes(DlcBatchService.LocalLua(NoDepots)), NoDepots);
        Assert.Equal([404010L, 1556110L], DlcBatchService.StoreDlcIds("{\"dlc\":[404010,1556110,\"x\",-0.5]}").Where(i => i > 0));
        Assert.Empty(DlcBatchService.StoreDlcIds("{\"name\":\"no dlc\"}"));
        Assert.Empty(DlcBatchService.StoreDlcIds("not json"));
    }
}
