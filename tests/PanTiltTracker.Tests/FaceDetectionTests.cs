using OpenCvSharp;
using PanTiltTracker.Vision;
using Xunit;

namespace PanTiltTracker.Tests;

public sealed class FaceDetectionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void NativeModelLoadsAndBlankFramesHaveNoFaceWithoutModifyingInput(int channels)
    {
        using var detector = new FaceTargetDetector();
        using var image = new Mat(720, 1280, MatType.CV_8UC(channels), Scalar.All(70));
        using var original = image.Clone();
        var result = detector.Detect(image, 17, TimeSpan.FromSeconds(2));
        Assert.Empty(result.Boxes);
        Assert.Null(result.PrimaryBox);
        Assert.False(result.Observation.HasTarget);
        Assert.Equal(17, result.Observation.FrameNumber);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Observation.CapturedAt);
        Assert.Equal(1280, result.Observation.Width);
        Assert.Equal(720, result.Observation.Height);
        Assert.Equal(0, Cv2.Norm(image, original));
    }

    [Fact]
    public void ModelCanBeLoadedFromAChinesePortableDirectory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UAVTrack-face-tests", "中文目录", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "人脸模型.xml");
        File.Copy(FaceTargetDetector.DefaultModelPath, path);
        using var detector = new FaceTargetDetector(path);
        using var blank = new Mat(240, 320, MatType.CV_8UC3, Scalar.Black);
        Assert.Empty(detector.Detect(blank, 0, TimeSpan.Zero).Boxes);
    }

    [Fact]
    public void MissingModelAndEmptyImagesFailClearly()
    {
        Assert.Throws<FileNotFoundException>(() => new FaceTargetDetector("missing-face-model.xml"));
        using var detector = new FaceTargetDetector();
        using var image = new Mat();
        Assert.Throws<ArgumentException>(() => detector.Detect(image, 0, TimeSpan.Zero));
    }
}
