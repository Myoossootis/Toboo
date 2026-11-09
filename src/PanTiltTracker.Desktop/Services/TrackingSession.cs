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
    bool DetectFaces = false,
    FaceAimSettings? FaceAim = null, AimReference? FaceAimReference = null, bool DetectExpressions = false);

public sealed record SessionFrame(byte[] Pixels, int Width, int Height,
    TargetObservation Observation, TrackingCommand Command, TimeSpan Time, double AlgorithmMs,
    string VisionStatus = "aruco_detection", string? CaptureSummary = null, int TargetCount = 0,
    double? TargetScore = null, FaceAimResult? FaceAim = null, string? OutputNotice = null, bool OutputSuspended = false,
    ExpressionAnalysis? Expression = null);

public static class TrackingSession
{
    public static Task RunAsync(SessionOptions options, Func<SessionFrame, Task> present,
        Func<bool> hideTarget, CancellationToken cancellation,
        Func<FaceAimResult, TimeSpan, CancellationToken, Task<FollowOutputFeedback>>? followOutput = null) => Task.Run(async () =>
    {
        options.Settings.Validate();
        if (followOutput is not null && (options.Source != InputSource.Camera || !options.DetectFaces ||
            options.FaceAimReference is null || options.FaceAim?.Camera is null || !options.FaceAim.DirectionsConfirmed))
            throw new ArgumentException("实物跟随只允许已配置相机、方向和请求参考的 USB 摄像头人脸模式。");
        if (options.Algorithm != VideoAlgorithm.Aruco && options.Source != InputSource.Video)
            throw new ArgumentException("CSRT 和帧差实验目前只支持视频文件。");
        if (options.DetectFaces && (options.Source == InputSource.Simulation || options.Algorithm != VideoAlgorithm.Aruco))
            throw new ArgumentException("人脸检测适用于图片、摄像头或视频的目标检测模式。");
        using var detector = options.DetectFaces ? null : new ArucoTargetDetector(options.MarkerId);
        using var faceDetector = options.DetectFaces ? new FaceTargetDetector() : null;
        var faceAimSettings = options.FaceAim ?? (options.DetectFaces ? LoadFaceAimSettings() : new FaceAimSettings());
        var faceAim = options.DetectFaces && options.Source is InputSource.Camera or InputSource.Video
            ? new FaceAimController(faceAimSettings, options.FaceAimReference) : null;
        using var videoTracker = options.Algorithm == VideoAlgorithm.Aruco ? null
            : new VideoTargetTracker(options.Algorithm, options.InitialRegion);
        var controller = new PanTiltController(options.Settings);
        var mode = options.Source switch
        {
            InputSource.Simulation => "simulate", InputSource.Image => "image",
            InputSource.Video => "video", _ => "camera"
        };
        using var log = new RunLog(options.OutputDirectory, mode, options.MarkerId, options.Settings,
            options.DetectFaces ? "yunet_face" : options.Algorithm == VideoAlgorithm.Aruco ? "aruco" : options.Algorithm.ToString(),
            new { input = options.InputPath, initialRegion = options.InitialRegion is { } r
                ? new { x = r.X, y = r.Y, width = r.Width, height = r.Height } : null,
                workingMaxWidth = options.DetectFaces ? FaceTargetDetector.WorkingMaxWidth : VideoTargetTracker.WorkingMaxWidth,
                faceSelection = options.DetectFaces ? (faceAim is null ? "largest_per_frame; not identity tracking"
                    : "confirmed spatial lock; not identity recognition") : null,
                faceAim = faceAim is null ? null : new { settings = faceAimSettings,
                    output = followOutput is null ? "preview_only; no transport" : "explicit live-camera follow; UART requests, not measured position",
                    requestReference = options.FaceAimReference,
                    angles = "camera sight error, not measured shaft angle" },
                faceViews = options.DetectFaces ? "YuNet 2023mar; frontal and profile; score 0.8; IoU NMS 0.3" : null },
            hardwareOutput: followOutput is null ? "disabled" : "face_follow_requests");
        using var expression = options.DetectFaces && options.DetectExpressions
            ? new ExpressionSession(options.OutputDirectory, options.Source == InputSource.Image) : null;
        if (expression is not null)
            File.WriteAllText(Path.Combine(options.OutputDirectory, "expression-settings.json"), JsonSerializer.Serialize(new {
                model = "OpenCV Zoo Progressive Teacher / MobileFaceNet", sha256 = FacialExpressionRecognizer.ModelSha256,
                settings = new ExpressionSettings(), intervalSeconds = FaceExpressionSession.IntervalSeconds,
                minimumFacePixels = 64, maximumInputAgeSeconds = 0.2, minimumBrightness = 25, maximumBrightness = 235,
                minimumLaplacianVariance = 35, source = mode, temporalConfirmation = options.Source != InputSource.Image,
                hardwareOutput = "disabled", thresholds = "preliminary, not calibrated", interpretation = "visible expression cues only" }));
        if (options.Source == InputSource.Camera)
        {
            await RunCameraAsync(options, detector, faceDetector, controller, log, present, cancellation, faceAim, faceAimSettings, followOutput, expression);
            return;
        }

        if (options.Source == InputSource.Image)
        {
            using var image = Cv2.ImRead(options.InputPath, ImreadModes.Color);
            if (image.Empty()) throw new IOException("无法读取图片，请检查文件格式。");
            await ProcessAsync(image, 0, TimeSpan.Zero, TimeSpan.Zero, detector,
                controller, log, present, false, cancellation, faceDetector: faceDetector, expression: expression);
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
                    present, false, cancellation, videoTracker, faceDetector: faceDetector, faceAim: faceAim, faceAimSettings: faceAimSettings, expression: expression);
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
        PanTiltController controller, RunLog log, Func<SessionFrame, Task> present, CancellationToken cancellation,
        FaceAimController? faceAim = null,
        FaceAimSettings? faceAimSettings = null,
        Func<FaceAimResult, TimeSpan, CancellationToken, Task<FollowOutputFeedback>>? followOutput = null,
        ExpressionSession? expression = null)
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
                            timestamp = "PC arrival, not exposure", hardwareOutput = followOutput is null ? "disabled" : "face_follow_requests"
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
                    faceDetector: faceDetector, faceAim: faceAim, faceAimSettings: faceAimSettings, followOutput: followOutput, expression: expression);
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
        VideoTargetTracker? videoTracker = null, string? captureSummary = null, FaceTargetDetector? faceDetector = null,
        FaceAimController? faceAim = null,
        FaceAimSettings? faceAimSettings = null,
        Func<FaceAimResult, TimeSpan, CancellationToken, Task<FollowOutputFeedback>>? followOutput = null,
        ExpressionSession? expression = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var frameWatch = Stopwatch.StartNew();
        var frameStartTime = time;
        var watch = Stopwatch.StartNew();
        var tracked = videoTracker?.Process(image, number, capturedAt);
        var faces = faceDetector?.Detect(image, number, capturedAt);
        var observation = faces?.Observation ?? tracked?.Observation ?? detector!.Detect(image, number, capturedAt);
        var status = faces is not null ? (observation.HasTarget ? "face_detected" : "no_face_detected")
            : tracked?.Status ?? "aruco_detection";
        if (realTime) time += watch.Elapsed;
        var aim = faces is not null && faceAim is not null ? faceAim.Step(
            faces.Boxes.Select(b => new FaceRegion(b.X, b.Y, b.Width, b.Height)).ToArray(),
            number, capturedAt, image.Width, image.Height, time) : null;
        watch.Stop(); // Algorithm timing excludes the separate serial request/acknowledgement round trip.
        FollowOutputFeedback? outputFeedback = null;
        if (aim is not null)
        {
            if (followOutput is not null)
            {
                outputFeedback = await followOutput(aim, time - capturedAt, cancellation);
                aim = FaceFollowOutputGuard.ApplyFeedback(faceAim!, aim, outputFeedback);
            }
            observation = aim.Observation; status = aim.Status;
        }
        var command = aim?.Command ?? controller.Step(observation, time);
        var faceBox = aim?.SelectedBox is { } selected ? new Rect((int)selected.X, (int)selected.Y,
            (int)selected.Width, (int)selected.Height) : (Rect?)null;
        // Optional expression inference happens after the existing control request/acknowledgement.
        // It does not feed angles, target selection, arming or motion permission.
        var expressionNow = realTime ? frameStartTime + frameWatch.Elapsed : time;
        var expressionResult = faces is not null ? expression?.Analyze(image, faces, aim, expressionNow, realTime) : null;
        log.Write(observation, command, time, watch.Elapsed.TotalMilliseconds, (aim is null ? faces?.PrimaryBox : faceBox) ?? tracked?.Box,
            outputFeedback?.Suspended == true ? status + ";servo_output_suspended" : status, faces?.Boxes.Length ?? tracked?.CandidateCount,
            faceAim: aim);

        using var color = new Mat();
        switch (image.Channels())
        {
            case 1: Cv2.CvtColor(image, color, ColorConversionCodes.GRAY2BGR); break;
            case 3: image.CopyTo(color); break;
            default: Cv2.CvtColor(image, color, ColorConversionCodes.BGRA2BGR); break;
        }
        if (tracked is not null) VideoTargetTracker.DrawResult(color, tracked);
        if (faces is not null) FaceTargetDetector.DrawResult(color, faces, aim);
        if (faces is not null && faceAimSettings is not null) FaceAimOverlay.Draw(color, faceAimSettings, aim);
        if (expressionResult is { } cue)
            Cv2.PutText(color, $"Expression: {cue.Estimate.Label} | {cue.Estimate.Reason}",
                new(10, Math.Max(18, image.Height - 12)), HersheyFonts.HersheySimplex, 0.45, new Scalar(255, 220, 80), 1, LineTypes.AntiAlias);
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
        await present(new(pixels, bgra.Width, bgra.Height, observation, command, realTime ? frameStartTime + frameWatch.Elapsed : time,
            watch.Elapsed.TotalMilliseconds, status, captureSummary, faces?.Boxes.Length ?? 0,
            null, aim, outputFeedback?.Notice, outputFeedback?.Suspended == true, expressionResult));
        return command;
    }

    private static Task PaceAsync(Stopwatch clock, TimeSpan nextTime, CancellationToken token)
    {
        var wait = nextTime - clock.Elapsed;
        return wait > TimeSpan.Zero ? Task.Delay(wait, token) : Task.CompletedTask;
    }

    private static FaceAimSettings LoadFaceAimSettings()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PanTiltTracker", "face-aim.local.json");
        var path = File.Exists(local) ? local : Path.Combine(AppContext.BaseDirectory, "face-aim.json");
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<FaceAimSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("人脸对准配置不能为空。");
        settings.Validate();
        return settings;
    }
}
