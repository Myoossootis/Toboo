using OpenCvSharp;
using PanTiltTracker.Vision;
using Xunit;

namespace PanTiltTracker.Tests;

public sealed class VideoInspectionTests
{
    [Fact]
    public void PreparationPreservesFrameCountAndFpsWithoutChangingOriginal()
    {
        var directory = NewDirectory();
        var source = Path.Combine(directory, "source.avi");
        WriteVideo(source, new Size(2560, 1440), 25);
        var originalHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source));
        var result = VideoInspection.Prepare1080p(source, Path.Combine(directory, "prepared.mp4"));
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal(25, result.ReportedFps, 3);
        Assert.Equal(3, result.ReportedFrames);
        Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void PreparationRejectsUpscaling()
    {
        var directory = NewDirectory();
        var source = Path.Combine(directory, "small.avi");
        WriteVideo(source, new Size(640, 360), 30);
        Assert.Throws<ArgumentException>(() => VideoInspection.Prepare1080p(source, Path.Combine(directory, "upscaled.mp4")));
    }

    [Fact]
    public void PreparationCannotOverwriteTheOriginal() =>
        Assert.Throws<ArgumentException>(() => VideoInspection.Prepare1080p("same.mp4", "same.mp4"));

    private static string NewDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestArtifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteVideo(string path, Size size, double fps)
    {
        using var writer = new VideoWriter(path, FourCC.MJPG, fps, size);
        Assert.True(writer.IsOpened());
        using var image = new Mat(size, MatType.CV_8UC3, new Scalar(180, 190, 200));
        for (var frame = 0; frame < 3; frame++) writer.Write(image);
    }
}
