using Avalonia;
using Avalonia.Media;

namespace Toboo.Desktop.Controls;

/// <summary>Small native vector icons, sharing a 24-unit grid and stroke.</summary>
public sealed class AppIcon : Avalonia.Controls.Shapes.Path
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<AppIcon, string>(nameof(Kind), "Robot");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public AppIcon()
    {
        Width = Height = 24; Stretch = Stretch.Uniform;
        StrokeThickness = 1.7; Stroke = Brush.Parse("#126F65");
        UpdateIcon();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty) UpdateIcon();
    }
    private void UpdateIcon() => Data = Geometry.Parse(Kind switch {
        "Chat" => "M5,3 H19 Q22,3 22,6 V15 Q22,18 19,18 H10 L5,22 V18 Q2,18 2,15 V6 Q2,3 5,3 Z",
        "Settings" => "M9,2 H15 L16,6 L20,7 L22,12 L20,17 L16,18 L15,22 H9 L8,18 L4,17 L2,12 L4,7 L8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
        "Camera" => "M3,6 H7 L9,3 H15 L17,6 H21 Q23,6 23,8 V19 Q23,21 21,21 H3 Q1,21 1,19 V8 Q1,6 3,6 Z M17,13 A5,5 0 1 1 7,13 A5,5 0 1 1 17,13",
        "Mic" => "M8,5 A4,4 0 0 1 16,5 V12 A4,4 0 0 1 8,12 Z M4,10 V12 A8,8 0 0 0 20,12 V10 M12,20 V24 M7,24 H17",
        "Volume" => "M2,9 H6 L12,4 V22 L6,17 H2 Z M16,8 Q21,13 16,18 M19,4 Q27,13 19,22",
        "SendUp" => "M12,21 V4 M5,11 L12,4 L19,11",
        "Cancel" => "M6,6 L18,18 M18,6 L6,18",
        "StopCapture" => "M7,5 H17 Q19,5 19,7 V17 Q19,19 17,19 H7 Q5,19 5,17 V7 Q5,5 7,5 Z",
        _ => "M12,2 V6 M10,2 A2,2 0 1 1 14,2 A2,2 0 1 1 10,2 M6,7 H18 Q23,7 23,13 V17 Q23,22 18,22 H6 Q1,22 1,17 V13 Q1,7 6,7 Z M7,12 V16 M17,12 V16"
    });
}
