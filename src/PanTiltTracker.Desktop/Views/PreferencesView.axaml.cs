using Avalonia.Controls;

namespace PanTiltTracker.Desktop.Views;

public sealed partial class PreferencesView : UserControl
{
    public PreferencesView() => InitializeComponent();
    public bool Compact { set { Classes.Set("compact", value); PreferencesGrid.ColumnDefinitions[1].Width = new(value ? 0 : 280); ContextRail.IsVisible = !value; } }
}
