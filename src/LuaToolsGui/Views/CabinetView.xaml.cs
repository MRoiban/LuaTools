using System.Windows;
using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class CabinetView : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(CabinetView), new PropertyMetadata(false));
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public CabinetView(CabinetViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        clock.Tick += (_, _) => { foreach (var game in viewModel.Games) game.RefreshLastSync(); };
        Loaded += (_, _) => { clock.Start(); viewModel.RefreshCloudSaves(); }; // CloudRedirect may have changed on the Mode page
        Unloaded += (_, _) => clock.Stop();
        SizeChanged += (_, _) => IsCompact = ActualWidth < 640;
    }
}
