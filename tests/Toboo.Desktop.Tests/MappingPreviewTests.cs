using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Toboo.Vision;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class MappingPreviewTests
{
    [AvaloniaFact]
    public async Task DemoShowsTrajectoryAndNearObstacleWithoutDevices()
    {
        var window = new MappingWindow();
        try
        {
            window.Show();
            Assert.False(window.FindControl<Button>("StartButton")!.IsEnabled);
            window.FindControl<Button>("DemoButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("合成演示", window.FindControl<TextBlock>("DeviceStatus")!.Text);
            Assert.Contains("停止建议", window.FindControl<TextBlock>("Advice")!.Text);
            Assert.Contains("27 次回波入图 · 27 个轨迹点", window.FindControl<TextBlock>("MapStatus")!.Text);
            Assert.Null(window.FindControl<Image>("CameraPreview")!.Source);
            await Task.Delay(60);
            using var screenshot = window.CaptureRenderedFrame();
            Assert.NotNull(screenshot);

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Toboo.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            var artifacts = Path.Combine(directory.FullName, "artifacts", "aruco-sonar-map");
            Directory.CreateDirectory(artifacts);
            screenshot.Save(Path.Combine(artifacts, "preview.png"));
            MarkerMappingVision.WritePatterns(artifacts);
        }
        finally { window.Close(); }
    }
}
