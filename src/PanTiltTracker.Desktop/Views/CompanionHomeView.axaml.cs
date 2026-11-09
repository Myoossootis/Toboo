using Avalonia.Controls;

namespace PanTiltTracker.Desktop.Views;

public sealed partial class CompanionHomeView : UserControl
{
    public CompanionHomeView() => InitializeComponent();
    public bool Compact { set { Classes.Set("compact", value); HomeGrid.ColumnDefinitions[1].Width = new(value ? 264 : 320); } }
}
