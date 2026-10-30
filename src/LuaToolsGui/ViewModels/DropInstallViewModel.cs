using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>One row in the overwrite-confirm diff (a depot/DLC the dropped lua adds or removes).</summary>
public record DropDiffRow(string Title, string Meta, bool IsDlc, bool IsShared, string SteamDbUrl);

/// <summary>
/// Reusable drag-and-drop installer used on Home and Add. Accepts dropped .lua / .manifest / .zip
/// files and installs them into Steam via <see cref="LuaInstaller"/>. Shows a before/after diff
/// confirm when a dropped lua would overwrite an existing one, then a result banner. Raises
/// <see cref="Installed"/> so the host page can refresh.
/// </summary>
public partial class DropInstallViewModel : ObservableObject
{
    private readonly LocalInstallCoordinator _batches;
    private readonly LuaInstaller _installer;
    private readonly SteamAppListCache _appList;
    private readonly SteamAppInfoCache _appInfo;
    private readonly SteamDepotInfo _depotInfo;

    /// <summary>Raised after a successful (non-cancelled) install so the page can refresh its library.</summary>
    public event Action? Installed;

    public DropInstallViewModel(LuaInstaller installer, SteamAppListCache appList,
        SteamAppInfoCache appInfo, SteamDepotInfo depotInfo, LocalInstallCoordinator? batches = null)
    {
        _installer = installer;
        _appList = appList;
        _appInfo = appInfo;
        _depotInfo = depotInfo;
        _batches = batches ?? new(installer, new(installer, appList, appInfo, depotInfo));
    }

    // Per-confirm steamcmd lookup: depot/DLC id → its real depot info (name/size/os/lang).
    private IReadOnlyDictionary<long, ContentDepot> _depotsById = new Dictionary<long, ContentDepot>();

    // ── Drag-over highlight ─────────────────────────────────────────
    [ObservableProperty] private bool _isDragOver;

    // ── Result banner ───────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    [ObservableProperty] private bool _resultFailed;
    public bool HasResult => ResultText is not null;

    // ── Overwrite confirm overlay ───────────────────────────────────
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string? _confirmNoChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAdded))]
    private List<DropDiffRow> _added = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoved))]
    private List<DropDiffRow> _removed = [];

    public bool HasAdded => Added.Count > 0;
    public bool HasRemoved => Removed.Count > 0;

    private TaskCompletionSource<bool>? _decision;

    /// <summary>
    /// Install a game by appid. Set by App to the SAME path <c>luatools://install/&lt;appid&gt;</c> takes,
    /// so a dropped link and the protocol trigger can't drift apart.
    /// </summary>
    public Func<long, Task>? InstallByAppId { get; set; }

    /// <summary>
    /// Entry point for a dropped LINK (a SteamDB / Steam store URL): pull the appid out and install it.
    /// Parsing lives here rather than in the view's code-behind so it's reachable from a test.
    /// </summary>
    /// <returns>True when the text named a game and the install was started.</returns>
    public async Task<bool> TryHandleLinkAsync(string? text)
    {
        if (SteamLinkParser.AppIdFrom(text) is not { } appId || InstallByAppId is null) return false;

        await InstallByAppId(appId);
        return true;
    }

    /// <summary>Entry point: hand the dropped file paths to install (called by the view's Drop handler).</summary>
    public async Task HandleDropAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(LuaInstaller.IsInstallable).ToArray();
        if (files.Length == 0) { ResultFailed = true; ResultText = Resources.Strings.Drop_Nothing; return; }
        var results = await _batches.InstallAsync(files, async (path, diff, ct) =>
        {
            var tcs = _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Added = diff.Added.Select(r => new DropDiffRow(r.Title, r.Meta, r.IsDlc, r.IsShared, r.SteamDbUrl)).ToList();
            Removed = diff.Removed.Select(r => new DropDiffRow(r.Title, r.Meta, r.IsDlc, r.IsShared, r.SteamDbUrl)).ToList();
            ConfirmTitle = string.Format(Resources.Strings.Drop_Confirm_Replace, NameFor(diff.AppId));
            ConfirmNoChanges = !diff.Readable ? Resources.Strings.Agent_Add_UnreadableDiff : Added.Count + Removed.Count == 0 ? Resources.Strings.Drop_Confirm_NoChanges : null;
            IsConfirming = true;
            try { return await tcs.Task.WaitAsync(ct); }
            finally { IsConfirming = false; _decision = null; }
        });
        ResultFailed = results.Any(r => !r.Ok);
        int luas = results.Count(r => r.LuaInstalled), manifests = results.Sum(r => r.ManifestCount);
        var parts = new List<string>();
        if (luas > 0) parts.Add(string.Format(Resources.Strings.Drop_Count_Luas, luas));
        if (manifests > 0) parts.Add(string.Format(Resources.Strings.Drop_Count_Manifests, manifests));
        int failed = results.Sum(r => r.FailedCount);
        string? error = results.FirstOrDefault(r => r.Error is not null)?.Error;
        if (parts.Count == 0 && !ResultFailed) ResultText = results.Any(r => r.Skipped) ? Resources.Strings.Drop_NothingInstalled : null;
        else
        {
            string ok = parts.Count > 0 ? string.Format(Resources.Strings.Drop_Result_Installed, string.Join(" + ", parts)) : "";
            string bad = failed > 0 ? string.Format(Resources.Strings.Drop_Result_Failed, failed) : "";
            ResultText = $"{ok}{bad}{(error is null ? ResultFailed && failed == 0 ? Resources.Strings.Agent_Add_InstallFailed : "" : " " + error)}".Trim();
        }
        if (luas + manifests > 0) Installed?.Invoke();
    }

    [RelayCommand]
    private void ConfirmOverwrite() => _decision?.TrySetResult(true);
    [RelayCommand]
    private void CancelOverwrite() => _decision?.TrySetResult(false);

    [RelayCommand]
    private static void OpenSteamDb(DropDiffRow row) => SteamService.OpenUrl(row.SteamDbUrl);

    private string NameFor(long appId) =>
        _appList.GetName(appId) ?? _appInfo.GetCached(appId)?.Name ?? appId.ToString();

}
