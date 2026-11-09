using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using PanTiltTracker.Core;

namespace PanTiltTracker.Vision;

public sealed record FaceLandmarks(Rect Box, Point2f[] Points, float DetectionScore);
public sealed record FaceDetectionResult(TargetObservation Observation, Rect[] Boxes, FaceLandmarks[]? Landmarks = null)
{
    public Rect? PrimaryBox => Boxes.Length == 0 ? null : Boxes[0];
}

/// <summary>Local CPU face detection across poses. Owns the network; never modifies the input image.</summary>
public sealed class FaceTargetDetector : IDisposable
{
    public const int WorkingMaxWidth = 640;
    internal const float ScoreThreshold = 0.8f;
    internal const float NmsThreshold = 0.3f;
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory,
        "models", "face", "face_detection_yunet_2023mar.onnx");
    private static readonly int[] strides = [8, 16, 32];
    private static readonly string[] outputNames =
        ["cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32", "bbox_8", "bbox_16", "bbox_32", "kps_8", "kps_16", "kps_32"];
    private readonly Net network;

    public FaceTargetDetector(string? modelPath = null)
    {
        modelPath ??= DefaultModelPath;
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("缺少人脸模型，请完整复制更新包中的 models 文件夹。", modelPath);
        // Keep Unicode paths in .NET rather than the old wrapper's ANSI filename marshaler.
        network = Net.ReadNetFromONNX(File.ReadAllBytes(modelPath)) ?? throw new InvalidDataException("无法加载 YuNet 人脸模型。");
        try
        {
            network.SetPreferableBackend(Backend.OPENCV);
            network.SetPreferableTarget(Target.CPU);
        }
        catch { network.Dispose(); throw; }
    }

    public FaceDetectionResult Detect(Mat image, long number, TimeSpan capturedAt)
    {
        if (image.Empty()) throw new ArgumentException("Cannot detect an empty image.", nameof(image));
        using var working = new Mat();
        switch (image.Channels())
        {
            case 1: Cv2.CvtColor(image, working, ColorConversionCodes.GRAY2BGR); break;
            case 3: image.CopyTo(working); break;
            case 4: Cv2.CvtColor(image, working, ColorConversionCodes.BGRA2BGR); break;
            default: throw new ArgumentException("Expected Gray8, BGR8 or BGRA8.", nameof(image));
        }
        if (working.Width > WorkingMaxWidth)
            Cv2.Resize(working, working, new(WorkingMaxWidth, Math.Max(1,
                (int)Math.Round(working.Height * (double)WorkingMaxWidth / working.Width))), interpolation: InterpolationFlags.Area);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(working, padded, 0, (32 - working.Height % 32) % 32,
            0, (32 - working.Width % 32) % 32, BorderTypes.Constant, Scalar.Black);
        using var blob = CvDnn.BlobFromImage(padded);
        network.SetInput(blob);
        var outputs = outputNames.Select(_ => new Mat()).ToArray();
        var candidates = new List<Rect>();
        var scores = new List<float>();
        var landmarks = new List<FaceLandmarks>();
        try
        {
            network.Forward(outputs, outputNames);
            // YuNet output decoding follows OpenCV 4.10 FaceDetectorYN (Apache-2.0):
            // https://github.com/opencv/opencv/blob/4.10.0/modules/objdetect/src/face_detect.cpp
            for (var level = 0; level < strides.Length; level++)
            {
                var stride = strides[level];
                var columns = padded.Width / stride;
                var count = columns * (padded.Height / stride);
                var classes = ReadFloats(outputs[level]);
                var objects = ReadFloats(outputs[level + 3]);
                var boxes = ReadFloats(outputs[level + 6]);
                var keypoints = ReadFloats(outputs[level + 9]);
                if (classes.Length != count || objects.Length != count || boxes.Length != count * 4 || keypoints.Length != count * 10)
                    throw new InvalidDataException("YuNet 输出尺寸与模型版本不匹配。");
                for (var index = 0; index < count; index++)
                {
                    var score = MathF.Sqrt(Math.Clamp(classes[index], 0, 1) * Math.Clamp(objects[index], 0, 1));
                    if (!float.IsFinite(score) || score < ScoreThreshold) continue;
                    var cx = (index % columns + boxes[index * 4]) * stride;
                    var cy = (index / columns + boxes[index * 4 + 1]) * stride;
                    var width = Math.Exp(boxes[index * 4 + 2]) * stride;
                    var height = Math.Exp(boxes[index * 4 + 3]) * stride;
                    if (!double.IsFinite(cx + cy + width + height)) continue;
                    var left = (int)Math.Floor(Math.Clamp(cx - width / 2, 0, working.Width));
                    var top = (int)Math.Floor(Math.Clamp(cy - height / 2, 0, working.Height));
                    var right = (int)Math.Ceiling(Math.Clamp(cx + width / 2, 0, working.Width));
                    var bottom = (int)Math.Ceiling(Math.Clamp(cy + height / 2, 0, working.Height));
                    if (right <= left || bottom <= top) continue;
                    var sourceBox = MapToSource(new(left, top, right - left, bottom - top), working.Size(), image.Size());
                    var points = new Point2f[5];
                    for (var k = 0; k < 5; k++)
                        points[k] = new((keypoints[index * 10 + k * 2] + index % columns) * stride * image.Width / working.Width,
                            (keypoints[index * 10 + k * 2 + 1] + index / columns) * stride * image.Height / working.Height);
                    candidates.Add(sourceBox);
                    scores.Add(score);
                    landmarks.Add(new(sourceBox, points, score));
                }
            }
        }
        finally { foreach (var output in outputs) output.Dispose(); }
        var merged = SelectDetections(candidates, scores);
        var primary = merged.Length == 0 ? (Rect?)null : merged[0];
        return new(new(number, capturedAt, image.Width, image.Height,
            primary is { } b ? b.X + b.Width / 2.0 : null,
            primary is { } p ? p.Y + p.Height / 2.0 : null), merged,
            merged.Select(box => landmarks.Where(l => l.Box == box).MaxBy(l => l.DetectionScore)!).ToArray());
    }

    private static float[] ReadFloats(Mat output)
    {
        var values = new float[checked((int)output.Total())];
        Marshal.Copy(output.Data, values, 0, values.Length);
        return values;
    }

    internal static Rect MapToSource(Rect box, Size from, Size to)
    {
        var left = Math.Clamp((int)Math.Round(box.X * (double)to.Width / from.Width), 0, to.Width);
        var top = Math.Clamp((int)Math.Round(box.Y * (double)to.Height / from.Height), 0, to.Height);
        var right = Math.Clamp((int)Math.Round(box.Right * (double)to.Width / from.Width), left, to.Width);
        var bottom = Math.Clamp((int)Math.Round(box.Bottom * (double)to.Height / from.Height), top, to.Height);
        return new(left, top, right - left, bottom - top);
    }

    internal static Rect[] SelectDetections(IReadOnlyList<Rect> boxes, IReadOnlyList<float> scores)
    {
        if (boxes.Count == 0) return [];
        CvDnn.NMSBoxes(boxes, scores, ScoreThreshold, NmsThreshold, out var kept);
        return kept.Select(index => boxes[index]).OrderByDescending(b => (long)b.Width * b.Height)
            .ThenBy(b => b.X).ThenBy(b => b.Y).ToArray();
    }

    public static void DrawResult(Mat image, FaceDetectionResult result, FaceAimResult? aim = null)
    {
        for (var index = 0; index < result.Boxes.Length; index++)
        {
            var box = result.Boxes[index];
            var primary = aim is null ? index == 0 : aim.SelectedBox is { } selected &&
                selected.X == box.X && selected.Y == box.Y && selected.Width == box.Width && selected.Height == box.Height;
            var color = primary ? new Scalar(70, 190, 70) : new Scalar(220, 180, 40);
            Cv2.Rectangle(image, box, color, primary ? 3 : 2);
            Cv2.PutText(image, primary ? (aim is null ? "face / largest" : "face / locked") : "face",
                new(box.X, Math.Max(18, box.Y - 6)), HersheyFonts.HersheySimplex, 0.55, color, 2, LineTypes.AntiAlias);
        }
        if (aim?.FilteredX is { } x && aim.FilteredY is { } y)
            Cv2.DrawMarker(image, new((int)Math.Round(x), (int)Math.Round(y)), new Scalar(0, 220, 255),
                MarkerTypes.Cross, 20, 2);
    }

    public void Dispose() => network.Dispose();
}
