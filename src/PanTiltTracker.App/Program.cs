using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using PanTiltTracker.Core;
using PanTiltTracker.Vision;

namespace PanTiltTracker.App;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var options = Options.Parse(args);
            if (options.Mode == "evaluate-drone")
            {
                var fps = double.Parse(options.Required("fps"), CultureInfo.InvariantCulture);
                DroneEvaluation.Run(options.Required("reference"), options.Required("telemetry"), fps,
                    options.Text("out", "artifacts/drone-evaluation"));
                return 0;
            }
            if (options.Mode is "inspect" or "prepare-video")
            {
                var input = options.Required("input");
                var inspectedPath = options.Mode == "prepare-video" ? options.Required("out") : input;
                var metadata = options.Mode == "prepare-video"
                    ? VideoInspection.Prepare1080p(input, inspectedPath) : VideoInspection.Read(input);
                var directory = options.Mode == "prepare-video"
                    ? Path.ChangeExtension(inspectedPath, null) + "-inspection"
                    : options.Text("out", "artifacts/video-inspection");
                VideoInspection.SaveSamples(inspectedPath, directory);
                var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(directory, "metadata.json"), json);
                Console.WriteLine(json);
                return 0;
            }
            if (options.Mode == "marker")
            {
                var path = options.Text("out", $"artifacts/marker-{options.Id}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var marker = ArucoTargetDetector.CreateMarker(options.Id, options.Integer("size", 600));
                if (!Cv2.ImWrite(path, marker)) throw new IOException("Could not save marker.");
                Console.WriteLine($"Saved DICT_4X4_50 / ID {options.Id}: {Path.GetFullPath(path)}");
                Console.WriteLine("Print without stretching; measure the BLACK square, excluding the white margin.");
                return 0;
            }

            var configPath = options.Text("config", Path.Combine(AppContext.BaseDirectory, "tracker.json"));
            var settings = JsonSerializer.Deserialize<ControllerSettings>(File.ReadAllText(configPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling =
                    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new ArgumentException("Configuration is empty.");
            settings.Validate();
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                if (options.Mode == "detect-drone")
                {
                    DroneVideoExperiment.Run(options.Required("input"), options.Text("out", "artifacts/drone-detection"),
                        VideoAlgorithm.Aruco, null, settings, options.Integer("frames", int.MaxValue), cancellation.Token,
                        options.Required("model"), options.Has("tracking"));
                    return 0;
                }
                if (options.Mode == "track-video")
                {
                    var algorithm = options.Text("algorithm", "csrt") switch
                    {
                        "csrt" => VideoAlgorithm.Csrt, "frame-diff" => VideoAlgorithm.FrameDifference,
                        _ => throw new ArgumentException("--algorithm must be csrt or frame-diff.")
                    };
                    Rect? region = null;
                    if (options.Has("roi"))
                    {
                        var parts = options.Required("roi").Split(',');
                        if (parts.Length != 4 || parts.Any(p => !int.TryParse(p, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out _))) throw new ArgumentException("--roi expects x,y,width,height.");
                        var values = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                        region = new(values[0], values[1], values[2], values[3]);
                    }
                    DroneVideoExperiment.Run(options.Required("input"), options.Text("out", "artifacts/drone-csrt"),
                        algorithm, region, settings, options.Integer("frames", int.MaxValue), cancellation.Token);
                    return 0;
                }
                using var detector = new ArucoTargetDetector(options.Id);
                var controller = new PanTiltController(settings);
                var directory = options.Text("out", $"artifacts/{options.Mode}");
                using var log = new RunLog(directory, options.Mode, options.Id, settings);
                Console.WriteLine($"Mode={options.Mode}; actuator output DISABLED; log={Path.GetFullPath(directory)}");

                switch (options.Mode)
                {
                    case "simulate": RunSimulation(options, detector, controller, log, directory, cancellation.Token); break;
                    case "image": RunImage(options, detector, controller, log, directory); break;
                    case "video": RunVideo(options, detector, controller, log, directory, cancellation.Token); break;
                    case "camera": await RunCamera(options, detector, controller, log, directory, cancellation.Token); break;
                    default: throw new ArgumentException($"Unknown mode: {options.Mode}");
                }
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                if (options.Preview) Cv2.DestroyAllWindows();
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            return 1;
        }
    }

    private static void RunSimulation(Options options, ArucoTargetDetector detector,
        PanTiltController controller, RunLog log, string directory, CancellationToken token)
    {
        using var scene = new SyntheticScene(options.Id);
        var count = options.Integer("frames", 300);
        var initial = controller.Step(null, TimeSpan.Zero);
        var pan = initial.PanDegrees;
        var tilt = initial.TiltDegrees;
        var detected = 0;
        for (var index = 0; index < count && !token.IsCancellationRequested; index++)
        {
            var time = TimeSpan.FromSeconds(index / 30.0);
            using var image = scene.Render(time.TotalSeconds, pan, tilt,
                hidden: time.TotalSeconds is >= 3 and < 4);
            var (observation, command) = Process(image, index, time, time, detector, controller, log);
            if (observation.HasTarget) detected++;
            pan = command.PanDegrees;
            tilt = command.TiltDegrees;
            if (index == 0 || index == count - 1)
                SaveOverlay(image, observation, command, Path.Combine(directory, $"frame-{index:D5}.png"));
            if (!Preview(image, observation, command, options.Preview)) break;
        }
        Console.WriteLine($"SIMULATION only: {detected} detected frames; ideal commanded angles feed the synthetic camera.");
    }

    private static void RunImage(Options options, ArucoTargetDetector detector,
        PanTiltController controller, RunLog log, string directory)
    {
        using var image = Cv2.ImRead(options.Required("input"), ImreadModes.Color);
        if (image.Empty()) throw new IOException("Could not read image.");
        var (observation, command) = Process(image, 0, TimeSpan.Zero, TimeSpan.Zero, detector, controller, log);
        SaveOverlay(image, observation, command, Path.Combine(directory, "annotated.png"));
        Console.WriteLine($"Detected={observation.HasTarget}; center=({observation.CenterX}, {observation.CenterY})");
    }

    private static void RunVideo(Options options, ArucoTargetDetector detector,
        PanTiltController controller, RunLog log, string directory, CancellationToken token)
    {
        using var capture = new VideoCapture(options.Required("input"));
        if (!capture.IsOpened()) throw new IOException("Could not open video.");
        var fps = options.Has("fps") ? options.Integer("fps", 30) : capture.Fps;
        if (!double.IsFinite(fps) || fps <= 0)
            throw new ArgumentException("Video FPS is unavailable. Supply --fps 30 (or the actual recording rate).");
        using var image = new Mat();
        var count = 0;
        while (count < options.Integer("frames", 300) && !token.IsCancellationRequested && capture.Read(image))
        {
            if (image.Empty()) break;
            // Nominal frame-index time for offline replay; not live capture or variable-rate PTS.
            var time = TimeSpan.FromSeconds(count / fps);
            var (observation, command) = Process(image, count, time, time, detector, controller, log);
            if (count == 0) SaveOverlay(image, observation, command, Path.Combine(directory, "first-frame.png"));
            count++;
            if (!Preview(image, observation, command, options.Preview)) break;
        }
        Console.WriteLine($"Offline video: {count} frames; time uses nominal FPS={fps:0.###}. Commands did not move the recorded camera.");
    }

    private static async Task RunCamera(Options options, ArucoTargetDetector detector,
        PanTiltController controller, RunLog log, string directory, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var buffer = new LatestFrameBuffer();
        var clock = Stopwatch.StartNew();
        var producer = Task.Run(() =>
        {
            using var capture = new VideoCapture(options.Integer("index", 0, allowZero: true), VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened()) throw new IOException("Could not open camera with DirectShow.");
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
                    throw new IOException("Camera stopped returning frames.");
                }
                // Arrival time, not the sensor exposure timestamp.
                buffer.Publish(new(number++, clock.Elapsed, image));
            }
        }, CancellationToken.None);

        var processed = 0;
        var lastArrival = clock.Elapsed;
        try
        {
            while (processed < options.Integer("frames", 300) && !token.IsCancellationRequested)
            {
                if (producer.IsFaulted) await producer;
                using var frame = buffer.Take();
                if (frame is null)
                {
                    controller.Step(null, clock.Elapsed);
                    if ((clock.Elapsed - lastArrival).TotalSeconds > 5)
                        throw new TimeoutException("No camera frame for five seconds.");
                    await Task.Delay(10, CancellationToken.None);
                    continue;
                }
                lastArrival = clock.Elapsed;
                var (observation, command) = Process(frame.Image, frame.Number, frame.CapturedAt,
                    clock.Elapsed, detector, controller, log, includeProcessingTime: true);
                if (processed == 0)
                    SaveOverlay(frame.Image, observation, command, Path.Combine(directory, "first-frame.png"));
                processed++;
                if (!Preview(frame.Image, observation, command, options.Preview)) break;
            }
        }
        finally
        {
            stop.Cancel();
            try { await producer.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { Console.Error.WriteLine("Camera driver is blocking Read; process exit will release it."); }
        }
        Console.WriteLine($"Live camera detection: {processed} processed frames. Servo output remains disabled.");
    }

    private static (TargetObservation, TrackingCommand) Process(Mat image, long number, TimeSpan captured,
        TimeSpan now, ArucoTargetDetector detector, PanTiltController controller, RunLog log,
        bool includeProcessingTime = false)
    {
        var watch = Stopwatch.StartNew();
        var observation = detector.Detect(image, number, captured);
        if (includeProcessingTime) now += watch.Elapsed;
        var command = controller.Step(observation, now);
        watch.Stop();
        log.Write(observation, command, now, watch.Elapsed.TotalMilliseconds);
        return (observation, command);
    }

    private static Mat Overlay(Mat image, TargetObservation observation, TrackingCommand command)
    {
        var result = new Mat();
        if (image.Channels() == 1) Cv2.CvtColor(image, result, ColorConversionCodes.GRAY2BGR);
        else image.CopyTo(result);
        Cv2.DrawMarker(result, new(image.Width / 2, image.Height / 2), Scalar.Blue,
            MarkerTypes.Cross, 24, 2);
        if (observation.HasTarget)
            Cv2.Circle(result, new((int)observation.CenterX!.Value, (int)observation.CenterY!.Value), 7, Scalar.Red, 2);
        Cv2.PutText(result, $"{command.State} | actuator output OFF", new(10, 25),
            HersheyFonts.HersheySimplex, 0.6, Scalar.Red, 1);
        return result;
    }

    private static void SaveOverlay(Mat image, TargetObservation observation, TrackingCommand command, string path)
    {
        using var overlay = Overlay(image, observation, command);
        if (!Cv2.ImWrite(path, overlay)) throw new IOException($"Could not save {path}.");
    }

    private static bool Preview(Mat image, TargetObservation observation, TrackingCommand command, bool enabled)
    {
        if (!enabled) return true;
        using var overlay = Overlay(image, observation, command);
        Cv2.ImShow("PanTiltTracker", overlay);
        return Cv2.WaitKey(1) != 27;
    }

    private static void PrintHelp() => Console.WriteLine("""
        PanTiltTracker (.NET 8 / Windows x64) — no actuator output in this milestone
        simulate [--frames 300] [--out artifacts/simulate] [--preview]
        marker   [--id 7] [--size 600] [--out artifacts/marker-7.png]
        image    --input image.png [--out artifacts/image]
        video    --input clip.mp4 [--frames 300] [--fps 30] [--preview]
        camera   [--index 0] [--frames 300] [--preview]
        inspect  --input clip.mp4 [--out artifacts/video-inspection]
        prepare-video --input original.mp4 --out prepared-1080p.mp4
        track-video --input clip.mp4 --algorithm csrt --roi x,y,width,height [--out artifacts/drone-csrt]
        detect-drone --input clip.mp4 --model models/drone/best.onnx [--tracking] [--frames 300] [--out artifacts/drone-detection]
        evaluate-drone --reference labels.csv --telemetry telemetry.csv --fps 30 [--out artifacts/drone-evaluation]
        track-video --input clip.mp4 --algorithm frame-diff [--out artifacts/drone-motion]
        All detection modes: --id 7 --config config/tracker.json --out <directory>
        """);

    private sealed class Options(string mode, Dictionary<string, string> values)
    {
        public string Mode => mode;
        public bool Preview => values.ContainsKey("preview");
        public int Id => Integer("id", 7, allowZero: true);
        public bool Has(string key) => values.ContainsKey(key);
        public string Text(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        public string Required(string key) => values.TryGetValue(key, out var value)
            ? value : throw new ArgumentException($"Missing --{key}.");
        public int Integer(string key, int fallback, bool allowZero = false)
        {
            if (!values.TryGetValue(key, out var value)) return fallback;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
                number < (allowZero ? 0 : 1))
                throw new ArgumentException($"Invalid --{key} value: {value}");
            return number;
        }

        public static Options Parse(string[] args)
        {
            if (args[0] is not ("simulate" or "marker" or "image" or "video" or "camera" or "inspect" or "prepare-video" or "track-video" or "detect-drone" or "evaluate-drone"))
                throw new ArgumentException($"Unknown mode: {args[0]}");
            string[] known = ["out", "id", "size", "input", "index", "frames", "fps", "config", "preview", "algorithm", "roi", "model", "reference", "telemetry", "tracking"];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index++)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || !known.Contains(args[index][2..]))
                    throw new ArgumentException($"Unknown option: {args[index]}");
                var key = args[index][2..];
                if (values.ContainsKey(key)) throw new ArgumentException($"Duplicate --{key}.");
                if (key is "preview" or "tracking") { values.Add(key, "true"); continue; }
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value after --{key}.");
                values.Add(key, args[index]);
            }
            return new(args[0], values);
        }
    }
}
