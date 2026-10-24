using LuaToolsGui.Services.Cabinet;
using System.Windows;
using System.Windows.Threading;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using LuaToolsGui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LuaToolsGui;

public partial class App : Application
{
    private readonly IHost _host;
    private readonly SemaphoreSlim _pluginRuntimeGate = new(1, 1);

    private async Task SetPluginRuntimeAsync(bool activate)
    {
        await _pluginRuntimeGate.WaitAsync();
        try
        {
            var settings = _host.Services.GetRequiredService<SettingsService>();
            var server = _host.Services.GetRequiredService<HttpServerService>();
            var injector = _host.Services.GetRequiredService<CefInjectorService>();
            if (activate && settings.IsExecutableWorkflowEnabled("LuaToolsPlugin") && BundledPlugin.IsActivated)
            {
                await server.StartAsync(CancellationToken.None);
                try { await injector.StartAsync(CancellationToken.None); }
                catch { await server.StopAsync(CancellationToken.None); throw; }
            }
            else
            {
                await server.StopAsync(CancellationToken.None);
            }
        }
        finally { _pluginRuntimeGate.Release(); }
    }

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SettingsService>();
                services.AddSingleton<ICabinetSettings>(sp => sp.GetRequiredService<SettingsService>());
                services.AddSingleton<CabinetService>();
                services.AddSingleton<ICabinetCloud>(sp => sp.GetRequiredService<CabinetService>());
                services.AddSingleton<ICabinetService>(sp => sp.GetRequiredService<CabinetService>());
                services.AddHostedService(sp => sp.GetRequiredService<CabinetService>());
                services.AddSingleton<UpdateService>();
                services.AddSingleton<CacheService>();
                services.AddSingleton<SteamService>();
                services.AddSingleton<SteamAppListCache>();
                services.AddSingleton<SteamAppInfoCache>();
                services.AddSingleton<CoverCache>();
                services.AddSingleton<ToastService>();
                services.AddSingleton<SteamDepotInfo>();
                services.AddSingleton<LuaVault>();
                services.AddSingleton<Services.AppInfo.LaunchModStore>();
                services.AddSingleton<Services.AppInfo.LaunchOptionsService>();
                services.AddSingleton<LuaInstaller>();
                services.AddSingleton<SteamLibraryService>();
                services.AddSingleton<GithubProxy>();
                services.AddSingleton<HardwareAppIdService>();
                services.AddSingleton<SteamlessService>();
                services.AddSingleton<SteamAutoCrackService>();
                services.AddSingleton<CloudRedirectService>();
                services.AddSingleton<DepotDownloaderService>();
                services.AddSingleton<DepotCacheMigrationService>();
                services.AddSingleton<AppliedFixIndexService>();
                services.AddSingleton<UnlockerService>();
                services.AddSingleton<PluginInstallerService>();
                services.AddTransient<DropInstallViewModel>(); // one per page (Home, Add)
                services.AddSingleton<AuthService>();
                services.AddSingleton<LuaToolsApiClient>();
                services.AddSingleton<Services.OnlineFixMe.OnlineFixMeClient>();
                services.AddSingleton<HubcapService>();
                // Central download queue. Singleton + hosted service (same pattern as HttpServerService
                // below): the hosted lifetime runs the scheduler pump, and view models resolve the same
                // instance to enqueue and observe.
                services.AddSingleton<Services.Downloads.DownloadQueue>();
                services.AddHostedService(sp => sp.GetRequiredService<Services.Downloads.DownloadQueue>());
                services.AddSingleton<Services.Downloads.ManifestJobFactory>();
                // Hook loader infrastructure
                services.AddSingleton<PluginAddService>();
                services.AddSingleton<DlcBatchService>();
                services.AddSingleton<HttpServerService>();
                // Also resolvable as a plain singleton (not just IHostedService) so PluginInstallerService
                // can call ReloadPluginFilesAsync() after install/uninstall. Same pattern as HttpServerService.
                services.AddSingleton<CefInjectorService>();
                services.AddHostedService(sp => sp.GetRequiredService<CefInjectorService>());
                services.AddSingleton<DownloadViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ManageViewModel>();
                services.AddSingleton<CabinetViewModel>();
                services.AddSingleton<BuildsViewModel>();
                services.AddTransient<LaunchOptionsViewModel>(); // one per dialog
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<ModeViewModel>();
                services.AddSingleton<FixesViewModel>();
                services.AddSingleton<DownloadsViewModel>();
                services.AddSingleton<PluginViewModel>();
                services.AddSingleton<OnboardingViewModel>();
                services.AddSingleton<MainViewModel>();
                // Pages resolved by NavigationView via the DI service provider.
                services.AddSingleton<HomeView>();
                services.AddSingleton<DownloadView>();
                services.AddSingleton<DownloadsView>();
                services.AddSingleton<ManageView>();
                services.AddSingleton<CabinetView>();
                services.AddSingleton<BuildsView>();
                services.AddSingleton<ModeView>();
                services.AddSingleton<FixesView>();
                services.AddSingleton<PluginView>();
                services.AddSingleton<SettingsView>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    /// <summary>
    /// Warn when Steam has overwritten launch options we'd applied, and offer to put them back.
    ///
    /// <para>
    /// Steam rebuilds appinfo.vdf from PICS on login, app updates and store browsing. It did so twice
    /// while this feature was being written, so an applied edit is not permanent. Re-applying is offered
    /// but never automatic: it closes Steam, which is not something to do behind the user's back at
    /// startup. Costs nothing when no launch options have been edited (the store short-circuits on empty).
    /// </para>
    /// </summary>
    private async Task CheckLaunchOptionDriftAsync()
    {
        try
        {
            var launch = _host.Services.GetRequiredService<Services.AppInfo.LaunchOptionsService>();
            if (launch.Store.IsEmpty) return;

            // Indexing the ~373 MB cache takes a couple of seconds, never on the UI thread.
            var drifted = await Task.Run(launch.FindDrifted);
            if (drifted.Count == 0) return;

            var toast = _host.Services.GetRequiredService<ToastService>();
            Dispatcher.Invoke(() => toast.ShowAction(
                LuaToolsGui.Resources.Strings.Launch_Drift_Title,
                string.Format(LuaToolsGui.Resources.Strings.Launch_Drift_Body, drifted.Count),
                LuaToolsGui.Resources.Strings.Launch_Drift_Action,
                () => _ = ReapplyDriftedAsync(launch, drifted, toast)));
        }
        catch
        {
            // Cache locked/unreadable: nothing actionable, and this must never block startup.
        }
    }

    /// <summary>
    /// The drift notice's "Re-apply" button: confirm, then write the staged edits back into appinfo.
    ///
    /// <para>
    /// The write runs OFF the UI thread. Unlike the launch-options dialog (which is modal, so its own
    /// synchronous apply merely blocks a window that's already blocking), this fires with the main window
    /// live, and <c>Apply</c> indexes a ~373 MB file, copies a backup and rewrites it. On the UI thread
    /// that's a multi-second freeze of the whole app.
    /// </para>
    /// </summary>
    private static async Task ReapplyDriftedAsync(
        Services.AppInfo.LaunchOptionsService launch, IReadOnlyList<int> drifted, ToastService toast)
    {
        // Same wording as the dialog's own prompt: closing Steam should never read as a different
        // decision depending on where it was triggered from.
        if (MessageBox.Show(
                LuaToolsGui.Resources.Strings.Launch_ApplyNow_Body,
                LuaToolsGui.Resources.Strings.Launch_ApplyNow_Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var result = await Task.Run(() => launch.Reapply(drifted));

        if (result.Ok)
            toast.Show(LuaToolsGui.Resources.Strings.Launch_Title,
                result.SteamWasRunning
                    ? LuaToolsGui.Resources.Strings.Launch_Applied_Restarted
                    : LuaToolsGui.Resources.Strings.Launch_Applied);
        else
            toast.Show(LuaToolsGui.Resources.Strings.Launch_Title,
                string.Format(LuaToolsGui.Resources.Strings.Launch_ApplyFailed, result.Error), error: true);
    }

    // Retained for legacy callers; no update flow is registered in this fork.
    internal static Func<Task>? RunUpdateFlow => null;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Point the shared HTTP handler at the DNS setting before anything makes a request. It calls
        // this per connection rather than reading it now, so the order is not load-bearing — but doing
        // it first means the very first call of the session already honours the user's choice.
        var dnsSettings = _host.Services.GetRequiredService<SettingsService>();
        AppHttp.ModeProvider = () => dnsSettings.DnsMode;
        // Only an explicit successful plugin installation activates its local runtime.
        _host.Services.GetRequiredService<PluginInstallerService>().ActivatePlugin =
            () => SetPluginRuntimeAsync(activate: true);
        dnsSettings.ExecutableWorkflowChanged += async (component, enabled) =>
        {
            if (component != "LuaToolsPlugin") return;
            try { await SetPluginRuntimeAsync(activate: enabled && BundledPlugin.IsActivated); }
            catch { /* Each request and injection also rechecks the disabled setting. */ }
        };

        await _host.StartAsync();

        var main = _host.Services.GetRequiredService<MainViewModel>();
        var settingsVm = _host.Services.GetRequiredService<SettingsViewModel>();

        // Changing the language needs a relaunch (x:Static resources resolve at parse time).
        settingsVm.RequestRestart = RelaunchApp;

        var window = _host.Services.GetRequiredService<MainWindow>();

        // Turning off "Minimize to tray" while hidden in the tray → bring the window back.
        settingsVm.RequestShowWindow = () => Dispatcher.Invoke(window.RestoreFromTray);

        // Relaunching the app (single-instance) signals this event → surface the existing window and
        // check for any protocol URL a second instance wrote. AutoReset + executeOnlyOnce:false so it
        // keeps firing for every relaunch.
        if (Program.ShowWindowSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.ShowWindowSignal,
                (_, _) => Dispatcher.Invoke(() =>
                {
                    window.RestoreFromTray();
                }),
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        // Settings' own "Sign in with Discord" button → browser OAuth (unchanged).
        settingsVm.RequestSignIn = () => main.SignInCommand.ExecuteAsync(null);

        // Guests hitting a protected action on other pages → navigate to Settings with context banner.
        Func<Task> navigateToSignIn = () =>
        {
            Dispatcher.Invoke(() =>
            {
                settingsVm.LoginRequiredMessage = LuaToolsGui.Resources.Strings.Settings_LoginRequired;
                window.NavigateToSettings();
            });
            return Task.CompletedTask;
        };
        _host.Services.GetRequiredService<DownloadViewModel>().RequestSignIn = navigateToSignIn;
        _host.Services.GetRequiredService<FixesViewModel>().RequestSignIn = navigateToSignIn;
        var toast = _host.Services.GetRequiredService<ToastService>();
        toast.Attach(window.RootSnackbar); // wire the presenter before anything can raise a toast

        var updates = _host.Services.GetRequiredService<UpdateService>();
        bool updateToastShown = false;
        updates.UpdateReady += () => Dispatcher.Invoke(() =>
        {
            // The in-window toast holds the (consented) restart action. One is enough: re-raises only
            // nudge from the tray, where a --minimized/tray instance would otherwise never surface it.
            if (!updateToastShown)
            {
                updateToastShown = true;
                toast.ShowAction(
                    "LuaTools", LuaToolsGui.Resources.Strings.Plugin_Badge_UpdateAvailable, LuaToolsGui.Resources.Strings.Lang_Changed_Restart,
                    () => { try { updates.ApplyAndRestart(); } catch { /* Keep the running app usable. */ } });
            }
            window.ShowUpdateNotification();
            settingsVm.ShowUpdateResult(UpdateCheckResult.Staged, updates.StagedVersion);
        });
        // Cabinet saves sync only through CloudRedirect, which uses each game's own Steam Cloud paths.
        var unlocker = _host.Services.GetRequiredService<UnlockerService>();
        var cabinetService = _host.Services.GetRequiredService<CabinetService>();
        var cabinetVm = _host.Services.GetRequiredService<CabinetViewModel>();
        var modeVm = _host.Services.GetRequiredService<ModeViewModel>();
        CabinetCloudSaves CloudSavesNow()
        {
            if (!cabinetService.Status.Cloud.Connected) return CabinetCloudSaves.Hidden;
            if (unlocker.SelectedMode is not (UnlockerMode.Ost or UnlockerMode.Bst)) return CabinetCloudSaves.Unavailable;
            return unlocker.IsCloudRedirectAddonEnabled && new CloudRedirectCabinetConfig().Status().Configured
                ? CabinetCloudSaves.Active : CabinetCloudSaves.Off;
        }
        async Task OfferCloudSavesAsync()
        {
            var state = CloudSavesNow();
            if (state == CabinetCloudSaves.Active)
            {
                await cabinetService.ConfigureCloudRedirectAsync(); // the server or token may have changed
                return;
            }
            if (state == CabinetCloudSaves.Unavailable)
            {
                toast.Show("Cabinet", LuaToolsGui.Resources.Strings.Cabinet_Saves_Unavailable);
                return;
            }
            if (state != CabinetCloudSaves.Off) return;
            // The user's explicit Yes here is the consent the Settings switch and Mode page Enable record.
            if (MessageBox.Show(window, LuaToolsGui.Resources.Strings.Cabinet_Saves_OfferBody,
                    LuaToolsGui.Resources.Strings.Cabinet_Saves_OfferTitle, MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            settingsVm.CloudRedirectEnabled = true;
            await modeVm.EnableCloudRedirectForCabinetAsync();
        }
        cabinetVm.CloudSavesProvider = CloudSavesNow;
        cabinetVm.RequestEnableCloudSaves = OfferCloudSavesAsync;
        settingsVm.RequestOfferCloudSaves = async () => { await OfferCloudSavesAsync(); cabinetVm.RefreshCloudSaves(); };
        cabinetVm.RefreshCloudSaves();

        settingsVm.RequestUpdateCheck = async () => (await Task.Run(() => updates.CheckAndStageAsync()), updates.StagedVersion);
        settingsVm.RequestApplyUpdate = () => { try { updates.ApplyAndRestart(); } catch { /* Keep the running app usable. */ } };
        _ = updates.CheckAndStageAsync();
        // The app usually lives in the tray for days (autostart --minimized), so a launch-time check alone
        // misses every release published after boot. Checks continue after staging so a newer release
        // replaces the staged one instead of installing an already-outdated build.
        var updateTimer = new DispatcherTimer { Interval = UpdateService.CheckInterval };
        updateTimer.Tick += (_, _) => _ = updates.CheckAndStageAsync(announceStaged: false);
        updateTimer.Start();
        window.RestoredFromTray += () => _ = updates.CheckIfDueAsync(TimeSpan.FromMinutes(15));

        // Language changed → persistent toast offering an immediate relaunch.
        settingsVm.RequestRestartPrompt = () => Dispatcher.Invoke(() =>
            toast.ShowAction(
                LuaToolsGui.Resources.Strings.Lang_Changed_Title,
                LuaToolsGui.Resources.Strings.Lang_Changed_Body,
                LuaToolsGui.Resources.Strings.Lang_Changed_Restart,
                () => settingsVm.RequestRestart?.Invoke()));

        var download = _host.Services.GetRequiredService<DownloadViewModel>();

        var manage = _host.Services.GetRequiredService<ManageViewModel>();

        // Manage page "Update" → go to the Add page pre-seeded with that appid.
        manage.NavigateToAdd = appId =>
            Dispatcher.Invoke(() => { window.NavigateToAdd(); download.SeedSearch(appId); });

        // Manage flyout "Manage Build" → go to the Builds page with that game selected.
        var builds = _host.Services.GetRequiredService<BuildsViewModel>();
        manage.NavigateToBuilds = appId =>
            Dispatcher.Invoke(() => { window.NavigateToBuilds(); _ = builds.SelectAppAsync(appId); });

        // Manage flyout "Launch options…" → modal editor over Steam's appinfo cache.
        manage.OpenLaunchOptions = (appId, name) => Dispatcher.Invoke(() =>
        {
            var dialog = new LaunchOptionsDialog(
                _host.Services.GetRequiredService<LaunchOptionsViewModel>(), appId, name)
            { Owner = window };
            dialog.ShowDialog();
        });

        // Steam regenerates appinfo.vdf from PICS, wiping launch edits. Check once at startup and
        // OFFER to re-apply, never silently, since applying closes Steam.
        _ = CheckLaunchOptionDriftAsync();

        // Home "recently added" + Add install banner "Reveal" → go to Manage and open that game's detail.
        Action<long> openInManage = appId =>
            Dispatcher.Invoke(() => { window.NavigateToManage(); _ = manage.OpenDetailForAppIdAsync(appId); });
        var home = _host.Services.GetRequiredService<HomeViewModel>();
        home.NavigateToGame = openInManage;

        // Deliberately NO queue-wide completion toast here. Every entry point already reports its own
        // outcome: Fixes toasts from ManifestJobFactory, the Add page shows its InstallStatus banner, the
        // store plugin has its popup, and a silent install pops a tray balloon. A global toast on top of
        // those double-notified every one of them.

        // The Downloads tab's "Review" button on an item waiting for an overwrite confirmation: the
        // overlay lives on the Add page, so send the user there.
        _host.Services.GetRequiredService<DownloadsViewModel>().RevealItem = _ => window.NavigateToAdd();

        download.NavigateToGame = openInManage;
        builds.NavigateToManage = openInManage; // Builds "Manage" button: the reverse of "Manage Build"
        // Depot download queues one item covering the whole selection; show the user where it went.
        builds.RequestShowDownloads = () => Dispatcher.Invoke(window.NavigateToDownloads);

        // Dragging a SteamDB / Steam store link onto either drop box installs that appid. Routed through
        // HandleProtocolUrl rather than calling ProtocolInstall directly, so a dropped link and
        // luatools://install/<id> are literally the same path and can't drift apart later.
        // DropInstallViewModel is transient, so Home and Add each hold their own instance.
        Func<long, Task> installByAppId = appId =>
        {
            Dispatcher.Invoke(() => HandleProtocolUrl($"luatools://install/{appId}"));
            return Task.CompletedTask;
        };
        home.Drop.InstallByAppId = installByAppId;
        download.Drop.InstallByAppId = installByAppId;

        // Home dashboard cells → section navigation.
        home.NavigateToPlugin = () => Dispatcher.Invoke(window.NavigateToPlugin);
        home.NavigateToManage = () => Dispatcher.Invoke(window.NavigateToManage);
        home.NavigateToSettings = () => Dispatcher.Invoke(window.NavigateToSettings);
        home.NavigateToMode = () => Dispatcher.Invoke(window.NavigateToMode);

        // Onboarding finished applying its actions → refresh the Home dashboard tiles (mode + plugin status).
        main.Onboarding.RefreshHome = () => Dispatcher.Invoke(() => home.LoadAsync());

        // Any game added (plugin store-page button, drag-drop, Add page, Fixes) → refresh the library views
        // live. LuaInstaller.Installed can fire on a background thread (plugin install), so marshal to UI.
        var luaInstaller = _host.Services.GetRequiredService<LuaInstaller>();
        var appInfo = _host.Services.GetRequiredService<SteamAppInfoCache>();
        luaInstaller.Installed += appId => Dispatcher.InvokeAsync(async () =>
        {
            _ = manage.LoadAsync();            // re-scan so Manage updates too if it's the visible page
            _ = builds.LoadAsync();            // a newly installed lua is a new variant in the vault
            await home.RefreshLibraryAsync();  // game appears (its cover may lag for newer titles)

            // Newer titles have no guessable header URL: the classic CDN path 404s and the real header is
            // a content-hashed store_item_assets URL that only comes from appdetails. Warm that game's
            // details at interactive priority (retries past throttling), then refresh again so its cover
            // fills in instead of staying blank until an app restart.
            if (await appInfo.EnsureFullDetailsAsync(appId))
                await home.RefreshLibraryAsync();
        });

        // Open only the local catalog UI. No protocol installs, onboarding installers,
        // key donation, analytics, upstream updates or automatic Steam modifications run here.
        if (Program.StartMinimized)
        {
            window.StartSilent();
            try { await main.InitializeAsync(); }
            catch { /* auth restore failed (e.g. offline). Plugin requests stay guest-only */ }
        }
        else
            window.Show();
        await SetPluginRuntimeAsync(activate: BundledPlugin.IsActivated);

        // Refresh bounded metadata through the validated catalog service.
        _ = _host.Services.GetRequiredService<HardwareAppIdService>().EnsureFreshAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await SetPluginRuntimeAsync(activate: false);
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }

    /// <summary>Relaunch the app (used after a language change). The single-instance mutex is released
    /// only when THIS process exits, so we start the new instance via a short delayed shell command. By
    /// the time it launches the exe, our mutex is free and the new instance won't bow out.</summary>
    private void RelaunchApp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                // cmd: wait ~1.2s for this process's mutex to release, then start the exe detached.
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch { /* if relaunch fails, the user can reopen manually */ }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>Route a luatools:// protocol URL to the appropriate page and action.</summary>
    private void HandleProtocolUrl(string url)
    {
        var (action, appId, silent) = ProtocolService.Parse(url);
        if (action is null || appId is null) return;

        var window = _host.Services.GetRequiredService<MainWindow>();
        var download = _host.Services.GetRequiredService<DownloadViewModel>();
        var manage = _host.Services.GetRequiredService<ManageViewModel>();
        var fixes = _host.Services.GetRequiredService<FixesViewModel>();

        switch (action)
        {
            case "game":
                window.NavigateToAdd();
                download.SeedSearch(appId.Value);
                break;
            case "install":
                window.NavigateToAdd();
                download.SeedSearch(appId.Value);
                break;
            case "manage":
                window.NavigateToManage();
                _ = manage.OpenDetailForAppIdAsync(appId.Value);
                break;
            case "fix":
                window.NavigateToFixes();
                _ = fixes.OpenForAppIdAsync(appId.Value);
                break;
        }
    }
}
