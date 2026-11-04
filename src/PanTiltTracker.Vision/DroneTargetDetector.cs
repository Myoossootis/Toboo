using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using PanTiltTracker.Core;

namespace PanTiltTracker.Vision;

public readonly record struct DroneDetection(Rect Box, float Score);
public sealed record DroneDetectionResult(TargetObservation Observation, DroneDetection[] Detections)
{
    public Rect? PrimaryBox => Detections.Length == 0 ? null : Detections[0].Box;
    public float? PrimaryScore => Detections.Length == 0 ? null : Detections[0].Score;
}

/// <summary>Single-class YOLOv5 CPU inference. Owns native/tensor buffers, never the input image.</summary>
public sealed class DroneTargetDetector : IDisposable
{
    public const string ModelSha256 = "4d078f4235221dbaa1299796d5a6bd5cc8f2210013b07e8199e5d06e88b43e8a";
    public const float ScoreThreshold = 0.35f;
    public const float NmsThreshold = 0.45f;
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", "drone", "best.onnx");
    private readonly InferenceSession session;
    private readonly RunOptions runOptions;
    private readonly OrtValue input;
    private readonly float[] pixels;
    private readonly string[] inputNames;
    private readonly string[] outputNames;
    private bool disposed;
    public int InputWidth { get; }
    public int InputHeight { get; }

    public DroneTargetDetector(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("无人机模型不存在。请运行 scripts/prepare-drone-model.ps1，再选择 models/drone/best.onnx。", modelPath);
        var bytes = File.ReadAllBytes(modelPath); // Unicode paths stay in .NET.
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != ModelSha256)
            throw new ArgumentException("第一阶段仅支持已核验的无人机 best.onnx，请使用 prepare-drone-model.ps1 下载的版本。", nameof(modelPath));
        using var settings = new SessionOptions { IntraOpNumThreads = 4, InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        session = new InferenceSession(bytes, settings);
        try
        {
            if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1)
                throw new InvalidDataException("Expected one YOLOv5 input and output.");
            var metadata = session.InputMetadata.Single();
            var shape = metadata.Value.Dimensions;
            if (metadata.Value.ElementType != typeof(float) || shape.Length != 4 || shape[0] != 1 || shape[1] != 3 ||
                shape[2] <= 0 || shape[3] <= 0 || shape[2] > 1280 || shape[3] > 1280)
                throw new InvalidDataException("Expected fixed float32 NCHW input [1,3,H,W].");
            InputHeight = shape[2]; InputWidth = shape[3];
            inputNames = [metadata.Key]; outputNames = session.OutputMetadata.Keys.ToArray();
            pixels = new float[3 * InputWidth * InputHeight];
            runOptions = new();
            input = OrtValue.CreateTensorValueFromMemory(pixels, [1, 3, InputHeight, InputWidth]);
        }
        catch { runOptions?.Dispose(); session.Dispose(); throw; }
    }

    public DroneDetectionResult Detect(Mat image, long number, TimeSpan capturedAt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var geometry = PrepareInput(image, new(InputWidth, InputHeight), pixels);
        using var outputs = session.Run(runOptions, inputNames, [input], outputNames);
        var output = outputs[0];
        var outputShape = output.GetTensorTypeAndShape();
        var detections = Decode(output.GetTensorDataAsSpan<float>(), outputShape.Shape, geometry);
        var primary = detections.Length == 0 ? (Rect?)null : detections[0].Box;
        return new(new(number, capturedAt, image.Width, image.Height,
            primary is { } b ? b.X + b.Width / 2.0 : null,
            primary is { } r ? r.Y + r.Height / 2.0 : null), detections);
    }

    internal readonly record struct Letterbox(Size Source, Size Resized, int Left, int Top)
    {
        public Rect? Map(float cx, float cy, float width, float height)
        {
            if (!float.IsFinite(cx) || !float.IsFinite(cy) || !float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) return null;
            var x = (int)Math.Clamp(Math.Floor(((double)cx - width / 2.0 - Left) * Source.Width / Resized.Width), 0, Source.Width);
            var y = (int)Math.Clamp(Math.Floor(((double)cy - height / 2.0 - Top) * Source.Height / Resized.Height), 0, Source.Height);
            var right = (int)Math.Clamp(Math.Ceiling(((double)cx + width / 2.0 - Left) * Source.Width / Resized.Width), 0, Source.Width);
            var bottom = (int)Math.Clamp(Math.Ceiling(((double)cy + height / 2.0 - Top) * Source.Height / Resized.Height), 0, Source.Height);
            return right > x && bottom > y ? new Rect(x, y, right - x, bottom - y) : null;
        }
    }

    internal static Letterbox PrepareInput(Mat image, Size inputSize, float[] destination)
    {
        if (image.Empty() || image.Depth() != MatType.CV_8U || image.Channels() is not (1 or 3 or 4))
            throw new ArgumentException("Expected nonempty Gray8, BGR8 or BGRA8 image.", nameof(image));
        if (inputSize.Width <= 0 || inputSize.Height <= 0 || destination.Length != 3 * inputSize.Width * inputSize.Height)
            throw new ArgumentException("Input tensor size does not match.", nameof(destination));
        var scale = Math.Min(inputSize.Width / (double)image.Width, inputSize.Height / (double)image.Height);
        var size = new Size(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        var left = (inputSize.Width - size.Width) / 2; var top = (inputSize.Height - size.Height) / 2;
        using var rgb = new Mat();
        Cv2.CvtColor(image, rgb, image.Channels() switch { 1 => ColorConversionCodes.GRAY2RGB,
            3 => ColorConversionCodes.BGR2RGB, _ => ColorConversionCodes.BGRA2RGB });
        using var resized = new Mat();
        Cv2.Resize(rgb, resized, size, interpolation: InterpolationFlags.Linear);
        Array.Fill(destination, 114f / 255f);
        var plane = inputSize.Width * inputSize.Height;
        var indexer = resized.GetGenericIndexer<Vec3b>();
        for (var y = 0; y < size.Height; y++)
            for (var x = 0; x < size.Width; x++)
            {
                var p = indexer[y, x]; var offset = (y + top) * inputSize.Width + x + left;
                destination[offset] = p.Item0 / 255f;
                destination[offset + plane] = p.Item1 / 255f;
                destination[offset + 2 * plane] = p.Item2 / 255f;
            }
        return new(image.Size(), size, left, top);
    }

    internal static DroneDetection[] Decode(ReadOnlySpan<float> data, long[] shape, Letterbox geometry)
    {
        if (shape.Length != 3 || shape[0] != 1 || shape[1] <= 0 || shape[2] != 6 || data.Length != shape[1] * 6)
            throw new InvalidDataException("Expected single-class YOLOv5 output [1,N,6], not YOLO11 or a COCO model.");
        var boxes = new List<Rect>(); var scores = new List<float>();
        for (var i = 0; i < data.Length; i += 6)
        {
            var obj = data[i + 4]; var cls = data[i + 5];
            if (!float.IsFinite(obj) || !float.IsFinite(cls) || obj < 0 || obj > 1 || cls < 0 || cls > 1) continue;
            var score = obj * cls;
            if (score < ScoreThreshold || geometry.Map(data[i], data[i + 1], data[i + 2], data[i + 3]) is not { } box) continue;
            boxes.Add(box); scores.Add(score);
        }
        if (boxes.Count == 0) return [];
        // NMSBoxes expects x,y,width,height, not two opposite corners.
        OpenCvSharp.Dnn.CvDnn.NMSBoxes(boxes, scores, ScoreThreshold, NmsThreshold, out var retained);
        return retained.Select(i => new DroneDetection(boxes[i], scores[i])).OrderByDescending(d => d.Score)
            .ThenByDescending(d => (long)d.Box.Width * d.Box.Height).ThenBy(d => d.Box.X).ThenBy(d => d.Box.Y).ToArray();
    }

    public static void DrawResult(Mat color, DroneDetectionResult result)
    {
        for (var i = 0; i < result.Detections.Length; i++)
        {
            var d = result.Detections[i];
            var pen = i == 0 ? new Scalar(45, 190, 90) : new Scalar(220, 175, 50);
            Cv2.Rectangle(color, d.Box, pen, 2);
            Cv2.PutText(color, FormattableString.Invariant($"drone {d.Score:0.00}"),
                new Point(d.Box.X, Math.Max(16, d.Box.Y - 6)), HersheyFonts.HersheySimplex, 0.5, pen, 1, LineTypes.AntiAlias);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; input.Dispose(); runOptions.Dispose(); session.Dispose();
    }
}
