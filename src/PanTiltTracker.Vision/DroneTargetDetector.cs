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

/// <summary>Verified CPU model profiles, emitting drone boxes only. Owns buffers, never the input image.</summary>
public sealed class DroneTargetDetector : IDisposable
{
    public const string ModelSha256 = "4d078f4235221dbaa1299796d5a6bd5cc8f2210013b07e8199e5d06e88b43e8a";
    public const string CandidateModelSha256 = "ad7dbd93bb7da4e20651d49d7dff399af8ffc7a2c69a4214fe0a682179318fed";
    public const string Yolo11ModelSha256 = "f3b238b537fe420f21ce1e2504055a21816b5ad80a8d9346f56ac3bced1afc29";
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
    public string LoadedModelSha256 { get; }
    public string ModelId => LoadedModelSha256 == Yolo11ModelSha256 ? "yolo11n_drone_bird" : "yolov5_drone";

    public DroneTargetDetector(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("无人机模型不存在。请运行 scripts/prepare-drone-model.ps1，再选择 models/drone/best.onnx。", modelPath);
        var bytes = File.ReadAllBytes(modelPath); // Unicode paths stay in .NET.
        LoadedModelSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (LoadedModelSha256 is not (ModelSha256 or CandidateModelSha256 or Yolo11ModelSha256))
            throw new ArgumentException("仅支持已核验的无人机 ONNX，请使用 prepare-drone-model.ps1 下载的版本。", nameof(modelPath));
        using var settings = new SessionOptions { IntraOpNumThreads = 4, InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        session = new InferenceSession(bytes, settings);
        try
        {
            if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1)
                throw new InvalidDataException("Expected one YOLOv5 input and output.");
            var metadata = session.InputMetadata.Single();
            var shape = metadata.Value.Dimensions;
            var yolo11 = LoadedModelSha256 == Yolo11ModelSha256;
            if (metadata.Value.ElementType != typeof(float) || shape.Length != 4 || shape[1] != 3 ||
                (!yolo11 && (shape[0] != 1 || shape[2] <= 0 || shape[3] <= 0 || shape[2] > 1280 || shape[3] > 1280)))
                throw new InvalidDataException("Expected fixed float32 NCHW input [1,3,H,W].");
            InputHeight = yolo11 ? 640 : shape[2]; InputWidth = yolo11 ? 640 : shape[3];
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
        var detections = Decode(output.GetTensorDataAsSpan<float>(), outputShape.Shape, geometry,
            LoadedModelSha256 == Yolo11ModelSha256);
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

    internal static DroneDetection[] Decode(ReadOnlySpan<float> data, long[] shape, Letterbox geometry, bool yolo11 = false)
    {
        if (shape.Length != 3 || shape[0] != 1 || shape[1] <= 0 || shape[2] <= 0 ||
            (yolo11 ? shape[1] != 6 : shape[2] != 6) || data.Length != shape[1] * shape[2])
            throw new InvalidDataException("Output does not match the pinned YOLOv5 [1,N,6] or YOLO11 [1,6,N] profile.");
        var boxes = new List<Rect>(); var scores = new List<float>();
        var rows = (int)(yolo11 ? shape[2] : shape[1]);
        for (var row = 0; row < rows; row++)
        {
            var i = row * 6;
            var obj = yolo11 ? 1 : data[i + 4];
            var cls = yolo11 ? data[4 * rows + row] : data[i + 5];
            if (yolo11 && (!float.IsFinite(data[5 * rows + row]) || data[5 * rows + row] is < 0 or > 1 || data[5 * rows + row] > cls)) continue;
            if (!float.IsFinite(obj) || !float.IsFinite(cls) || obj < 0 || obj > 1 || cls < 0 || cls > 1) continue;
            var score = obj * cls;
            var mapped = yolo11 ? geometry.Map(data[row], data[rows + row], data[2 * rows + row], data[3 * rows + row])
                : geometry.Map(data[i], data[i + 1], data[i + 2], data[i + 3]);
            if (score < ScoreThreshold || mapped is not { } box) continue;
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
