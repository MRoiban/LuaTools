using System.IO;
using System.Globalization;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Cabinet;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class CabinetSettingsTests
{
    internal sealed class Startup : IStartupRegistration
    {
        public bool IsEnabled { get; set; }
        public int Writes { get; private set; }
        public void SetEnabled(bool enabled) { IsEnabled = enabled; Writes++; }
    }
    [Fact]
    public void RetiredSettingsKeysAreIgnoredWhenLoadingOldJson()
    {
        var path = Path.Combine(Path.GetTempPath(), "CabinetSettingsTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            // Compose retired key names so repository searches can enforce removal of their API.
            File.WriteAllText(Path.Combine(path, "settings.json"), "{\"CabinetEnabled\":true,\"CabinetController" +
                "Layouts\":true,\"CabinetScreenshot" + "Attribution\":true,\"CabinetWorkshopLinks\":true}");
            var loaded = new SettingsService(null, path, new Startup());
            Assert.True(loaded.CabinetEnabled);
            Assert.True(loaded.CabinetWorkshopLinks);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact]
    public void DefaultsAndReadingStoredAutostartNeverWriteRegistry()
    {
        var registry = new Startup();
        var settings = new SettingsService(new AppSettings { StartWithWindows = true }, startup: registry);
        Assert.False(settings.CabinetEnabled); Assert.True(settings.CabinetLivePlaytime); Assert.True(settings.CabinetFetchSchemas);
        Assert.True(settings.CabinetWorkshopLinks); // Cabinet parity: Workshop links on by default
        Assert.False(settings.StartWithWindows); Assert.Equal(0, registry.Writes);
        registry.IsEnabled = true; Assert.True(settings.StartWithWindows); Assert.Equal(0, registry.Writes);
        settings.StartWithWindows = false; Assert.False(registry.IsEnabled); Assert.Equal(1, registry.Writes);
        settings.StartWithWindows = true; Assert.True(registry.IsEnabled); Assert.Equal(2, registry.Writes);
    }
    [Fact]
    public void CabinetChoicesPersistIndependentlyAndDoNotWriteAutostart()
    {
        var path = Path.Combine(Path.GetTempPath(), "CabinetSettingsTests", Guid.NewGuid().ToString("N"));
        var registry = new Startup();
        try
        {
            var settings = new SettingsService(new AppSettings(), path, registry);
            settings.CabinetEnabled = true; settings.CabinetLivePlaytime = false; settings.CabinetFetchSchemas = false;
            settings.CabinetWorkshopLinks = true;
            var loaded = new SettingsService(null, path, registry);
            Assert.True(loaded.CabinetEnabled); Assert.False(loaded.CabinetLivePlaytime); Assert.False(loaded.CabinetFetchSchemas);
            Assert.True(loaded.CabinetWorkshopLinks);
            loaded.CabinetEnabled = false;
            Assert.True(new SettingsService(null, path, registry).CabinetWorkshopLinks);
            Assert.Equal(0, registry.Writes);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact]
    public void ViewModelInitializesWithoutSavingThenEveryCabinetChangeNotifiesEngine()
    {
        var settings = new SettingsService(new AppSettings()); var service = new FakeCabinetService();
        var vm = new SettingsViewModel(settings, new AuthService(), new SteamService(settings), new HubcapService(), service);
        Assert.Equal(0, service.Changes); Assert.False(vm.CabinetEnabled); Assert.True(vm.CabinetLivePlaytime); Assert.False(vm.CabinetOptionsEnabled);
        vm.CabinetEnabled = true; vm.CabinetLivePlaytime = false; vm.CabinetFetchSchemas = false;
        vm.CabinetWorkshopLinks = false;
        Assert.True(vm.CabinetOptionsEnabled);
        Assert.Equal(4, service.Changes); Assert.True(settings.CabinetEnabled); Assert.False(settings.CabinetLivePlaytime);
        Assert.False(settings.CabinetWorkshopLinks);
    }
    [Fact]
    public void TurningMasterOffDisablesOptionsAndRefreshDoesNotReconfigureEngine()
    {
        var settings = new SettingsService(new AppSettings { CabinetEnabled = true, CabinetWorkshopLinks = true });
        var service = new FakeCabinetService();
        var vm = new SettingsViewModel(settings, new AuthService(), new SteamService(settings), new HubcapService(), service);
        Assert.True(vm.CabinetOptionsEnabled);
        vm.CabinetEnabled = false;
        Assert.False(vm.CabinetOptionsEnabled); Assert.True(vm.CabinetWorkshopLinks); Assert.True(settings.CabinetWorkshopLinks);
        settings.CabinetEnabled = true;
        vm.RefreshCabinetSettings();
        Assert.True(vm.CabinetOptionsEnabled); Assert.Equal(1, service.Changes);
    }
    [Theory]
    [InlineData("en", "Achievements synced", "Everything is already", "Playtime added", "achievement lists", "Workshop links", "Game|3")]
    [InlineData("fr", "Succès synchronisés", "Tout est déjà", "Temps de jeu ajouté", "listes de succès", "liens Workshop", "Game|3")]
    public void AllNotificationKindsAreLocalized(string language, params string[] expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            foreach (var kind in Enum.GetValues<CabinetNotificationKind>())
            {
                var result = CabinetNotificationLocalizer.Format(new(kind, ["Game|3"], 3));
                Assert.Contains(expected[(int)kind], result);
                Assert.DoesNotContain("Cabinet_", result); Assert.DoesNotContain("{0}", result);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}

