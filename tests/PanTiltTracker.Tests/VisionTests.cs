using OpenCvSharp;
using PanTiltTracker.Core;
using PanTiltTracker.Vision;
using Xunit;

namespace PanTiltTracker.Tests;

public sealed class VisionTests
{
    [Fact]
    public void ActualArucoDetectorFindsTheGeneratedMarker()
    {
        using var scene = new SyntheticScene();
        using var image = scene.RenderAt(440, 200);
        using var detector = new ArucoTargetDetector();
        var result = detector.Detect(image, 3, TimeSpan.FromSeconds(1));
        Assert.True(result.HasTarget);
        Assert.InRange(result.CenterX!.Value, 438, 442);
        Assert.InRange(result.CenterY!.Value, 198, 202);
    }

    [Fact]
    public void ColorInputHasTheSameCoordinates()
    {
        using var scene = new SyntheticScene();
        using var gray = scene.RenderAt(300, 220);
        using var color = new Mat();
        Cv2.CvtColor(gray, color, ColorConversionCodes.GRAY2BGR);
        using var detector = new ArucoTargetDetector();
        var first = detector.Detect(gray, 0, TimeSpan.Zero);
        var second = detector.Detect(color, 1, TimeSpan.Zero);
        Assert.Equal(first.CenterX, second.CenterX);
        Assert.Equal(first.CenterY, second.CenterY);
    }

    [Fact]
    public void WrongIdAndBlankImageAreMissingTargets()
    {
        using var scene = new SyntheticScene(8);
        using var image = scene.RenderAt(320, 240);
        using var blank = scene.RenderAt(320, 240, hidden: true);
        using var detector = new ArucoTargetDetector(7);
        Assert.False(detector.Detect(image, 0, TimeSpan.Zero).HasTarget);
        Assert.False(detector.Detect(blank, 1, TimeSpan.Zero).HasTarget);
    }

    [Fact]
    public void DuplicateTargetIdsAreRejected()
    {
        using var scene = new SyntheticScene();
        using var left = scene.RenderAt(160, 240);
        using var right = scene.RenderAt(480, 240);
        using var combined = new Mat();
        Cv2.Min(left, right, combined);
        using var detector = new ArucoTargetDetector();
        Assert.False(detector.Detect(combined, 0, TimeSpan.Zero).HasTarget);
    }

    [Fact]
    public void IdealClosedLoopConvergesForAStationaryTarget()
    {
        using var scene = new SyntheticScene();
        using var detector = new ArucoTargetDetector();
        var controller = new PanTiltController(new());
        var pan = 90.0;
        var tilt = 90.0;
        TrackingCommand? command = null;
        for (var index = 0; index < 120; index++)
        {
            using var image = scene.Render(0, pan, tilt);
            var time = TimeSpan.FromSeconds(index / 30.0);
            var observation = detector.Detect(image, index, time);
            Assert.True(observation.HasTarget);
            command = controller.Step(observation, time);
            pan = command.PanDegrees;
            tilt = command.TiltDegrees;
        }
        Assert.InRange(Math.Abs(command!.ErrorX!.Value), 0, 0.025);
        Assert.InRange(Math.Abs(command.ErrorY!.Value), 0, 0.025);
    }

    [Fact]
    public void OcclusionHoldsCommandsAndDetectionCanRecover()
    {
        using var scene = new SyntheticScene();
        using var detector = new ArucoTargetDetector();
        var controller = new PanTiltController(new());
        using var visible = scene.RenderAt(440, 200);
        using var hidden = scene.RenderAt(440, 200, hidden: true);
        controller.Step(detector.Detect(visible, 0, TimeSpan.Zero), TimeSpan.Zero);
        var time = TimeSpan.FromSeconds(0.1);
        var previous = controller.Step(detector.Detect(visible, 1, time), time);
        time = TimeSpan.FromSeconds(0.7);
        var lost = controller.Step(detector.Detect(hidden, 2, time), time);
        Assert.Equal(TrackingState.Lost, lost.State);
        Assert.Equal(previous.PanDegrees, lost.PanDegrees);
        time = TimeSpan.FromSeconds(0.8);
        var recovered = detector.Detect(visible, 3, time);
        Assert.True(recovered.HasTarget);
        Assert.Equal(TrackingState.TimingGap, controller.Step(recovered, time).State);
    }
}
