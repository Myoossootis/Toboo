using PanTiltTracker.Core;
using Xunit;

namespace PanTiltTracker.Tests;

public sealed class FaceAimTests
{
    private static readonly CameraAngleModel Camera = CameraAngleModel.FromFieldOfView(640, 480, 60, 45);
    private static FaceAimSettings Ready => new() { Camera = Camera, DirectionsConfirmed = true };
    private static FaceRegion Face(double centerX = 470, double centerY = 240, double size = 80) =>
        new(centerX - size / 2, centerY - size / 2, size, size);
    private static FaceAimController Controller(FaceAimSettings? settings = null) => new(settings ?? Ready, new(103, 50));
    private static FaceAimResult Step(FaceAimController controller, int frame, params FaceRegion[] faces) =>
        controller.Step(faces, frame, TimeSpan.FromSeconds(frame / 30.0), 640, 480, TimeSpan.FromSeconds(frame / 30.0));
    private static FaceRegion AtAngle(double degrees) => Face(320 + Camera.Fx * Math.Tan(degrees * Math.PI / 180));

    [Fact]
    public void SightAnglesScaleWithResolutionAndNeverMirror()
    {
        Assert.Equal(new SightAngles(0, 0), Camera.Project(320, 240, 640, 480));
        var a = Camera.Project(470, 300, 640, 480);
        var b = Camera.Project(940, 600, 1280, 960);
        Assert.Equal(a, b);
        Assert.True(a.HorizontalDegrees > 0 && a.VerticalDegrees > 0);
        Assert.Equal(-a.HorizontalDegrees, Camera.Project(170, 300, 640, 480).HorizontalDegrees, 8);
        Assert.Equal(30, Camera.Project(639.999999, 240, 640, 480).HorizontalDegrees, 6);
        Assert.Throws<ArgumentException>(() => Camera.Project(470, 300, 1280, 720));
    }

    [Fact]
    public void IsolatedDetectionDoesNotBecomeAMotionRequest()
    {
        var controller = Controller();
        Assert.Equal("face_confirming", Step(controller, 0, Face()).Status);
        var missing = Step(controller, 1);
        Assert.False(missing.RequestPermitted);
        Assert.False(missing.Observation.HasTarget);
        Assert.Equal(103, missing.Command.PanDegrees);
        Assert.Equal("face_confirming", Step(controller, 2, Face()).Status);
        Assert.Equal("face_confirming", Step(controller, 3, Face()).Status);
        var acquired = Step(controller, 4, Face());
        Assert.True(acquired.Observation.HasTarget);
        Assert.Equal(1, acquired.LockEpoch);
        Assert.False(acquired.RequestPermitted); // Prime the clock, no first-frame jump.
        Assert.True(Step(controller, 5, Face()).RequestPermitted);
    }

    [Fact]
    public void LargerSecondFaceDoesNotStealTheSpatialLock()
    {
        var controller = Controller();
        for (var i = 0; i < 3; i++) Step(controller, i, Face());
        var result = Step(controller, 3, Face(110, 240, 180), Face(473));
        Assert.Equal(473, result.Observation.CenterX);
        var missed = Step(controller, 4, Face(110, 240, 180));
        Assert.Equal("face_lock_missing", missed.Status);
        Assert.Null(missed.SelectedBox);
        Assert.Null(missed.FilteredX);
        Assert.False(missed.RequestPermitted);
        var recovered = Step(controller, 5, Face(475));
        Assert.Equal(1, recovered.LockEpoch);
        Assert.Equal(result.Command.PanDegrees, recovered.Command.PanDegrees);
    }

    [Fact]
    public void LossExpiresLockAndNewPersonNeedsConfirmation()
    {
        var controller = Controller();
        for (var i = 0; i < 4; i++) Step(controller, i, Face());
        for (var i = 4; i < 17; i++) Step(controller, i);
        Assert.Equal("face_confirming", Step(controller, 17, Face(120)).Status);
        Assert.Equal("face_confirming", Step(controller, 18, Face(120)).Status);
        var reacquired = Step(controller, 19, Face(120));
        Assert.Equal(2, reacquired.LockEpoch);
        Assert.False(reacquired.RequestPermitted);
        Assert.Equal(120, reacquired.FilteredX);
    }

    [Fact]
    public void MissingGeometryDirectionsAndReferenceIndependentlyBlockRequests()
    {
        foreach (var settings in new[] { new FaceAimSettings(), Ready with { DirectionsConfirmed = false } })
        {
            var controller = Controller(settings);
            FaceAimResult? result = null;
            for (var i = 0; i < 15; i++) result = Step(controller, i, Face());
            Assert.False(result!.RequestPermitted);
            Assert.Equal(103, result.Command.PanDegrees);
            Assert.NotEqual("ready", result.Readiness);
        }
        var unreferenced = new FaceAimController(Ready);
        FaceAimResult? preview = null;
        for (var i = 0; i < 15; i++) preview = Step(unreferenced, i, Face());
        Assert.Equal("request_reference_required", preview!.Readiness);
        Assert.False(preview.HasReference);
        Assert.NotNull(preview.SightError);
        Assert.False(preview.RequestPermitted);
    }

    [Fact]
    public void WrongCameraAspectRatioHoldsInsteadOfGuessingAngles()
    {
        var controller = Controller(Ready with { Camera = CameraAngleModel.FromFieldOfView(1280, 720, 60, 40) });
        FaceAimResult? result = null;
        for (var i = 0; i < 10; i++) result = Step(controller, i, Face());
        Assert.Equal("camera_geometry_mismatch", result!.Readiness);
        Assert.Null(result.SightError);
        Assert.Equal(103, result.Command.PanDegrees);
    }

    [Fact]
    public void FilterReducesAlternatingJitterWithoutInventingABox()
    {
        var controller = Controller();
        var rawSquared = 0.0; var filteredSquared = 0.0;
        FaceAimResult? last = null;
        for (var i = 0; i < 120; i++)
        {
            var raw = 320 + (i % 2 == 0 ? 8 : -8);
            last = Step(controller, i, Face(raw));
            if (i < 30) continue;
            Assert.Equal(raw, last.Observation.CenterX);
            rawSquared += Math.Pow(raw - 320, 2);
            filteredSquared += Math.Pow(last.FilteredX!.Value - 320, 2);
        }
        Assert.True(filteredSquared < rawSquared * 0.10);
        Assert.Equal(103, last!.Command.PanDegrees);
        Assert.Equal(0, last.PanVelocity);
        Assert.Null(Step(controller, 120).FilteredX);
    }

    [Fact]
    public void TimeBasedFilterIsConsistentAcrossDifferentFrameIntervals()
    {
        var settings = Ready with { ConfirmationFrames = 1 };
        var fast = Controller(settings); var slow = Controller(settings);
        Step(fast, 0, Face(450)); Step(slow, 0, Face(450));
        FaceAimResult? a = null; FaceAimResult? b = null;
        foreach (var (time, index) in new[] { (0.02, 1), (0.05, 2), (0.10, 3) })
            a = fast.Step([Face(470)], index, TimeSpan.FromSeconds(time), 640, 480, TimeSpan.FromSeconds(time));
        foreach (var (time, index) in new[] { (0.04, 1), (0.10, 2) })
            b = slow.Step([Face(470)], index, TimeSpan.FromSeconds(time), 640, 480, TimeSpan.FromSeconds(time));
        Assert.Equal(a!.FilteredX!.Value, b!.FilteredX!.Value, 6);
        Assert.Equal(450 + 20 * (1 - Math.Exp(-1)), a.FilteredX.Value, 6);
    }

    [Fact]
    public void DeadZoneHysteresisStopsChatterBetweenEntryAndExitThresholds()
    {
        var controller = Controller(Ready with { ConfirmationFrames = 1, FilterTimeConstantSeconds = 0.0001 });
        for (var i = 0; i < 10; i++)
            Assert.Equal(103, Step(controller, i, AtAngle(i % 2 == 0 ? 1.2 : 1.7)).Command.PanDegrees);
        Assert.True(Step(controller, 10, AtAngle(2)).Command.PanDegrees > 103);
        for (var i = 11; i < 30; i++) Step(controller, i, AtAngle(0.5));
        var stopped = Step(controller, 30, AtAngle(0.5));
        var insideBand = Step(controller, 31, AtAngle(1.5));
        Assert.Equal(TrackingState.Centered, insideBand.Command.State);
        Assert.Equal(stopped.Command.PanDegrees, insideBand.Command.PanDegrees);
    }

    [Fact]
    public void SpeedAndAccelerationAreBoundedDuringMotionAndReversal()
    {
        var settings = Ready with { MaxSpeedDegreesPerSecond = 5,
            MaxAccelerationDegreesPerSecondSquared = 20, ConfirmationFrames = 1 };
        var controller = Controller(settings);
        var previous = Step(controller, 0, AtAngle(8));
        for (var i = 1; i < 100; i++)
        {
            // Move the face continuously through center, so this remains the same spatial lock.
            var degrees = i < 40 ? 8 : Math.Max(-8, 8 - (i - 40) * 0.8);
            var result = Step(controller, i, AtAngle(degrees));
            Assert.True(result.RequestPermitted);
            Assert.InRange(Math.Abs(result.PanVelocity), 0, 5.000001);
            var dt = (result.Observation.CapturedAt - previous.Observation.CapturedAt).TotalSeconds;
            Assert.InRange(Math.Abs(result.PanVelocity - previous.PanVelocity) / dt, 0, 20.00001);
            Assert.InRange(Math.Abs(result.Command.PanDegrees - previous.Command.PanDegrees) / dt, 0, 5.000001);
            previous = result;
        }
        Assert.True(previous.PanVelocity < 0);
    }

    [Fact]
    public void LimitsAndReversedMountingAreAppliedToRequests()
    {
        var normal = Controller(); var reversed = Controller(Ready with { PanDirection = -1 });
        FaceAimResult? a = null; FaceAimResult? b = null;
        for (var i = 0; i < 600; i++)
        {
            a = Step(normal, i, Face(500, 350));
            b = Step(reversed, i, Face(500, 350));
            Assert.InRange(a.Command.PanDegrees, 30, 175);
            Assert.InRange(a.Command.TiltDegrees, 35, 65);
        }
        Assert.Equal(175, a!.Command.PanDegrees);
        Assert.Equal(65, a.Command.TiltDegrees);
        Assert.Equal(30, b!.Command.PanDegrees);
        Assert.Equal(0, a.PanVelocity);
    }

    [Fact]
    public void StaleDuplicateMissingAndLongGapHoldAndRestartWithoutCatchup()
    {
        var controller = Controller();
        FaceAimResult? previous = null;
        for (var i = 0; i < 8; i++) previous = Step(controller, i, Face());
        var stale = controller.Step([Face()], 8, TimeSpan.FromSeconds(0.26), 640, 480, TimeSpan.FromSeconds(0.5));
        Assert.Equal("face_stale_frame", stale.Status);
        Assert.False(stale.RequestPermitted);
        Assert.Equal(previous!.Command.PanDegrees, stale.Command.PanDegrees);
        Assert.Equal(0, stale.PanVelocity);
        var repeated = controller.Step([Face()], 7, TimeSpan.FromSeconds(0.23), 640, 480, TimeSpan.FromSeconds(0.5));
        Assert.Equal("face_repeated_frame", repeated.Status);
        var gap = controller.Step([Face()], 9, TimeSpan.FromSeconds(5), 640, 480, TimeSpan.FromSeconds(5));
        Assert.Equal(TrackingState.TimingGap, gap.Command.State);
        Assert.Equal(previous.Command.PanDegrees, gap.Command.PanDegrees);
        Assert.False(gap.Observation.HasTarget);
        for (var i = 10; i < 12; i++)
            controller.Step([Face()], i, TimeSpan.FromSeconds(5 + (i - 9) / 30.0), 640, 480, TimeSpan.FromSeconds(5 + (i - 9) / 30.0));
        var resumed = controller.Step([Face()], 12, TimeSpan.FromSeconds(5.1), 640, 480, TimeSpan.FromSeconds(5.1));
        Assert.InRange(resumed.Command.PanDegrees - previous.Command.PanDegrees, 0, 0.04);
        var missing = controller.Step([], 13, TimeSpan.FromSeconds(5.13), 640, 480, TimeSpan.FromSeconds(5.13));
        Assert.Equal(resumed.Command.PanDegrees, missing.Command.PanDegrees);
        Assert.Null(missing.SightError);
        Assert.False(missing.RequestPermitted);
    }

    [Fact]
    public void SyntheticIdealPlantConvergesButDoesNotClaimHardwareAccuracy()
    {
        var controller = Controller();
        var requested = 103.0;
        var error = 15.0;
        for (var i = 0; i < 240; i++)
        {
            error = 15 - (requested - 103); // Ideal instant plant, no backlash, latency or quantization.
            var result = Step(controller, i, AtAngle(error));
            requested = result.Command.PanDegrees;
        }
        Assert.InRange(Math.Abs(error), 0, 1.8);
        Assert.InRange(requested, 115, 119);
    }

    [Fact]
    public void InvalidSettingsBoxesReferenceAndClockAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Controller(Ready with { MaxAccelerationDegreesPerSecondSquared = double.NaN }));
        Assert.Throws<ArgumentException>(() => Controller(Ready with { DeadZoneExitDegrees = 0.5 }));
        Assert.Throws<ArgumentException>(() => new FaceAimController(Ready, new(90, 90)));
        var controller = Controller();
        Assert.Throws<ArgumentException>(() => Step(controller, 0, new FaceRegion(-1, 0, 80, 80)));
        Step(controller, 1, Face());
        Assert.Throws<ArgumentOutOfRangeException>(() => Step(controller, 0, Face()));
    }
}
