using Avalonia.Controls;

namespace PanTiltTracker.Desktop.Views;

public sealed partial class DevicesView : UserControl
{
    public DevicesView() => InitializeComponent();
    public bool Compact { set => Classes.Set("compact", value); }
}
