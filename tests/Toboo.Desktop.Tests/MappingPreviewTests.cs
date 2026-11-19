using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Toboo.Vision;
using Toboo.Desktop.Controls;
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
            Assert.NotNull(window.FindControl<Image>("CameraPreview")!.Source);
            Assert.NotNull(window.FindControl<Image>("DepthPreview")!.Source);
            Assert.True(window.FindControl<PointCloudView>("Cloud")!.PointCount > 8000);
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
                await VerifyLocalPhotoAsync(window);
        }
        finally { window.Close(); }
    }

    private static async Task VerifyLocalPhotoAsync(MappingWindow window)
    {
        await window.LoadPhotoAsync(Environment.GetEnvironmentVariable("TOBOO_DEPTH_TEST_IMAGE")!);
        Assert.Contains("照片估计", window.FindControl<TextBlock>("DepthStatus")!.Text);
        Assert.True(window.FindControl<PointCloudView>("Cloud")!.PointCount > 8000);
        Assert.Equal("实测距离 —", window.FindControl<TextBlock>("RangeStatus")!.Text);
        Assert.NotNull(window.FindControl<Image>("DepthPreview")!.Source);
        Assert.True(window.FindControl<Button>("PhotoButton")!.IsEnabled);
        await Task.Delay(80);
        using var screenshot = window.CaptureRenderedFrame();
        Assert.NotNull(screenshot);
        var destination = Environment.GetEnvironmentVariable("TOBOO_DEPTH_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(destination)) screenshot.Save(destination);
    }

    [AvaloniaFact]
    public async Task InvalidPhotoReportsFailureAndAllowsRetry()
    {
        var window = new MappingWindow();
        try
        {
            window.Show();
            await window.LoadPhotoAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg"));
            Assert.Contains("照片分析失败", window.FindControl<TextBlock>("DepthStatus")!.Text);
            Assert.True(window.FindControl<Button>("PhotoButton")!.IsEnabled);
            Assert.False(window.FindControl<Button>("StopButton")!.IsEnabled);
        }
        finally { window.Close(); }
    }
}
