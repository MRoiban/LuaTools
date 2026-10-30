using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly SteamService _steam;
    private readonly SteamCoordinator _operations;

    /// <summary>The first-run welcome overlay VM (hosted at the window root, shown via its IsOpen).</summary>
    public OnboardingViewModel Onboarding { get; }

    /// <summary>App version shown in the nav pane footer, e.g. "v1.0.1". Read from the assembly.</summary>
    public string VersionLabel { get; } = $"v{ReadVersion()}";

    private static string ReadVersion()
    {
        // InformationalVersion carries the csproj <Version> (may have a "+commit" suffix. Trim it).
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var ver = info ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        int plus = ver.IndexOf('+');
        return plus >= 0 ? ver[..plus] : ver;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRealUser))]
    [NotifyPropertyChangedFor(nameof(FooterStatus))]
    private bool _isGuest = true;

    public bool IsRealUser => !IsGuest;

    /// <summary>Bottom-of-pane line: version plus auth state. No username shown (privacy).</summary>
    public string FooterStatus => $"{VersionLabel} · {(IsGuest ? Resources.Strings.Nav_Footer_Guest : Resources.Strings.Nav_Footer_LoggedIn)}";

    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string? _signInError;

    public MainViewModel(AuthService auth, SteamService steam, OnboardingViewModel onboarding, SteamCoordinator? operations = null)
    {
        _auth = auth;
        _steam = steam;
        _operations = operations ?? new(steam);
        Onboarding = onboarding;
        _auth.AuthStateChanged += () => IsGuest = _auth.IsGuest;
    }

    private Task? _initialize;

    /// <summary>Restore the saved session once. Runs at startup even for a --minimized (tray) launch,
    /// where the window's Loaded never fires and the Steam plugin would otherwise act as a guest; the
    /// window's own call later just awaits the same task.</summary>
    public Task InitializeAsync() => _initialize ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        await _auth.InitializeAsync();
        IsGuest = _auth.IsGuest;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSigningIn) return;
        IsSigningIn = true;
        SignInError = null;
        try
        {
            await _auth.SignInAsync();
        }
        catch (Exception ex)
        {
            SignInError = ex.Message;
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    /// <summary>Confirm, then kill + relaunch Steam so newly added/removed luas take effect.</summary>
    [RelayCommand]
    private void RestartSteam()
    {
        try
        {
        var result = MessageBox.Show(
            Resources.Strings.Main_RestartSteam_Ask,
            Resources.Strings.Manage_RestartSteam_Title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        if (!_operations.Restart())
            MessageBox.Show(
                Resources.Strings.Manage_RestartSteam_Failed,
                Resources.Strings.Manage_RestartSteam_Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            }
        catch (Exception ex) { AppEventLog.Shared.Write(Microsoft.Extensions.Logging.LogLevel.Warning, "Steam", Resources.Strings.Manage_RestartSteam_Failed, ex); MessageBox.Show(Resources.Strings.Manage_RestartSteam_Failed, Resources.Strings.Manage_RestartSteam_Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
