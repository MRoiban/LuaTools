using System.IO;
using System.Runtime.CompilerServices;
using CabinetEngine;
using LuaToolsGui.Resources;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

// The engine self-tests swap static hooks (manifest fetcher, snapshot delays); keep them serial.
[CollectionDefinition("CabinetEngine", DisableParallelization = true)]
public sealed class CabinetEngineCollection { }

[Collection("CabinetEngine")]
public sealed class CabinetSavesTests
{
    // CabinetService's static constructor installs the policy fetcher; run it before any self-test
    // replaces the fetcher, so it can never swap the real one back in mid-test.
    static CabinetSavesTests() => RuntimeHelpers.RunClassConstructor(typeof(CabinetService).TypeHandle);

    [Fact]
    public void SharedSaveEngineSelfTestPasses() => SaveSelfTest.Run();

    [Fact]
    public void CloudSelfTestPassesAgainstLocalFakeServer() => CloudSelfTest.Run();

    [Fact]
    public void ManifestSourceIsAllowedByTheOutboundPolicy()
    {
        new OutboundNetworkPolicy().Validate(new Uri(SaveManifestCache.Url));
        Assert.Equal("raw.githubusercontent.com", new Uri(SaveManifestCache.Url).Host);
        var fetch = SaveManifestCache.Fetcher;
        Assert.Equal(nameof(CabinetService.FetchManifest), fetch.Method.Name);
    }

    private static SaveGameStatus Status(string status, int files, string discovery = SaveStatus.Found) =>
        new() { AppId = "1139980", Name = "Travellers Rest", Status = status, Files = files, Bytes = files * 10, Discovery = discovery };

    [Fact]
    public void NoSaveFilesIsNeverReportedSynced()
    {
        var mapped = CabinetService.MapSaves(new[]
        {
            Status(SaveSyncStatus.Synced, 0, SaveStatus.Empty),
            Status(SaveSyncStatus.Synced, 0, SaveStatus.Missing),
            Status(SaveSyncStatus.Empty, 0, SaveStatus.Empty),
            Status(SaveSyncStatus.Missing, 0, SaveStatus.Unsupported),
        }, new Dictionary<string, string>(), connected: true);
        Assert.Equal(new[] { CabinetSaveState.NoFiles, CabinetSaveState.NotFound, CabinetSaveState.NoFiles, CabinetSaveState.NotFound }, mapped.Select(m => m.State));
    }

    [Fact]
    public void PendingWithoutConnectionSaysNotConnected()
    {
        var mapped = CabinetService.MapSaves(new[] { Status(SaveSyncStatus.Pending, 3) }, new Dictionary<string, string>(), connected: false);
        Assert.Equal(CabinetSaveState.NotConnected, mapped.Single().State);
    }

    [Fact]
    public void RestorePreviewKeepsEveryDestinationAndTheBackup()
    {
        var plan = new SaveRestorePlan { GameId = "steam:1", BackupPath = @"C:\b\1.zip" };
        plan.Items.Add(new SaveRestoreItem { Action = "replace", Destination = @"C:\s\a.save", Size = 3 });
        plan.Items.Add(new SaveRestoreItem { Action = "create", Destination = @"C:\s\b.save", Size = 4 });
        plan.Deletions.Add(new SaveRestoreItem { Action = "delete", Destination = @"C:\s\old.save" });
        var preview = CabinetService.ToPreview(plan);
        Assert.True(preview.CanApply);
        Assert.Equal(new[] { CabinetRestoreAction.Replace, CabinetRestoreAction.Create, CabinetRestoreAction.Delete }, preview.Items.Select(i => i.Action));
        string text = CabinetViewModel.FormatPreview(preview);
        foreach (var path in new[] { @"C:\b\1.zip", @"C:\s\a.save", @"C:\s\b.save", @"C:\s\old.save" }) Assert.Contains(path, text);
    }

    private static CabinetSaveGame Game(CabinetSaveState state, int files = 3, int cloudFiles = 3, string? overrideMode = null) => new(
        "1139980", "Travellers Rest", state, files > 0 ? "found" : "empty",
        new[] { new CabinetSaveRoot("manifest:<winLocalAppDataLow>/Louqou/TravellersRest", "manifest", @"C:\Users\A\AppData\LocalLow\Louqou\TravellersRest", files > 0 ? "found" : "empty", files, files * 10, null) },
        files, files * 10, cloudFiles, cloudFiles * 10, null, null, null, overrideMode, "abc@2026-10-24", Array.Empty<string>());

    [Fact]
    public void RowsShowLocalizedStatesAndOnlyRelevantActions()
    {
        var service = new FakeCabinetService();
        using var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()), null, new FakeSaves(), new FakeDialogs());
        service.Publish(CabinetStatus.Off with
        {
            Enabled = true,
            Saves = new[] { Game(CabinetSaveState.NoFiles, 0, 0), Game(CabinetSaveState.NotFound, 0, 0), Game(CabinetSaveState.Conflict), Game(CabinetSaveState.Synced) },
        });
        Assert.True(vm.HasSaveGames);
        Assert.Equal(Strings.CabinetSaves_State_NoFiles, vm.SaveGames[0].StatusText);
        Assert.Equal(Strings.CabinetSaves_State_NotFound, vm.SaveGames[1].StatusText);
        Assert.DoesNotContain(vm.SaveGames.Take(2), r => r.StatusText == Strings.CabinetSaves_State_Synced);
        Assert.True(vm.SaveGames[2].ShowConflictActions); Assert.False(vm.SaveGames[2].ShowRestore);
        Assert.False(vm.SaveGames[3].ShowConflictActions); Assert.True(vm.SaveGames[3].ShowRestore); Assert.True(vm.SaveGames[3].ShowDelete);
        Assert.Contains(@"C:\Users\A\AppData\LocalLow\Louqou\TravellersRest", vm.SaveGames[3].Locations);
        Assert.Contains("abc@2026-10-24", vm.SaveAttribution);
    }

    [Fact]
    public async Task RestoreShowsThePlanAndNeedsConfirmation()
    {
        var saves = new FakeSaves(); var dialogs = new FakeDialogs();
        var service = new FakeCabinetService();
        using var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()), null, saves, dialogs);
        service.Publish(CabinetStatus.Off with { Enabled = true, Saves = new[] { Game(CabinetSaveState.LocalMissing, 0, 3) } });
        saves.Preview = new CabinetRestorePreview(null, new[] { new CabinetRestoreItem(CabinetRestoreAction.Create, @"C:\x\a.save", 3) }, Array.Empty<string>(), true);
        dialogs.ConfirmResult = false;
        await vm.SaveGames[0].RestoreCommand.ExecuteAsync(null);
        Assert.Equal(0, saves.Restores); Assert.Contains(@"C:\x\a.save", dialogs.LastMessage);
        dialogs.ConfirmResult = true;
        await vm.SaveGames[0].RestoreCommand.ExecuteAsync(null);
        Assert.Equal(1, saves.Restores);
        saves.Preview = new CabinetRestorePreview(null, Array.Empty<CabinetRestoreItem>(), new[] { "override:1: no matching save location on this PC" }, false);
        await vm.SaveGames[0].RestoreCommand.ExecuteAsync(null);
        Assert.Equal(1, saves.Restores); Assert.Contains("override:1", dialogs.LastMessage);
    }

    [Fact]
    public async Task EditLocationSupplementsOrReplaces()
    {
        var saves = new FakeSaves(); var dialogs = new FakeDialogs { Folder = @"D:\Saves\TR", Replace = false };
        var service = new FakeCabinetService();
        using var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()), null, saves, dialogs);
        service.Publish(CabinetStatus.Off with { Enabled = true, Saves = new[] { Game(CabinetSaveState.NotFound, 0, 0) } });
        await vm.SaveGames[0].EditLocationCommand.ExecuteAsync(null);
        Assert.Equal(("1139980", "supplement", @"D:\Saves\TR"), (saves.OverrideApp, saves.OverrideMode, saves.OverrideFolders!.Single()));
        dialogs.Replace = null; saves.OverrideApp = null;
        await vm.SaveGames[0].EditLocationCommand.ExecuteAsync(null);
        Assert.Null(saves.OverrideApp);
        saves.Fail = new InvalidOperationException("boom");
        await vm.RescanSavesCommand.ExecuteAsync(null);
        Assert.Contains("boom", vm.SaveError);
    }

    // A copy of real save files (never the originals): set CABINET_REAL_SAVES_COPY to a folder that
    // holds a copied GameSaves tree. It uploads from one simulated profile and restores into another.
    [Fact]
    public void RealSaveCopyRoundTripsBetweenSimulatedProfiles()
    {
        string? copy = Environment.GetEnvironmentVariable("CABINET_REAL_SAVES_COPY");
        if (string.IsNullOrEmpty(copy) || !Directory.Exists(copy)) return;
        string root = Path.Combine(Path.GetTempPath(), "cabinet-real-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var index = SaveSelfTest.FixtureIndex();
            SaveEnvironment Profile(string user)
            {
                string home = Path.Combine(root, "Users", user);
                var e = new SaveEnvironment { Home = home, OsUserName = user, AppData = Path.Combine(home, "AppData", "Roaming"), LocalAppData = Path.Combine(home, "AppData", "Local"), LocalLow = Path.Combine(home, "AppData", "LocalLow"), Documents = Path.Combine(home, "Documents") };
                Directory.CreateDirectory(e.LocalLow);
                return e;
            }
            var a = Profile("Alex"); var b = Profile("Second");
            string source = Path.Combine(a.LocalLow, "Louqou", "TravellersRest", "GameSaves");
            foreach (var file in Directory.GetFiles(copy, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(source, Path.GetRelativePath(copy, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
            var cloud = new MemoryCloud();
            var sa = new SaveSync(Path.Combine(root, "dataA"), null, () => a) { GameRunning = (_, _) => false };
            var sb = new SaveSync(Path.Combine(root, "dataB"), null, () => b) { GameRunning = (_, _) => false };
            sa.Manifest.Use(index); sb.Manifest.Use(index); sa.UseAccount("http://fake", "u"); sb.UseAccount("http://fake", "u");
            sa.Sync("1139980", null, null, cloud, _ => false, a, true);
            Assert.Equal(SaveSyncStatus.Synced, sa.StatusOf("1139980")!.Status);
            sb.Sync("1139980", cloud.Meta, null, cloud, _ => false, b, true);
            Assert.Equal(SaveSyncStatus.Synced, sb.StatusOf("1139980")!.Status);
            string restored = Path.Combine(b.LocalLow, "Louqou", "TravellersRest", "GameSaves");
            var expected = Directory.GetFiles(copy, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(copy, f)).OrderBy(x => x).ToList();
            Assert.Equal(expected, Directory.GetFiles(restored, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(restored, f)).OrderBy(x => x));
            foreach (var rel in expected) Assert.Equal(File.ReadAllBytes(Path.Combine(copy, rel)), File.ReadAllBytes(Path.Combine(restored, rel)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Opt-in visual check (LUATOOLS_RENDER_CABINET=<png path>): loads CabinetView with App's
    // resources and sample save states, renders it and saves a PNG. Off in CI: it creates a WPF Application.
    [Fact]
    public void CabinetViewRendersSaveStates()
    {
        string? output = Environment.GetEnvironmentVariable("LUATOOLS_RENDER_CABINET");
        if (string.IsNullOrEmpty(output)) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = System.Windows.Application.Current ?? new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources["BoolToVis"] = new System.Windows.Controls.BooleanToVisibilityConverter();
                app.Resources["NullToCollapsed"] = new NullToCollapsedConverter();
                app.Resources["InverseBoolToVis"] = new InverseBoolToVisibilityConverter();
                var service = new FakeCabinetService();
                var vm = new CabinetViewModel(service, new SettingsService(new AppSettings()), null, new FakeSaves(), new FakeDialogs());
                var tr = new CabinetSaveGame("1139980", "Travellers Rest", CabinetSaveState.Synced, "found", new[]
                {
                    new CabinetSaveRoot("manifest:<winLocalAppDataLow>/Louqou/TravellersRest", "manifest", @"C:\Users\Alex\AppData\LocalLow\Louqou\TravellersRest", "found", 3, 13427, null),
                    new CabinetSaveRoot("onlinefix:Saves", "onlinefix", @"C:\Users\Public\Documents\OnlineFix\1139980\Saves", "empty", 0, 0, null),
                }, 3, 13427, 3, 13427, DateTimeOffset.Now.AddMinutes(-5), null, null, null, "e8e10f67b377@2026-10-24", Array.Empty<string>());
                service.Publish(CabinetStatus.Off with
                {
                    Enabled = true,
                    Cloud = new CabinetCloudStatus(true, "Alex", "cabinet.mc2.onl", null),
                    Saves = new[]
                    {
                        tr,
                        tr with { AppId = "2929250", Name = "Over the Hill", State = CabinetSaveState.NoFiles, Files = 0, Bytes = 0, CloudFiles = 0, CloudBytes = 0, LastUpload = null,
                            Roots = new[] { new CabinetSaveRoot("onlinefix:Saves", "onlinefix", @"C:\Users\Public\Documents\OnlineFix\2929250\Saves", "empty", 0, 0, null) } },
                        tr with { AppId = "632360", Name = "Risk of Rain 2", State = CabinetSaveState.Conflict },
                        tr with { AppId = "3000", Name = "Registry Game", State = CabinetSaveState.NotFound, Files = 0, Bytes = 0, CloudFiles = 0, Roots = Array.Empty<CabinetSaveRoot>(), Notes = new[] { "unsupported: registry: HKEY_CURRENT_USER/Software/Foo" } },
                    },
                });
                var view = new LuaToolsGui.Views.CabinetView(vm) { Width = 900, Height = 1100 };
                var surface = new System.Windows.Controls.Border { Child = view, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)) };
                var window = new System.Windows.Window { Content = surface, Width = 920, Height = 1140, WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, Left = -4000, Top = -4000, Background = System.Windows.Media.Brushes.Black };
                window.Show();
                view.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(900, 1100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var file = File.Create(output)) encoder.Save(file);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class MemoryCloud : ISaveCloud
    {
        public SaveCloudMeta? Meta; private byte[]? _bytes;
        public SavePutResult Put(string appid, SaveArchive archive, string baseHash, bool deleteIntent)
        {
            var read = SaveArchive.Read(archive.Bytes, "steam:" + appid);
            if (read.IsEmpty) return new SavePutResult { Status = 422, Error = "empty_archive" };
            Meta = new SaveCloudMeta { Id = Guid.NewGuid().ToString("N"), ContentHash = read.ContentHash, Files = read.Files.Count, Bytes = read.ExpandedBytes, Size = archive.Bytes.Length };
            _bytes = archive.Bytes;
            return new SavePutResult { Status = 200, Meta = Meta };
        }
        public byte[]? Get(string appid, string versionId) => _bytes;
        public byte[]? GetLegacy(string appid) => null;
    }

    private sealed class FakeSaves : ICabinetSaves
    {
        public CabinetRestorePreview Preview = new(null, Array.Empty<CabinetRestoreItem>(), Array.Empty<string>(), false);
        public int Restores; public string? OverrideApp, OverrideMode; public IReadOnlyList<string>? OverrideFolders; public Exception? Fail;
        private Task Run(Action action) { if (Fail is not null) return Task.FromException(Fail); action(); return Task.CompletedTask; }
        public Task RescanAsync(CancellationToken cancellationToken = default) => Run(() => { });
        public Task SetOverrideAsync(string appId, string mode, IReadOnlyList<string> folders, bool? sync, CancellationToken cancellationToken = default) =>
            Run(() => { OverrideApp = appId; OverrideMode = mode; OverrideFolders = folders; });
        public Task ResetOverrideAsync(string appId, CancellationToken cancellationToken = default) => Run(() => { });
        public Task<CabinetRestorePreview> PreviewRestoreAsync(string appId, CancellationToken cancellationToken = default) => Task.FromResult(Preview);
        public Task RestoreAsync(string appId, CancellationToken cancellationToken = default) => Run(() => Restores++);
        public Task KeepLocalAsync(string appId, CancellationToken cancellationToken = default) => Run(() => { });
        public Task DeleteCloudAsync(string appId, CancellationToken cancellationToken = default) => Run(() => { });
    }

    private sealed class FakeDialogs : ICabinetSaveDialogs
    {
        public string? Folder; public bool? Replace = false; public bool ConfirmResult; public string LastMessage = "";
        public string? PickFolder(string? initial) => Folder;
        public bool? AskReplace(string folder) => Replace;
        public bool Confirm(string title, string message) { LastMessage = message; return ConfirmResult; }
        public void Inform(string title, string message) => LastMessage = message;
    }
}
