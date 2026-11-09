using Avalonia.Controls;
using Toboo.Desktop.ViewModels;

namespace Toboo.Desktop;

public sealed partial class SensorWindow : Window
{
    public SensorViewModel ViewModel { get; }
    public SensorWindow() : this(new SensorViewModel()) { }
    public SensorWindow(SensorViewModel model)
    {
        InitializeComponent(); ViewModel = model; DataContext = model;
    }
    protected override void OnClosed(EventArgs args)
    {
        ViewModel.Dispose(); base.OnClosed(args);
    }
}
