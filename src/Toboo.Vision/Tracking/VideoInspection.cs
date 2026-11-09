using OpenCvSharp;

namespace Toboo.Vision;

public sealed record VideoMetadata(string File, int Width, int Height, double ReportedFps,
    long ReportedFrames, double DurationSeconds, string Codec);

public static class VideoInspection
{
    public static VideoMetadata Prepare1080p(string input, string output)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must be different from the original video.");
        var metadata = Read(input);
        if (metadata.Width < 1920 || metadata.Height < 1080 ||
            Math.Abs(metadata.Width / (double)metadata.Height - 16.0 / 9) > 0.001 ||
            !double.IsFinite(metadata.ReportedFps) || metadata.ReportedFps <= 0)
            throw new ArgumentException("Use a readable 16:9 source at least 1920x1080 with a valid FPS.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var capture = new VideoCapture(input);
        using var writer = new VideoWriter(output, FourCC.FromString("mp4v"), metadata.ReportedFps, new Size(1920, 1080));
        if (!writer.IsOpened()) throw new IOException("MP4V writer is unavailable.");
        using var original = new Mat();
        using var resized = new Mat();
        while (capture.Read(original) && !original.Empty())
        {
            Cv2.Resize(original, resized, new Size(1920, 1080), 0, 0, InterpolationFlags.Area);
            writer.Write(resized);
        }
        writer.Release();
        var prepared = Read(output);
        if (prepared.ReportedFrames != metadata.ReportedFrames ||
            Math.Abs(prepared.ReportedFps - metadata.ReportedFps) > 0.001)
            throw new IOException("Prepared video does not preserve the source frame count and nominal FPS.");
        return prepared;
    }

    public static VideoMetadata Read(string path)
    {
        using var capture = new VideoCapture(path);
        if (!capture.IsOpened()) throw new IOException("Cannot open video: " + path);
        using var first = new Mat();
        if (!capture.Read(first) || first.Empty()) throw new IOException("Video has no readable frame.");
        var fps = capture.Fps;
        var count = (long)capture.FrameCount;
        var fourcc = (int)capture.Get(VideoCaptureProperties.FourCC);
        var codec = new string(Enumerable.Range(0, 4).Select(shift => (char)((fourcc >> (shift * 8)) & 255)).ToArray());
        return new(Path.GetFullPath(path), first.Width, first.Height, fps, count,
            fps > 0 ? count / fps : 0, codec);
    }

    public static void SaveSamples(string path, string directory)
    {
        Directory.CreateDirectory(directory);
        using var capture = new VideoCapture(path);
        if (!capture.IsOpened()) throw new IOException("Cannot open video: " + path);
        var count = Math.Max(1, (long)capture.FrameCount);
        foreach (var frame in new long[] { 0, count / 4, count / 2, count * 3 / 4 }.Distinct())
        {
            capture.Set(VideoCaptureProperties.PosFrames, frame);
            using var image = new Mat();
            if (capture.Read(image) && !image.Empty())
                image.SaveImage(Path.Combine(directory, $"frame-{frame:D5}.png"));
        }
    }
}
