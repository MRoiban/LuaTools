using System.IO;
using System.Reflection;
using System.Text.Json;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class ExecutableWorkflowSettingsTests
{
    private static readonly (string Property, string Component, string? Alias)[] Workflows =
    [
        (nameof(SettingsService.ExecutableGameFixesEnabled), "DenuvoFix", null),
        (nameof(SettingsService.BetterSteamToolsEnabled), "BetterSteamTools", null),
        (nameof(SettingsService.OpenSteamToolsEnabled), "OpenSteamTools", null),
        (nameof(SettingsService.SteamPluginEnabled), "LuaToolsPlugin", "LuaToolsPluginFrontend"),
        (nameof(SettingsService.DepotDownloaderEnabled), "DepotDownloader", null),
        (nameof(SettingsService.SteamlessEnabled), "Steamless", null),
        (nameof(SettingsService.SteamAutoCrackEnabled), "SteamAutoCrack", null),
        (nameof(SettingsService.CloudRedirectEnabled), "CloudRedirect", "CloudRedirectAddon"),
    ];

    [Fact]
    public void AbsentAndUnrelatedLegacySettingsGrantNoExecutableConsent()
    {
        foreach (var settings in new[]
        {
            new SettingsService(new AppSettings()),
            new SettingsService(new AppSettings { FastFetch = true, SelectedMode = "Bst", DonateKeys = true }),
        })
        {
            foreach (var workflow in Workflows)
            {
                Assert.False(settings.IsExecutableWorkflowEnabled(workflow.Component));
                if (workflow.Alias is not null) Assert.False(settings.IsExecutableWorkflowEnabled(workflow.Alias));
            }
            Assert.False(settings.IsExecutableWorkflowEnabled("Unknown"));
            Assert.False(settings.IsExecutableWorkflowEnabled("steamless"));
            Assert.False(settings.IsExecutableWorkflowEnabled(""));
            Assert.False(settings.IsExecutableWorkflowEnabled(null!));
        }
    }

    [Fact]
    public void EachConsentPersistsIndependentlyAndCanBeDisabledAfterReload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "LuaTools-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(null, directory);
            Assert.False(Directory.Exists(directory)); // Reading defaults has no persistence side effect.
            foreach (var workflow in Workflows)
            {
                PropertyInfo property = typeof(SettingsService).GetProperty(workflow.Property)!;
                property.SetValue(settings, true);
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")));
                Assert.True(json.RootElement.GetProperty(workflow.Property).GetBoolean());

                var reloaded = new SettingsService(null, directory);
                foreach (var other in Workflows)
                    Assert.Equal(other.Component == workflow.Component, reloaded.IsExecutableWorkflowEnabled(other.Component));
                if (workflow.Alias is not null) Assert.True(reloaded.IsExecutableWorkflowEnabled(workflow.Alias));

                property.SetValue(reloaded, false);
                settings = new SettingsService(null, directory);
                Assert.False(settings.IsExecutableWorkflowEnabled(workflow.Component));
                if (workflow.Alias is not null) Assert.False(settings.IsExecutableWorkflowEnabled(workflow.Alias));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FailedPersistenceDoesNotGrantConsentOrEmitAnEnableEvent()
    {
        string path = Path.Combine(Path.GetTempPath(), "LuaTools-settings-tests-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "This file cannot be a settings directory.");
        try
        {
            var settings = new SettingsService(new AppSettings(), path);
            bool changed = false;
            settings.ExecutableWorkflowChanged += (_, _) => changed = true;
            Assert.ThrowsAny<IOException>(() => settings.SteamPluginEnabled = true);
            Assert.False(settings.SteamPluginEnabled);
            Assert.False(changed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChangeEventsUseCanonicalComponentsAndOnlyFireForChanges()
    {
        var settings = new SettingsService(new AppSettings());
        var changes = new List<(string Component, bool Enabled)>();
        settings.ExecutableWorkflowChanged += (component, enabled) => changes.Add((component, enabled));
        foreach (var workflow in Workflows)
        {
            var property = typeof(SettingsService).GetProperty(workflow.Property)!;
            property.SetValue(settings, false);
            property.SetValue(settings, true);
            property.SetValue(settings, true);
            property.SetValue(settings, false);
        }
        Assert.Equal(Workflows.SelectMany(workflow => new[]
        {
            (workflow.Component, true), (workflow.Component, false),
        }), changes);
        Assert.False(settings.DonateKeys);
        Assert.False(settings.StartWithWindows);
    }
}
