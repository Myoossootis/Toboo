using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using OpenCvSharp;
using Toboo.Core;
using Toboo.Desktop.Services;
using Toboo.Desktop.ViewModels;
using Toboo.Vision;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class FaceAimWorkbenchTests
{
    [Fact]
    public void TwentyDegreeSpeedRoundTripsAndHigherSpeedIsRejected()
    {
        var file = Path.Combine(Path.GetTempPath(), "UAVTrack-tests", Guid.NewGuid() + ".json");
        var model = new FaceAimViewModel(file) { SpeedLimit = 20 };
        model.SaveCommand.Execute(null);
        var restored = new FaceAimViewModel(file);
        Assert.Equal(20, restored.SpeedLimit);
        Assert.Equal(20, restored.Snapshot().MaxSpeedDegreesPerSecond);
        restored.SpeedLimit = 21;
        Assert.Throws<ArgumentException>(() => restored.Snapshot());
        File.Delete(file);
    }

    [Fact]
    public void FullConfiguredRangeIsDefaultAndSavedSpeedIsPreserved()
    {
        var file = Path.Combine(Path.GetTempPath(), "UAVTrack-tests", Guid.NewGuid() + ".json");
        var model = new FaceAimViewModel(file) { SpeedLimit = 15 };
        Assert.False(model.TrialRangeEnabled);
        Assert.Equal(2, model.FollowAxesIndex);
        model.SaveCommand.Execute(null);
        Assert.Equal(15, new FaceAimViewModel(file).SpeedLimit);
        File.Delete(file);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, true, true)]
    public void SelectedFollowAxesAreSavedAndRestored(int index, bool panEnabled, bool tiltEnabled)
    {
        var file = Path.Combine(Path.GetTempPath(), "UAVTrack-tests", Guid.NewGuid() + ".json");
        var model = new FaceAimViewModel(file) { FollowAxesIndex = index };
        model.SaveCommand.Execute(null);
        var restored = new FaceAimViewModel(file);
        Assert.Equal(index, restored.FollowAxesIndex);
        Assert.Equal(panEnabled, restored.Snapshot().PanEnabled);
        Assert.Equal(tiltEnabled, restored.Snapshot().TiltEnabled);
        File.Delete(file);
    }

    [Fact]
    public void RequestedBoundaryIsReportedWhileLockedAndDoesNotBlockMovingAway()
    {
        var settings = new FaceAimSettings { Camera = CameraAngleModel.FromFieldOfView(640, 480, 60, 45),
            DirectionsConfirmed = true, PanMinDegrees = 102, PanMaxDegrees = 112, TiltEnabled = false };
        var controller = new FaceAimController(settings, new(112, 53));
        FaceAimResult? aim = null;
        for (var frame = 0; frame < 4; frame++) aim = controller.Step([new(400, 200, 80, 80)], frame,
            TimeSpan.FromSeconds(frame / 30.0), 640, 480, TimeSpan.FromSeconds(frame / 30.0));
        Assert.Contains("112° 上限", MainViewModel.BoundaryMessage(aim!, settings));
        Assert.Equal("", MainViewModel.BoundaryMessage(aim! with { SightError = new(-10, 0) }, settings));
        Assert.Equal("", MainViewModel.BoundaryMessage(aim! with { SightError = null }, settings));
        Assert.Equal("", MainViewModel.BoundaryMessage(aim!, settings with { PanEnabled = false }));
    }

    [Fact]
    public void SavedFovEstimateReopensAsAnEstimateWithoutChangingItsAngles()
    {
        var file = Path.Combine(Path.GetTempPath(), "UAVTrack-tests", Guid.NewGuid() + ".json");
        var model = new FaceAimViewModel(file) { GeometryMode = 1, HorizontalFov = 60, VerticalFov = 45,
            PanDirectionIndex = 0, TiltDirectionIndex = 1, DirectionsConfirmed = true };
        model.SaveCommand.Execute(null);
        var restored = new FaceAimViewModel(file);
        Assert.Equal(1, restored.GeometryMode);
        Assert.Equal(60, restored.HorizontalFov, 8);
        Assert.Equal(45, restored.VerticalFov, 8);
        Assert.True(restored.Snapshot().Camera!.Approximate);
        Assert.True(restored.DirectionsConfirmed);
        Assert.Equal(-1, restored.Snapshot().TiltDirection);
        File.Delete(file);
    }

    [Fact]
    public void TrialBoundsAreRelativeToFreshRequestAndNeverExpandSavedLimits()
    {
        int[] limits = [30, 175, 35, 65];
        Assert.Equal([98, 108, 45, 55], FaceServoSession.CalculateTrialBounds(limits, new(103, 50), 5));
        Assert.Equal([30, 35, 60, 65], FaceServoSession.CalculateTrialBounds(limits, new(30, 65), 5));
        Assert.Equal([100, 106, 48, 53], FaceServoSession.CalculateTrialBounds([100, 106, 48, 53], new(103, 50), 5));
        Assert.Equal([30, 175, 35, 65], limits);
        Assert.Throws<ArgumentException>(() => FaceServoSession.CalculateTrialBounds(limits, new(-1, 50), 5));
        Assert.Throws<ArgumentException>(() => FaceServoSession.CalculateTrialBounds(limits, new(103, 50), 20));
    }

    [Fact]
    public void SettingsRoundTripPreservesIntrinsicsAndDirectionConfirmation()
    {
        var file = Path.Combine(Path.GetTempPath(), "UAVTrack-tests", Guid.NewGuid() + ".json");
        var model = new FaceAimViewModel(file) { GeometryMode = 2, Fx = 600, Fy = 610, Cx = 308, Cy = 231,
            PanDirectionIndex = 1, TiltDirectionIndex = 0, DirectionsConfirmed = true, SpeedLimit = 7 };
        model.SaveCommand.Execute(null);
        var restored = new FaceAimViewModel(file);
        Assert.Equal(308, restored.Snapshot().Camera!.Cx);
        Assert.Equal(231, restored.Snapshot().Camera!.Cy);
        Assert.Equal(-1, restored.Snapshot().PanDirection);
        Assert.Equal(1, restored.Snapshot().TiltDirection);
        Assert.True(restored.DirectionsConfirmed);
        restored.PanDirectionIndex = 0;
        Assert.False(restored.DirectionsConfirmed);
        restored.DeadZoneExit = restored.DeadZoneEnter;
        Assert.Throws<ArgumentException>(() => restored.Snapshot());
        File.Delete(file);
    }

    [Fact]
    public void AngularGuidesMatchOffCentreOpticalAxisAndRejectWrongAspectRatio()
    {
        using var image = new Mat(480, 640, MatType.CV_8UC3, Scalar.Black);
        var settings = new FaceAimSettings { Camera = new(640, 480, 600, 610, 308, 231, false) };
        FaceAimOverlay.Draw(image, settings, null);
        Assert.Equal(new Vec3b(255, 255, 255), image.At<Vec3b>(231, 308));
        using var wrongAspect = new Mat(360, 640, MatType.CV_8UC3, Scalar.Black);
        FaceAimOverlay.Draw(wrongAspect, settings, null);
        Assert.Equal(0, Cv2.CountNonZero(wrongAspect.Reshape(1)));
    }

    [AvaloniaTheory]
    [InlineData(1280, 860)]
    [InlineData(980, 700)]
    public async Task PreviewSettingsAndFollowGateAreVisibleWithoutActuatorAccess(int width, int height)
    {
        using var model = new MainViewModel { SourceIndex = 3, TargetIndex = 1 };
        model.FaceAim.GeometryMode = 1;
        model.FaceAim.DirectionsConfirmed = false;
        var window = new AdvancedWindow(model) { Width = width, Height = height };
        try
        {
            window.Show();
            window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 3;
            await Task.Delay(80);
            var geometry = window.FindControl<ComboBox>("FaceGeometryMode")!;
            Assert.True(geometry.IsEffectivelyVisible);
            Assert.InRange(geometry.Bounds.Width, 120, 230);
            var follow = window.FindControl<Button>("FaceFollowButton")!;
            Assert.True(follow.IsEffectivelyVisible);
            Assert.False(follow.IsEffectivelyEnabled);
            Assert.Contains("转动云台", follow.Content!.ToString());
            Assert.Contains("不转动", window.FindControl<Button>("StartButton")!.Content!.ToString());
            Assert.True(window.FindControl<Button>("StartButton")!.IsEffectivelyEnabled);
            var controller = new FaceAimController(model.FaceAim.Snapshot());
            FaceAimResult? aim = null;
            for (var i = 0; i < 4; i++) aim = controller.Step([new(400, 220, 80, 80)], i,
                TimeSpan.FromSeconds(i / 30.0), 640, 480, TimeSpan.FromSeconds(i / 30.0));
            model.PresentFaceAim(aim);
            Assert.Contains("水平 +", model.FaceSightError);
            Assert.Contains("待确认", model.FaceCorrection);
            Assert.False(aim!.RequestPermitted);
            var folder = Path.Combine(Workspace(), "artifacts", "desktop-preview");
            Directory.CreateDirectory(folder);
            using var rendered = window.CaptureRenderedFrame();
            Assert.NotNull(rendered);
            rendered.Save(Path.Combine(folder, $"face-aim-settings-{width}.png"));
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task FileInputsCannotBeConnectedToActuators()
    {
        foreach (var source in new[] { InputSource.Simulation, InputSource.Image, InputSource.Video })
        {
            var outputCalls = 0;
            var options = new SessionOptions(source, "does-not-exist", 0, 7, new(), "unused", DetectFaces: true,
                FaceAim: new() { Camera = CameraAngleModel.FromFieldOfView(640, 480, 60, 45), DirectionsConfirmed = true },
                FaceAimReference: new(103, 50));
            await Assert.ThrowsAsync<ArgumentException>(() => TrackingSession.RunAsync(options, _ => Task.CompletedTask,
                () => false, CancellationToken.None, (_, _, _) => { outputCalls++; return Task.FromResult(new FollowOutputFeedback()); }));
            Assert.Equal(0, outputCalls);
        }
    }

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Toboo.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
