using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using PanTiltTracker.Core;
using PanTiltTracker.Vision;

namespace PanTiltTracker.Desktop.Services;

public enum InputSource { Simulation, Image, Video, Camera }

public sealed record SessionOptions(InputSource Source, string InputPath, int CameraIndex,
    int MarkerId, ControllerSettings Settings, string OutputDirectory,
    VideoAlgorithm Algorithm = VideoAlgorithm.Aruco, Rect? InitialRegion = null, CameraSettings? Camera = null,
    bool DetectFaces = false);

public sealed record SessionFrame(byte[] Pixels, int Width, int Height,
    TargetObservation Observation, TrackingCommand Command, TimeSpan Time, double AlgorithmMs,
    string VisionStatus = "aruco_detection", string? CaptureSummary = null, int TargetCount = 0);

public static class TrackingSession
{
    public static Task RunAsync(SessionOptions options, Func<SessionFrame, Task> present,
        Func<bool> hideTarget, CancellationToken cancellation) => Task.Run(async () =>
    {
        options.Settings.Validate();
        if (options.Algorithm != VideoAlgorithm.Aruco && options.Source != InputSource.Video)
            throw new ArgumentException("CSRT 和帧差实验目前只支持视频文件。");
        if (options.DetectFaces && (options.Source == InputSource.Simulation || options.Algorithm != VideoAlgorithm.Aruco))
            throw new ArgumentException("人脸检测适用于图片、摄像头或视频的目标检测模式。");
        using var detector = options.DetectFaces ? null : new ArucoTargetDetector(options.MarkerId);
        using var faceDetector = options.DetectFaces ? new FaceTargetDetector() : null;
        using var videoTracker = options.Algorithm == VideoAlgorithm.Aruco ? null
            : new VideoTargetTracker(options.Algorithm, options.InitialRegion);
        var controller = new PanTiltController(options.Settings);
        var mode = options.Source switch
        {
            InputSource.Simulation => "simulate", InputSource.Image => "image",
            InputSource.Video => "video", _ => "camera"
        };
        using var log = new RunLog(options.OutputDirectory, mode, options.MarkerId, options.Settings,
            options.DetectFaces ? "haar_face" : options.Algorithm == VideoAlgorithm.Aruco ? "aruco" : options.Algorithm.ToString(),
            new { input = options.InputPath, initialRegion = options.InitialRegion is { } r
                ? new { x = r.X, y = r.Y, width = r.Width, height = r.Height } : null,
                workingMaxWidth = options.DetectFaces ? FaceTargetDetector.WorkingMaxWidth : VideoTargetTracker.WorkingMaxWidth,
                faceSelection = options.DetectFaces ? "largest_per_frame; not identity tracking" : null });
        if (options.Source == InputSource.Camera)
        {
            await RunCameraAsync(options, detector, faceDetector, controller, log, present, cancellation);
            return;
        }

        if (options.Source == InputSource.Image)
        {
            using var image = Cv2.ImRead(options.InputPath, ImreadModes.Color);
            if (image.Empty()) throw new IOException("无法读取图片，请检查文件格式。");
            await ProcessAsync(image, 0, TimeSpan.Zero, TimeSpan.Zero, detector,
                controller, log, present, false, cancellation, faceDetector: faceDetector);
            return;
        }

        if (options.Source == InputSource.Video)
        {
            using var capture = new VideoCapture(options.InputPath);
            if (!capture.IsOpened()) throw new IOException("无法打开视频文件。");
            var fps = capture.Fps;
            if (!double.IsFinite(fps) || fps <= 0) throw new IOException("视频缺少有效帧率，暂无法按时间轴回放。");
            using var image = new Mat();
            var wallClock = Stopwatch.StartNew();
            long frame = 0;
            while (!cancellation.IsCancellationRequested && capture.Read(image) && !image.Empty())
            {
                var time = TimeSpan.FromSeconds(frame / fps);
                await ProcessAsync(image, frame++, time, time, detector, controller, log,
                    present, false, cancellation, videoTracker, faceDetector: faceDetector);
                await PaceAsync(wallClock, TimeSpan.FromSeconds(frame / fps), cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            return;
        }

        using var scene = new SyntheticScene(options.MarkerId);
        var command = controller.Step(null, TimeSpan.Zero);
        var simulationClock = Stopwatch.StartNew();
        long number = 0;
        while (!cancellation.IsCancellationRequested)
        {
            var time = TimeSpan.FromSeconds(number / 30.0);
            using var image = scene.Render(time.TotalSeconds, command.PanDegrees,
                command.TiltDegrees, hideTarget());
            command = await ProcessAsync(image, number++, time, time, detector, controller,
                log, present, false, cancellation);
            await PaceAsync(simulationClock, TimeSpan.FromSeconds(number / 30.0), cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
    }, cancellation);

    private static async Task RunCameraAsync(SessionOptions options, ArucoTargetDetector? detector, FaceTargetDetector? faceDetector,
        PanTiltController controller, RunLog log, Func<SessionFrame, Task> present, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var buffer = new LatestFrameBuffer();
        var settings = options.Camera ?? new CameraSettings();
        settings.Validate();
        string? captureSummary = null;
        var clock = Stopwatch.StartNew();
        var producer = Task.Run(() =>
        {
            using var capture = new VideoCapture(options.CameraIndex, settings.Backend);
            if (!capture.IsOpened()) throw new IOException("无法打开摄像头。请关闭微软相机/会议软件，检查设备编号、采集后端和 Windows 桌面应用相机权限。");
            capture.Set(VideoCaptureProperties.FrameWidth, settings.Width);
            capture.Set(VideoCaptureProperties.FrameHeight, settings.Height);
            capture.Set(VideoCaptureProperties.Fps, settings.Fps);
            var reportedFps = capture.Fps;
            var fourcc = (int)capture.Get(VideoCaptureProperties.FourCC);
            var format = new string(Enumerable.Range(0, 4).Select(i => (char)((fourcc >> (8 * i)) & 255)).ToArray()).Trim('\0');
            Volatile.Write(ref captureSummary, $"{settings.Backend} · 格式 {format} · 驱动报告 {reportedFps:0.##} fps（非实测）");
            long number = 0;
            while (!stop.IsCancellationRequested)
            {
                Mat? image = new();
                try
                {
                    if (!capture.Read(image) || image.Empty()) throw new IOException("摄像头未返回有效图像。");
                    if (number == 0)
                        File.WriteAllText(Path.Combine(options.OutputDirectory, "capture-info.json"), JsonSerializer.Serialize(new
                        {
                            index = options.CameraIndex, requested = settings, backend = settings.Backend.ToString(),
                            returnedWidth = image.Width, returnedHeight = image.Height,
                            driverReportedFps = double.IsFinite(reportedFps) ? (double?)reportedFps : null, format,
                            timestamp = "PC arrival, not exposure", hardwareOutput = "disabled"
                        }, new JsonSerializerOptions { WriteIndented = true }));
                    buffer.Publish(new(number++, clock.Elapsed, image));
                    image = null; // LatestFrameBuffer now owns the native allocation.
                }
                finally { image?.Dispose(); }
            }
        }, CancellationToken.None);
        var lastArrival = clock.Elapsed;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (producer.IsFaulted) await producer;
                using var frame = buffer.Take();
                if (frame is null)
                {
                    controller.Step(null, clock.Elapsed);
                    if ((clock.Elapsed - lastArrival).TotalSeconds > 5)
                        throw new TimeoutException("连续 5 秒未收到相机图像。");
                    await Task.Delay(10, cancellation);
                    continue;
                }
                lastArrival = clock.Elapsed;
                await ProcessAsync(frame.Image, frame.Number, frame.CapturedAt, clock.Elapsed,
                    detector, controller, log, present, true, cancellation, captureSummary: Volatile.Read(ref captureSummary),
                    faceDetector: faceDetector);
            }
        }
        finally
        {
            stop.Cancel();
            try { await producer.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException)
            {
                // Keep the UI out of a second overlapping camera session until process restart.
                throw new IOException("相机驱动仍阻塞读取，请重启应用后再连接相机。");
            }
        }
        cancellation.ThrowIfCancellationRequested();
    }

    private static async Task<TrackingCommand> ProcessAsync(Mat image, long number,
        TimeSpan capturedAt, TimeSpan time, ArucoTargetDetector? detector, PanTiltController controller,
        RunLog log, Func<SessionFrame, Task> present, bool realTime, CancellationToken cancellation,
        VideoTargetTracker? videoTracker = null, string? captureSummary = null, FaceTargetDetector? faceDetector = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var tracked = videoTracker?.Process(image, number, capturedAt);
        var faces = faceDetector?.Detect(image, number, capturedAt);
        var observation = faces?.Observation ?? tracked?.Observation ?? detector!.Detect(image, number, capturedAt);
        var status = faces is not null ? (observation.HasTarget ? "face_detected" : "no_face_detected")
            : tracked?.Status ?? "aruco_detection";
        if (realTime) time += watch.Elapsed;
        var command = controller.Step(observation, time);
        watch.Stop();
        log.Write(observation, command, time, watch.Elapsed.TotalMilliseconds, faces?.PrimaryBox ?? tracked?.Box,
            status, faces?.Boxes.Length ?? tracked?.CandidateCount);

        using var color = new Mat();
        switch (image.Channels())
        {
            case 1: Cv2.CvtColor(image, color, ColorConversionCodes.GRAY2BGR); break;
            case 3: image.CopyTo(color); break;
            default: Cv2.CvtColor(image, color, ColorConversionCodes.BGRA2BGR); break;
        }
        if (tracked is not null) VideoTargetTracker.DrawResult(color, tracked);
        if (faces is not null) FaceTargetDetector.DrawResult(color, faces);
        var center = new Point(image.Width / 2, image.Height / 2);
        Cv2.DrawMarker(color, center, new Scalar(120, 130, 140), MarkerTypes.Cross, 32, 1);
        if (observation.HasTarget)
        {
            var target = new Point((int)observation.CenterX!.Value, (int)observation.CenterY!.Value);
            Cv2.Line(color, center, target, new Scalar(150, 185, 180), 1, LineTypes.AntiAlias);
            Cv2.Circle(color, target, 10, new Scalar(105, 135, 15), 2, LineTypes.AntiAlias);
            Cv2.PutText(color, $"{(faces is not null ? "face" : tracked is null ? $"ID {detector!.TargetId}" : "target")}  ({target.X}, {target.Y})",
                new Point(Math.Clamp(target.X + 15, 5, Math.Max(5, image.Width - 210)),
                    Math.Clamp(target.Y - 18, 20, Math.Max(20, image.Height - 10))),
                HersheyFonts.HersheySimplex, 0.45, new Scalar(95, 105, 35), 1, LineTypes.AntiAlias);
        }
        using var bgra = new Mat();
        Cv2.CvtColor(color, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[bgra.Width * bgra.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        await present(new(pixels, bgra.Width, bgra.Height, observation, command, time,
            watch.Elapsed.TotalMilliseconds, status, captureSummary, faces?.Boxes.Length ?? 0));
        return command;
    }

    private static Task PaceAsync(Stopwatch clock, TimeSpan nextTime, CancellationToken token)
    {
        var wait = nextTime - clock.Elapsed;
        return wait > TimeSpan.Zero ? Task.Delay(wait, token) : Task.CompletedTask;
    }
}
