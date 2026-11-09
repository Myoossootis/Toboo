using OpenCvSharp;
using Toboo.Desktop.Services;
using Toboo.Desktop.ViewModels;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class CameraTests
{
    [Fact]
    public void OnlySupportedCaptureBackendsAndPositiveSizesAreAccepted()
    {
        new CameraSettings().Validate();
        new CameraSettings(1920, 1080, 30, VideoCaptureAPIs.MSMF).Validate();
        Assert.Throws<ArgumentException>(() => new CameraSettings(0).Validate());
        Assert.Throws<ArgumentException>(() => new CameraSettings(Fps: 0).Validate());
        Assert.Throws<ArgumentException>(() => new CameraSettings(Backend: VideoCaptureAPIs.ANY).Validate());
    }

    [AvaloniaFact]
    public async Task NamedCameraSelectionUpdatesManualIndexAndAllowsPreviewWithoutAMarker()
    {
        using var model = new MainViewModel() { SourceIndex = 3 };
        var window = new AdvancedWindow(model);
        try
        {
            window.Show();
            model.Cameras.Add(new(0, "Laptop camera"));
            model.Cameras.Add(new(1, "USB camera"));
            var selector = window.FindControl<ComboBox>("CameraSelector")!;
            selector.SelectedItem = model.Cameras[1];
            await Task.Delay(20);
            Assert.Equal(1, model.CameraIndex);
            Assert.Equal(model.Cameras[1], model.SelectedCamera);
            Assert.True(selector.IsEffectivelyVisible);
            Assert.True(model.CanStart);
            Assert.Contains("预览", model.SourceDescription);
            Assert.False(model.CanSelectRegion);
        }
        finally { window.Close(); }
    }
}
