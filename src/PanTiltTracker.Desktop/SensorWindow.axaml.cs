using Avalonia.Controls;
using PanTiltTracker.Desktop.ViewModels;

namespace PanTiltTracker.Desktop;

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
