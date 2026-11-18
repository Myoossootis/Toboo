using OpenCvSharp;
using Toboo.Vision;
using Xunit;

namespace Toboo.Tests;

public sealed class MarkerMappingVisionTests
{
    [Fact]
    public void KnownCameraProjectionRecoversWorldPositionAndForwardDirection()
    {
        var calibration = new MappingIntrinsics(640, 480, 600, 600, 320, 240, new double[5], 0.1);
        Point3f[] square = [new(-.05f, .05f, 0), new(.05f, .05f, 0), new(.05f, -.05f, 0), new(-.05f, -.05f, 0)];
        Cv2.ProjectPoints(square, [Math.PI, 0, 0], [-0.10, 0.02, 0.6], calibration.Matrix,
            calibration.Distortion, out var corners, out _);
        var pose = MarkerMappingVision.SolvePose(corners, calibration, 0.1);
        Assert.NotNull(pose);
        Assert.InRange(pose.Value.Position.X, 0.099, 0.101);
        Assert.InRange(pose.Value.Position.Z, 0.599, 0.601);
        Assert.InRange(pose.Value.Forward.Z, -1.00001, -0.99999);
    }

    [Fact]
    public void RenderedTagIsDetectedAndOnlyMatchingCalibrationProducesPose()
    {
        var calibration = new MappingIntrinsics(640, 480, 600, 600, 320, 240, new double[5], 0.1);
        using var image = new Mat(480, 640, MatType.CV_8UC1, Scalar.White);
        using var tag = ArucoTargetDetector.CreateMarker(7, 120);
        using var roi = new Mat(image, new Rect(240, 160, tag.Width, tag.Height));
        tag.CopyTo(roi);
        using var vision = new MarkerMappingVision();
        Assert.Null(vision.Locate(image, calibration with { Width = 800 }, 0.1, out var corners));
        Assert.NotNull(corners);
        var pose = vision.Locate(image, calibration, 0.1, out _);
        Assert.NotNull(pose);
        Assert.InRange(pose.Value.Position.Z, 0.48, 0.53);
    }

    [Fact]
    public void CalibrationRecoversIntrinsicsFromDiverseSyntheticChessboardViews()
    {
        var k = new double[,] { { 570, 0, 322 }, { 0, 560, 236 }, { 0, 0, 1 } };
        var objectPoints = Enumerable.Range(0, 54).Select(i => new Point3f(i % 9, i / 9, 0)).ToArray();
        var samples = new List<Point2f[]>();
        for (var i = 0; i < 12; i++)
        {
            double[] r = [Math.Sin(i * .7) * .38, Math.Cos(i * .8) * .4, (i % 3 - 1) * .1];
            double[] t = [-4 + (i % 4 - 1.5) * 1.4, -2.5 + (i / 4 - 1) * 1.1, 19 + i % 5];
            Cv2.ProjectPoints(objectPoints, r, t, k, new double[5], out var imagePoints, out _);
            samples.Add(imagePoints);
        }
        Assert.False(MarkerMappingVision.IsNewCalibrationView(samples, samples[0].Reverse().ToArray(), 640));
        var calibration = MarkerMappingVision.Calibrate(samples, 640, 480);
        Assert.InRange(calibration.Fx, 569.8, 570.2);
        Assert.InRange(calibration.Fy, 559.8, 560.2);
        Assert.InRange(calibration.RmsPixels, 0, 0.01);
    }
}
