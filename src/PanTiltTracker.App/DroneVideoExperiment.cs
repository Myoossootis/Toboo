using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using PanTiltTracker.Core;
using PanTiltTracker.Vision;

namespace PanTiltTracker.App;

internal static class DroneVideoExperiment
{
    public static void Run(string input, string directory, VideoAlgorithm algorithm, Rect? region,
        ControllerSettings settings, int maxFrames, CancellationToken cancellation)
    {
        var annotatedPath = Path.GetFullPath(Path.Combine(directory, "annotated.mp4"));
        if (string.Equals(Path.GetFullPath(input), annotatedPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output would overwrite the input video. Choose a different --out directory.");
        using var capture = new VideoCapture(input);
        if (!capture.IsOpened()) throw new IOException("Could not open video.");
        var fps = capture.Fps;
        if (!double.IsFinite(fps) || fps <= 0) throw new IOException("Video has no valid nominal FPS.");
        using var tracker = new VideoTargetTracker(algorithm, region);
        using var log = new RunLog(directory, "video", 7, settings, algorithm.ToString(), new
        {
            input = Path.GetFullPath(input), initialRegion = RegionData(region),
            workingMaxWidth = VideoTargetTracker.WorkingMaxWidth, nominalFps = fps,
            interpretation = "Recorded video, no feedback. Reported boxes are not ground truth or calibrated angles."
        });
        using var image = new Mat();
        using var writer = new VideoWriter();
        var controller = new PanTiltController(settings);
        var times = new List<double>();
        var count = 0;
        var valid = 0;
        var sampleStep = Math.Max(1, (int)Math.Min(maxFrames, capture.FrameCount) / 4);
        while (count < maxFrames && !cancellation.IsCancellationRequested && capture.Read(image) && !image.Empty())
        {
            var time = TimeSpan.FromSeconds(count / fps);
            var watch = Stopwatch.StartNew();
            var result = tracker.Process(image, count, time);
            watch.Stop();
            var command = controller.Step(result.Observation, time);
            log.Write(result.Observation, command, time, watch.Elapsed.TotalMilliseconds,
                result.Box, result.Status, result.CandidateCount);
            times.Add(watch.Elapsed.TotalMilliseconds);
            if (result.Observation.HasTarget) valid++;
            using var overlay = image.Clone();
            VideoTargetTracker.DrawResult(overlay, result);
            if (!writer.IsOpened())
            {
                writer.Open(annotatedPath, FourCC.FromString("mp4v"), fps, overlay.Size());
                if (!writer.IsOpened()) throw new IOException("Could not create annotated video.");
            }
            writer.Write(overlay);
            if (count % sampleStep == 0 || count == Math.Min(maxFrames, (int)capture.FrameCount) - 1)
            {
                overlay.SaveImage(Path.Combine(directory, $"frame-{count:D5}.png"));
                if (!tracker.MotionMask.Empty()) tracker.MotionMask.SaveImage(Path.Combine(directory, $"mask-{count:D5}.png"));
            }
            count++;
        }
        cancellation.ThrowIfCancellationRequested();
        if (count == 0) throw new IOException("Video returned no decodable frames.");
        times.Sort();
        var summary = new
        {
            frames = count, reportedTargetFrames = valid,
            meanAlgorithmMs = times.Count == 0 ? 0 : times.Average(),
            p95AlgorithmMs = times.Count == 0 ? 0 : times[(int)Math.Ceiling(times.Count * 0.95) - 1],
            accuracyMeasured = false,
            note = "A reported target may drift or be a non-drone motion candidate. First CSRT box is manual initialization."
        };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "summary.json"), json);
        Console.WriteLine(json);
    }

    public static object? RegionData(Rect? box) => box is { } r
        ? new { x = r.X, y = r.Y, width = r.Width, height = r.Height } : null;
}
