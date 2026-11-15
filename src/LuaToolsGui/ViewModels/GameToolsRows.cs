using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LuaToolsGui.Services.GameTools;

namespace LuaToolsGui.ViewModels;

/// <summary>One achievement on the Tools page. Toggling only stages the change; Save writes it.</summary>
public partial class AchievementRowViewModel : ObservableObject
{
    private bool _original;

    public AchievementInfo Info { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IconPath), nameof(StatusText), nameof(ShowProgress))]
    private bool _unlocked;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IconPath))] private string? _colorIcon;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IconPath))] private string? _grayIcon;

    public AchievementRowViewModel(AchievementInfo info)
    {
        Info = info;
        _original = _unlocked = info.Unlocked;
    }

    public bool IsDirty => Unlocked != _original;
    public bool CanToggle => !Info.Protected;
    public string? IconPath => Unlocked ? ColorIcon ?? GrayIcon : GrayIcon ?? ColorIcon;

    /// <summary>"Unlocked 12/03/2026 18:04", "Unlocked" (staged or no time recorded) or "Locked".</summary>
    public string StatusText =>
        !Unlocked ? Resources.Strings.Tools_Locked
        : !IsDirty && Info.UnlockedAt is { } at
            ? string.Format(Resources.Strings.Tools_UnlockedAt, at.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))
            : Resources.Strings.Tools_Unlocked;

    public bool ShowProgress => !Unlocked && Info.Progress is not null;
    public double ProgressMin => Info.Progress?.Min ?? 0;
    public double ProgressMax => Info.Progress?.Max ?? 1;
    public double ProgressValue => Math.Clamp(Info.Progress?.Value ?? 0, ProgressMin, ProgressMax);
    public string? ProgressText => Info.Progress is { } p
        ? ProgressValue.ToString("0.##", CultureInfo.CurrentCulture) + " / " + p.Max.ToString("0.##", CultureInfo.CurrentCulture) : null;

    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();
        return Info.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || Info.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || Info.ApiName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The staged state is now what Steam has (after a live save).</summary>
    public void Commit()
    {
        _original = Unlocked;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>One editable stat. Ints must be whole numbers; floats accept the user's or the invariant format.</summary>
public partial class StatRowViewModel : ObservableObject
{
    private double _original;

    public StatInfo Info { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IsValid))]
    private string _text;

    public StatRowViewModel(StatInfo info)
    {
        Info = info;
        _original = info.Value;
        _text = Format(info.Value);
    }

    public bool IsReadOnly => Info.Protected;
    public bool IsValid => Parsed.HasValue;
    public bool IsDirty => Parsed is { } v && (Info.IsFloat ? (float)v != (float)_original : v != _original);

    public double? Parsed
    {
        get
        {
            if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
                && !double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
            if (!double.IsFinite(v)) return null;
            if (Info.IsFloat) return Math.Abs(v) <= float.MaxValue ? v : null;
            return v == Math.Floor(v) && v >= int.MinValue && v <= int.MaxValue ? v : null;
        }
    }

    public void Commit()
    {
        _original = Parsed ?? _original;
        OnPropertyChanged(nameof(IsDirty));
    }

    private string Format(double v) =>
        Info.IsFloat ? ((float)v).ToString(CultureInfo.CurrentCulture) : ((long)v).ToString(CultureInfo.CurrentCulture);
}
