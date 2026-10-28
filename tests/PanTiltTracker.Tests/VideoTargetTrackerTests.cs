using OpenCvSharp;
using PanTiltTracker.Core;
using PanTiltTracker.Vision;
using Xunit;

namespace PanTiltTracker.Tests;

public sealed class VideoTargetTrackerTests
{
    [Fact]
    public void CsrtTracksSyntheticTranslationInOriginalUnmirroredCoordinates()
    {
        var region = new Rect(900, 180, 96, 64);
        using var tracker = new VideoTargetTracker(VideoAlgorithm.Csrt, region);
        using var patch = Patch();
        for (var index = 0; index < 8; index++)
        {
            var box = region with { X = region.X + index * 3, Y = region.Y + index * 2 };
            using var image = Scene(patch, box);
            using var before = image.Clone();
            var result = tracker.Process(image, index, TimeSpan.FromSeconds(index / 30.0));
            Assert.True(result.Observation.HasTarget);
            Assert.InRange(result.Observation.CenterX!.Value, box.X + 38, box.X + 58);
            Assert.InRange(result.Observation.CenterY!.Value, box.Y + 22, box.Y + 42);
            Assert.Equal(1280, result.Observation.Width);
            Assert.Equal(0, Cv2.Norm(image, before));
        }
    }

    [Fact]
    public void CsrtLossDoesNotReuseOldCoordinatesOrAutomaticallyReacquire()
    {
        var region = new Rect(500, 180, 96, 64);
        using var tracker = new VideoTargetTracker(VideoAlgorithm.Csrt, region);
        using var patch = Patch();
        using var visible = Scene(patch, region);
        using var blank = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(25, 25, 25));
        var controller = new PanTiltController(new());
        controller.Step(tracker.Process(visible, 0, TimeSpan.Zero).Observation, TimeSpan.Zero);
        var previous = controller.Step(tracker.Process(visible, 1, TimeSpan.FromSeconds(0.03)).Observation,
            TimeSpan.FromSeconds(0.03));
        var missing = tracker.Process(blank, 2, TimeSpan.FromSeconds(0.06));
        Assert.Null(missing.Box);
        Assert.False(missing.Observation.HasTarget);
        Assert.Equal("lost_reinitialize", missing.Status);
        var held = controller.Step(missing.Observation, TimeSpan.FromSeconds(0.06));
        Assert.Equal(previous.PanDegrees, held.PanDegrees);
        Assert.Equal(previous.TiltDegrees, held.TiltDegrees);
        Assert.False(tracker.Process(visible, 3, TimeSpan.FromSeconds(0.09)).Observation.HasTarget);
    }

    [Fact]
    public void FrameDifferenceDoesNotCallAStationaryObjectMoving()
    {
        using var tracker = new VideoTargetTracker(VideoAlgorithm.FrameDifference);
        using var first = new Mat(360, 640, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(first, new Rect(300, 150, 60, 40), Scalar.White, -1);
        Assert.False(tracker.Process(first, 0, TimeSpan.Zero).Observation.HasTarget);
        Assert.False(tracker.Process(first, 1, TimeSpan.FromSeconds(0.03)).Observation.HasTarget);
        using var moved = new Mat(360, 640, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(moved, new Rect(330, 150, 60, 40), Scalar.White, -1);
        var result = tracker.Process(moved, 2, TimeSpan.FromSeconds(0.06));
        Assert.True(result.Observation.HasTarget);
        Assert.True(result.CandidateCount >= 1);
        Assert.Equal("motion_candidate_not_classified", result.Status);
        Assert.False(tracker.MotionMask.Empty());
        Assert.False(tracker.Process(moved, 3, TimeSpan.FromSeconds(0.09)).Observation.HasTarget);
    }

    [Fact]
    public void InvalidInitializationFrameReuseAndSizeChangesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new VideoTargetTracker(VideoAlgorithm.Csrt));
        using var invalid = new VideoTargetTracker(VideoAlgorithm.Csrt, new Rect(1200, 700, 96, 64));
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, Scalar.Black);
        Assert.Throws<ArgumentException>(() => invalid.Process(frame, 0, TimeSpan.Zero));
        using var tracker = new VideoTargetTracker(VideoAlgorithm.FrameDifference);
        tracker.Process(frame, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => tracker.Process(frame, 0, TimeSpan.Zero));
        using var otherSize = new Mat(360, 640, MatType.CV_8UC3, Scalar.Black);
        Assert.Throws<ArgumentException>(() => tracker.Process(otherSize, 1, TimeSpan.Zero));
        tracker.Dispose();
        Assert.Throws<ObjectDisposedException>(() => tracker.Process(frame, 1, TimeSpan.Zero));
        Assert.False(frame.Empty());
    }

    private static Mat Patch()
    {
        var patch = new Mat(64, 96, MatType.CV_8UC3, Scalar.White);
        var random = new Random(42);
        for (var y = 0; y < 64; y += 8)
            for (var x = 0; x < 96; x += 8)
                Cv2.Rectangle(patch, new Rect(x, y, 8, 8), new Scalar(random.Next(256), random.Next(256), random.Next(256)), -1);
        return patch;
    }

    private static Mat Scene(Mat patch, Rect region)
    {
        var image = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(25, 25, 25));
        using var destination = new Mat(image, region);
        patch.CopyTo(destination);
        return image;
    }
}
