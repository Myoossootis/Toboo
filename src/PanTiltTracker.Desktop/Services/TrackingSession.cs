using System.Diagnostics;
using OpenCvSharp;
using PanTiltTracker.Core;
using PanTiltTracker.Vision;

namespace PanTiltTracker.Desktop.Services;

public enum InputSource { Simulation, Image, Video, Camera }

public sealed record SessionOptions(InputSource Source, string InputPath, int CameraIndex,
    int MarkerId, ControllerSettings Settings, string OutputDirectory);

public sealed record SessionFrame(byte[] Pixels, int Width, int Height,
    TargetObservation Observation, TrackingCommand Command, TimeSpan Time, double AlgorithmMs);

public static class TrackingSession
{
    public static Task RunAsync(SessionOptions options, Func<SessionFrame, Task> present,
        Func<bool> hideTarget, CancellationToken cancellation) => Task.Run(async () =>
    {
        options.Settings.Validate();
        using var detector = new ArucoTargetDetector(options.MarkerId);
        var controller = new PanTiltController(options.Settings);
        var mode = options.Source switch
        {
            InputSource.Simulation => "simulate", InputSource.Image => "image",
            InputSource.Video => "video", _ => "camera"
        };
        using var log = new RunLog(options.OutputDirectory, mode, options.MarkerId, options.Settings);
        if (options.Source == InputSource.Camera)
        {
            await RunCameraAsync(options, detector, controller, log, present, cancellation);
            return;
        }

        if (options.Source == InputSource.Image)
        {
            using var image = Cv2.ImRead(options.InputPath, ImreadModes.Color);
            if (image.Empty()) throw new IOException("无法读取图片，请检查文件格式。");
            await ProcessAsync(image, 0, TimeSpan.Zero, TimeSpan.Zero, detector,
                controller, log, present, false, cancellation);
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
                    present, false, cancellation);
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

    private static async Task RunCameraAsync(SessionOptions options, ArucoTargetDetector detector,
        PanTiltController controller, RunLog log, Func<SessionFrame, Task> present, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var buffer = new LatestFrameBuffer();
        var clock = Stopwatch.StartNew();
        var producer = Task.Run(() =>
        {
            using var capture = new VideoCapture(options.CameraIndex, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened()) throw new IOException("无法打开 USB 摄像头，请核对设备编号及占用情况。");
            capture.Set(VideoCaptureProperties.FrameWidth, 640);
            capture.Set(VideoCaptureProperties.FrameHeight, 480);
            capture.Set(VideoCaptureProperties.Fps, 30);
            long number = 0;
            while (!stop.IsCancellationRequested)
            {
                var image = new Mat();
                if (!capture.Read(image) || image.Empty())
                {
                    image.Dispose();
                    throw new IOException("摄像头未返回有效图像。");
                }
                buffer.Publish(new(number++, clock.Elapsed, image));
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
                    detector, controller, log, present, true, cancellation);
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
        TimeSpan capturedAt, TimeSpan time, ArucoTargetDetector detector, PanTiltController controller,
        RunLog log, Func<SessionFrame, Task> present, bool realTime, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var observation = detector.Detect(image, number, capturedAt);
        if (realTime) time += watch.Elapsed;
        var command = controller.Step(observation, time);
        watch.Stop();
        log.Write(observation, command, time, watch.Elapsed.TotalMilliseconds);

        using var color = new Mat();
        switch (image.Channels())
        {
            case 1: Cv2.CvtColor(image, color, ColorConversionCodes.GRAY2BGR); break;
            case 3: image.CopyTo(color); break;
            default: Cv2.CvtColor(image, color, ColorConversionCodes.BGRA2BGR); break;
        }
        var center = new Point(image.Width / 2, image.Height / 2);
        Cv2.DrawMarker(color, center, new Scalar(120, 130, 140), MarkerTypes.Cross, 32, 1);
        if (observation.HasTarget)
        {
            var target = new Point((int)observation.CenterX!.Value, (int)observation.CenterY!.Value);
            Cv2.Line(color, center, target, new Scalar(150, 185, 180), 1, LineTypes.AntiAlias);
            Cv2.Circle(color, target, 10, new Scalar(105, 135, 15), 2, LineTypes.AntiAlias);
            Cv2.PutText(color, $"ID {detector.TargetId}  ({target.X}, {target.Y})",
                new Point(Math.Clamp(target.X + 15, 5, Math.Max(5, image.Width - 210)),
                    Math.Clamp(target.Y - 18, 20, Math.Max(20, image.Height - 10))),
                HersheyFonts.HersheySimplex, 0.45, new Scalar(95, 105, 35), 1, LineTypes.AntiAlias);
        }
        using var bgra = new Mat();
        Cv2.CvtColor(color, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[bgra.Width * bgra.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        await present(new(pixels, bgra.Width, bgra.Height, observation, command, time, watch.Elapsed.TotalMilliseconds));
        return command;
    }

    private static Task PaceAsync(Stopwatch clock, TimeSpan nextTime, CancellationToken token)
    {
        var wait = nextTime - clock.Elapsed;
        return wait > TimeSpan.Zero ? Task.Delay(wait, token) : Task.CompletedTask;
    }
}
