using Toboo.Core;
using Xunit;

namespace Toboo.Tests;

public sealed class ControllerTests
{
    private static TargetObservation Target(long frame, double time, double x = 500, double y = 300) =>
        new(frame, TimeSpan.FromSeconds(time), 640, 480, x, y);

    [Fact]
    public void ErrorControlsDirectionAndSpeedIsLimited()
    {
        var controller = new PanTiltController(new() { MaxSpeedDegreesPerSecond = 5 });
        controller.Step(Target(0, 0), TimeSpan.Zero);
        var command = controller.Step(Target(1, 0.1), TimeSpan.FromSeconds(0.1));
        Assert.InRange(command.PanDegrees, 90.01, 90.5);
        Assert.InRange(command.TiltDegrees, 90.01, 90.5);
        Assert.Equal(0.28125, command.ErrorX!.Value, 6);
    }

    [Fact]
    public void ReversedMountingReversesMotion()
    {
        var controller = new PanTiltController(new() { PanDirection = -1, TiltDirection = -1 });
        controller.Step(Target(0, 0), TimeSpan.Zero);
        var command = controller.Step(Target(1, 0.1), TimeSpan.FromSeconds(0.1));
        Assert.True(command.PanDegrees < 90);
        Assert.True(command.TiltDegrees < 90);
    }

    [Fact]
    public void CenterDeadZoneHoldsBothAxes()
    {
        var controller = new PanTiltController(new());
        controller.Step(Target(0, 0, 325, 243), TimeSpan.Zero);
        var command = controller.Step(Target(1, 0.1, 325, 243), TimeSpan.FromSeconds(0.1));
        Assert.Equal(TrackingState.Centered, command.State);
        Assert.Equal(90, command.PanDegrees);
        Assert.Equal(90, command.TiltDegrees);
    }

    [Fact]
    public void PersistentErrorCannotExceedAngleLimits()
    {
        var controller = new PanTiltController(new());
        TrackingCommand? command = null;
        for (var index = 0; index < 300; index++)
            command = controller.Step(Target(index, index / 10.0, 639, 479), TimeSpan.FromSeconds(index / 10.0));
        Assert.Equal(120, command!.PanDegrees);
        Assert.Equal(110, command.TiltDegrees);
    }

    [Fact]
    public void MissingTargetHoldsImmediatelyAndThenReportsLost()
    {
        var controller = new PanTiltController(new());
        controller.Step(Target(0, 0), TimeSpan.Zero);
        var moving = controller.Step(Target(1, 0.1), TimeSpan.FromSeconds(0.1));
        var missing = controller.Step(null, TimeSpan.FromSeconds(0.2));
        var lost = controller.Step(null, TimeSpan.FromSeconds(0.7));
        Assert.Equal(TrackingState.Missing, missing.State);
        Assert.Equal(TrackingState.Lost, lost.State);
        Assert.Equal(moving.PanDegrees, lost.PanDegrees);
        Assert.Null(lost.ErrorX);
    }

    [Fact]
    public void InvalidDetectionDoesNotBecomeZeroError()
    {
        var controller = new PanTiltController(new());
        var command = controller.Step(new(0, TimeSpan.Zero, 640, 480, null, null), TimeSpan.Zero);
        Assert.Equal(TrackingState.Waiting, command.State);
        Assert.Null(command.ErrorX);
        Assert.Null(command.ErrorY);
    }

    [Fact]
    public void StaleAndDuplicateFramesDoNotMove()
    {
        var controller = new PanTiltController(new());
        controller.Step(Target(0, 0), TimeSpan.Zero);
        var stale = controller.Step(Target(1, 0), TimeSpan.FromSeconds(0.3));
        var repeated = controller.Step(Target(1, 0.4), TimeSpan.FromSeconds(0.4));
        Assert.Equal(TrackingState.Stale, stale.State);
        Assert.Equal(90, stale.PanDegrees);
        Assert.Equal(stale.PanDegrees, repeated.PanDegrees);
    }

    [Fact]
    public void LongPauseDoesNotIntegrateIntoLargeJump()
    {
        var controller = new PanTiltController(new());
        controller.Step(Target(0, 0), TimeSpan.Zero);
        var gap = controller.Step(Target(1, 5), TimeSpan.FromSeconds(5));
        var resumed = controller.Step(Target(2, 5.03), TimeSpan.FromSeconds(5.03));
        Assert.Equal(TrackingState.TimingGap, gap.State);
        Assert.Equal(90, gap.PanDegrees);
        Assert.InRange(resumed.PanDegrees, 90.001, 90.9);
    }

    [Fact]
    public void EmptyPollingDoesNotChangeTheFrameUpdateInterval()
    {
        var settings = new ControllerSettings();
        var direct = new PanTiltController(settings);
        var polled = new PanTiltController(settings);
        direct.Step(Target(0, 0), TimeSpan.Zero);
        polled.Step(Target(0, 0), TimeSpan.Zero);
        polled.Step(null, TimeSpan.FromSeconds(0.09));
        var expected = direct.Step(Target(1, 0.1), TimeSpan.FromSeconds(0.1));
        var actual = polled.Step(Target(1, 0.1), TimeSpan.FromSeconds(0.1));
        Assert.Equal(expected.PanDegrees, actual.PanDegrees);
    }

    [Fact]
    public void ClockMustBeMonotonic()
    {
        var controller = new PanTiltController(new());
        controller.Step(Target(0, 1), TimeSpan.FromSeconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Step(null, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void InvalidMountingDirectionIsRejected(int direction) =>
        Assert.Throws<ArgumentException>(() => new PanTiltController(new() { PanDirection = direction }));

    [Fact]
    public void NonFiniteSettingsAreRejected() =>
        Assert.Throws<ArgumentException>(() => new PanTiltController(new() { PanGain = double.NaN }));
}
