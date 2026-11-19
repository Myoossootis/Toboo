using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Toboo.Vision;
using Toboo.Desktop.Controls;
using Toboo.Desktop.Views;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class MappingPreviewTests
{
    [AvaloniaFact]
    public async Task DemoShowsTrajectoryAndNearObstacleWithoutDevices()
    {
        using var view = new SpatialCalibrationView();
        var window = new Window { Content = view, Width = 1240, Height = 880 };
        try
        {
            window.Show();
            Assert.False(view.FindControl<Button>("StartButton")!.IsEnabled);
            view.FindControl<Button>("DemoButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("合成演示", view.FindControl<TextBlock>("DeviceStatus")!.Text);
            Assert.Contains("停止建议", view.FindControl<TextBlock>("Advice")!.Text);
            Assert.Contains("27 次回波入图 · 27 个轨迹点", view.FindControl<TextBlock>("MapStatus")!.Text);
            Assert.NotNull(view.FindControl<Image>("CameraPreview")!.Source);
            Assert.NotNull(view.FindControl<Image>("DepthPreview")!.Source);
            Assert.True(view.FindControl<PointCloudView>("Cloud")!.PointCount > 8000);
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
            if (File.Exists(Environment.GetEnvironmentVariable("TOBOO_DEPTH_TEST_IMAGE")) &&
                File.Exists(RelativeDepthPreview.DefaultModelPath))
                await VerifyLocalPhotoAsync(view, window);
        }
        finally { await view.StopAsync(); window.Close(); }
    }

    private static async Task VerifyLocalPhotoAsync(SpatialCalibrationView view, Window window)
    {
        await view.LoadPhotoAsync(Environment.GetEnvironmentVariable("TOBOO_DEPTH_TEST_IMAGE")!);
        Assert.Contains("照片估计", view.FindControl<TextBlock>("DepthStatus")!.Text);
        Assert.True(view.FindControl<PointCloudView>("Cloud")!.PointCount > 8000);
        Assert.Equal("实测距离 —", view.FindControl<TextBlock>("RangeStatus")!.Text);
        Assert.NotNull(view.FindControl<Image>("DepthPreview")!.Source);
        Assert.True(view.FindControl<Button>("PhotoButton")!.IsEnabled);
        await Task.Delay(80);
        using var screenshot = window.CaptureRenderedFrame();
        Assert.NotNull(screenshot);
        var destination = Environment.GetEnvironmentVariable("TOBOO_DEPTH_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(destination)) screenshot.Save(destination);
    }

    [AvaloniaFact]
    public async Task InvalidPhotoReportsFailureAndAllowsRetry()
    {
        using var view = new SpatialCalibrationView();
        var window = new Window { Content = view, Width = 1240, Height = 880 };
        try
        {
            window.Show();
            await view.LoadPhotoAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg"));
            Assert.Contains("照片分析失败", view.FindControl<TextBlock>("DepthStatus")!.Text);
            Assert.True(view.FindControl<Button>("PhotoButton")!.IsEnabled);
            Assert.False(view.FindControl<Button>("StopButton")!.IsEnabled);
        }
        finally { await view.StopAsync(); window.Close(); }
    }
}
