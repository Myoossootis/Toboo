using OpenCvSharp;
using System.Security.Cryptography;
using Toboo.Vision;
using Xunit;

namespace Toboo.Tests;

public sealed class FaceDetectionTests
{
    [Fact]
    public void ModelMatchesThePinnedUpstreamWeights()
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FaceTargetDetector.DefaultModelPath))).ToLowerInvariant();
        Assert.Equal("8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4", hash);
    }

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
        var path = Path.Combine(folder, "人脸模型.onnx");
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

    [Fact]
    public void BoxesMapBackWithoutMirroringAndClipToSourceBounds()
    {
        var box = new Rect(60, 100, 80, 100);
        Assert.Equal(new Rect(120, 200, 160, 200), FaceTargetDetector.MapToSource(box, new(640, 360), new(1280, 720)));
        Assert.Equal(new Rect(0, 0, 1280, 720), FaceTargetDetector.MapToSource(new(-20, -10, 800, 500), new(640, 360), new(1280, 720)));
    }

    [Fact]
    public void DuplicateViewsMergeButAdjacentPeopleRemainSeparate()
    {
        var largest = new Rect(10, 20, 100, 100);
        var duplicate = new Rect(15, 25, 95, 95);
        var neighbor = new Rect(90, 20, 100, 100);
        var boxes = FaceTargetDetector.SelectDetections([duplicate, neighbor, largest, largest], [0.9f, 0.85f, 0.99f, 0.95f]);
        Assert.Equal(new[] { largest, neighbor }, boxes);
    }
}
