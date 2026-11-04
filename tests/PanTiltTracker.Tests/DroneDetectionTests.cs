using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using PanTiltTracker.Vision;
using Xunit;

namespace PanTiltTracker.Tests;

public sealed class DroneDetectionTests
{
    [DroneModelFact]
    public void VerifiedModelLoadsFromUnicodePathAndBlankFramesStayEmptyInAllFormats()
    {
        var source = Environment.GetEnvironmentVariable("UAVTRACK_DRONE_MODEL")!;
        var folder = Path.Combine(Path.GetTempPath(), "UAVTrack-无人机模型", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, "无人机.onnx");
        File.Copy(source, copy);
        using var detector = new DroneTargetDetector(copy);
        Assert.Equal(416, detector.InputWidth); Assert.Equal(416, detector.InputHeight);
        foreach (var channels in new[] { 1, 3, 4 })
        {
            using var frame = new Mat(241, 321, MatType.CV_8UC(channels), Scalar.Black);
            using var before = frame.Clone();
            var result = detector.Detect(frame, channels, TimeSpan.FromSeconds(channels));
            Assert.False(result.Observation.HasTarget);
            Assert.Null(result.PrimaryBox); Assert.Null(result.PrimaryScore);
            Assert.Empty(result.Detections);
            Assert.Equal(channels, result.Observation.FrameNumber);
            Assert.Equal(0, Cv2.Norm(frame, before));
        }
        detector.Dispose();
        using var image = new Mat(20,20,MatType.CV_8UC3);
        Assert.Throws<ObjectDisposedException>(() => detector.Detect(image, 4, TimeSpan.Zero));
    }

    [Fact]
    public void CpuRuntimeLoadsWithoutOptionalWeights()
    {
        Assert.False(string.IsNullOrWhiteSpace(OrtEnv.Instance().GetVersionString()));
    }

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(4)]
    public void LetterboxPreservesInputAndPacksNormalizedRgbPlanes(int channels)
    {
        using var image = new Mat(2, 4, MatType.CV_8UC(channels), new Scalar(10, 20, 30, 255));
        using var before = image.Clone();
        var tensor = new float[3 * 8 * 8];
        var map = DroneTargetDetector.PrepareInput(image, new(8, 8), tensor);
        Assert.Equal(2, map.Top); Assert.Equal(0, map.Left);
        Assert.Equal(new Size(8, 4), map.Resized);
        Assert.Equal(114f / 255, tensor[0]);
        Assert.Equal((channels == 1 ? 10f : 30f) / 255, tensor[2 * 8]);
        Assert.Equal((channels == 1 ? 10f : 20f) / 255, tensor[64 + 2 * 8]);
        Assert.Equal(10f / 255, tensor[128 + 2 * 8]);
        Assert.Equal(0, Cv2.Norm(image, before));
    }

    [Fact]
    public void MappingRemovesPaddingScalesEachAxisAndDoesNotMirror()
    {
        var map = new DroneTargetDetector.Letterbox(new(1920, 1080), new(416, 234), 0, 91);
        Assert.Equal(new Rect(480, 270, 960, 540), map.Map(208, 208, 208, 117));
        Assert.Null(map.Map(208, 20, 40, 20)); // box entirely in top padding
        Assert.Equal(new Rect(0, 0, 1920, 1080), map.Map(208, 208, 1000, 1000));
        Assert.Null(map.Map(float.NaN, 208, 20, 20));
        Assert.Null(map.Map(208, 208, -1, 20));
    }

    [Fact]
    public void DecodeMultipliesScoresDeduplicatesAndKeepsSeparateDronesInScoreOrder()
    {
        var map = new DroneTargetDetector.Letterbox(new(200, 100), new(200, 100), 0, 0);
        float[] rows = [50,50,40,40,0.9f,0.8f, 51,50,40,40,0.8f,0.8f,
            150,50,20,20,0.95f,0.95f, 90,50,20,20,0.5f,0.5f];
        var detections = DroneTargetDetector.Decode(rows, [1,4,6], map);
        Assert.Equal(2, detections.Length);
        Assert.Equal(new Rect(140,40,20,20), detections[0].Box);
        Assert.Equal(0.95f * 0.95f, detections[0].Score);
        Assert.Equal(new Rect(30,30,40,40), detections[1].Box);
        Assert.Equal(0.9f * 0.8f, detections[1].Score);
    }

    [Fact]
    public void Yolo11UsesClassScoresWithoutObjectnessAndRejectsBirds()
    {
        var map = new DroneTargetDetector.Letterbox(new(200,100),new(200,100),0,0);
        float[] channels = [50,150, 50,50, 40,20, 40,20, .8f,.7f, .1f,.9f];
        var boxes = DroneTargetDetector.Decode(channels,[1,6,2],map,true);
        Assert.Single(boxes); Assert.Equal(new Rect(30,30,40,40),boxes[0].Box);
        Assert.Equal(.8f,boxes[0].Score);
        Assert.Throws<InvalidDataException>(()=>DroneTargetDetector.Decode(channels,[1,2,6],map,true));
    }

    [Fact]
    public void BadPredictionsAndNoCandidatesDoNotInventCoordinates()
    {
        var map = new DroneTargetDetector.Letterbox(new(100,100), new(100,100), 0, 0);
        float[] rows = [50,50,20,20,float.NaN,1, 50,50,20,20,1,2,
            50,50,0,20,1,1, 50,50,20,20,0.1f,1];
        Assert.Empty(DroneTargetDetector.Decode(rows, [1,4,6], map));
        Assert.Throws<InvalidDataException>(() => DroneTargetDetector.Decode(rows, [1,6,4], map));
        Assert.Throws<InvalidDataException>(() => DroneTargetDetector.Decode(rows, [1,4,85], map));
    }

    [Fact]
    public void InvalidInputsAndUnverifiedModelsAreRejectedBeforeInference()
    {
        using var empty = new Mat();
        Assert.Throws<ArgumentException>(() => DroneTargetDetector.PrepareInput(empty, new(8,8), new float[192]));
        using var unsupported = new Mat(8,8,MatType.CV_32FC3);
        Assert.Throws<ArgumentException>(() => DroneTargetDetector.PrepareInput(unsupported, new(8,8), new float[192]));
        using var image = new Mat(8,8,MatType.CV_8UC3);
        Assert.Throws<ArgumentException>(() => DroneTargetDetector.PrepareInput(image, new(8,8), []));
        Assert.Throws<FileNotFoundException>(() => new DroneTargetDetector(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx")));
        var folder = Path.Combine(Path.GetTempPath(), "UAVTrack-drone-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var fake = Path.Combine(folder, "错误模型.onnx");
        File.WriteAllText(fake, "not the verified model");
        Assert.Throws<ArgumentException>(() => new DroneTargetDetector(fake));
    }
}

public sealed class DroneModelFactAttribute : FactAttribute
{
    public DroneModelFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UAVTRACK_DRONE_MODEL")))
            Skip = "Optional third-party weights: set UAVTRACK_DRONE_MODEL to the verified local best.onnx.";
    }
}
