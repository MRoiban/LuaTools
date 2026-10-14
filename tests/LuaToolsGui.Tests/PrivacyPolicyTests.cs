using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class PrivacyPolicyTests
{
    [Fact]
    public void ApiKeyIsProtectedAtRestAndRejectsPlaintextOrCorruption()
    {
        const string key = "smm_test-only-credential";
        string? protectedKey = SettingsService.ProtectApiKey(key);
        Assert.NotNull(protectedKey);
        Assert.DoesNotContain(key, protectedKey);
        Assert.Equal(key, SettingsService.UnprotectApiKey(protectedKey));
        Assert.Null(SettingsService.UnprotectApiKey(key));
        Assert.Null(SettingsService.UnprotectApiKey("dpapi:v1:not-base64"));
        Assert.Null(SettingsService.UnprotectApiKey("dpapi:v1:AQIDBA=="));
        var loaded = new SettingsService(new AppSettings { HubcapApiKey = key });
        Assert.Null(loaded.HubcapApiKey);
    }

    [Fact]
    public void LegacySettingsCannotEnableDonationOrAutomaticStartup()
    {
        var legacy = new AppSettings { DonateKeys = true, StartWithWindows = true };
        var settings = new SettingsService(legacy);

        Assert.False(settings.DonateKeys);
        Assert.False(settings.StartWithWindows);
        // Even an in-memory legacy value reintroduced after loading is ignored by the policy getters.
        legacy.DonateKeys = true;
        Assert.False(settings.DonateKeys);
    }

    [Theory]
    [InlineData(null, "Never")]
    [InlineData("unknown", "Never")]
    [InlineData("", "Never")]
    [InlineData("Auto", "Auto")]
    [InlineData("Always", "Always")]
    [InlineData("Never", "Never")]
    public void DnsPreferenceDefaultsToSystemDnsAndPreservesExplicitChoices(string? stored, string expected)
    {
        var state = new AppSettings { DnsMode = stored };
        var settings = new SettingsService(state);
        Assert.Equal(expected, settings.DnsMode);
        Assert.Equal(stored, state.DnsMode); // Reading policy does not rewrite settings or touch disk.
        state.DnsMode = "Always";
        Assert.Equal("Always", settings.DnsMode);
        state.DnsMode = "Auto";
        Assert.Equal("Auto", settings.DnsMode);
    }

    [Fact]
    public async Task DisabledServicesNeverStageUpdatesOrTransmitKeys()
    {
        var updates = new UpdateService();
        await updates.CheckAndStageAsync();
        updates.ApplyAndRestart(["--tray-locked"]);
        updates.ApplyOnExit();
        Assert.False(updates.HasStagedUpdate);
        await new DonateKeysService().SendPendingKeysIfEnabledAsync();
        await new AnalyticsService().TrackAppLaunchAsync();
        Assert.Null(App.RunUpdateFlow);
    }

    [Fact]
    public void DataAndMirrorConfigurationDoesNotReuseUpstreamTrust()
    {
        Assert.NotEqual("LuaToolsGui", AppConfig.DataDirectoryName);
        Assert.All(AppConfig.GithubApiMirrors.Concat(AppConfig.GithubDownloadMirrors), mirror =>
            Assert.Equal("https", new Uri(mirror).Scheme));
        Assert.Empty(AppConfig.ManifestBackendUrl);
    }
}
