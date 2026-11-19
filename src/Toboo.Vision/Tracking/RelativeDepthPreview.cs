using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace Toboo.Vision;

public readonly record struct DepthPoint(float X, float Y, float Z, uint Rgb);
public sealed record DepthPreviewFrame(byte[] ColorJpeg, byte[] DepthPng, DepthPoint[] Points,
    double InferenceMilliseconds);

/// <summary>Camera-local relative depth. All native allocations belong to the inference worker.</summary>
public sealed class RelativeDepthPreview : IDisposable
{
    public const string ModelSha256 = "2d8c6cb8f415229daf1eb041024208e2608c9f98e17c81cc7c6ecb449c56fd58";
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", "depth", "midas-small-v21.onnx");
    private readonly Net network;

    public RelativeDepthPreview(string? path = null)
    {
        path ??= DefaultModelPath;
        if (!File.Exists(path)) throw new FileNotFoundException("请先运行 scripts/prepare-depth-model.ps1，重新构建后开启空间预览。");
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("深度模型校验失败，请重新准备模型。");
        network = Net.ReadNetFromONNX(bytes) ?? throw new InvalidDataException("无法载入深度模型。");
        try { network.SetPreferableBackend(Backend.OPENCV); network.SetPreferableTarget(Target.CPU); }
        catch { network.Dispose(); throw; }
    }

    public DepthPreviewFrame Infer(Mat image)
    {
        if (image.Empty()) throw new ArgumentException("图片为空。");
        var watch = Stopwatch.StartNew();
        using var color = new Mat();
        switch (image.Channels())
        {
            case 1: Cv2.CvtColor(image, color, ColorConversionCodes.GRAY2BGR); break;
            case 3: image.CopyTo(color); break;
            case 4: Cv2.CvtColor(image, color, ColorConversionCodes.BGRA2BGR); break;
            default: throw new ArgumentException("不支持的图像格式。");
        }
        using var resized = new Mat();
        Cv2.Resize(color, resized, new(256, 256), interpolation: InterpolationFlags.Cubic);
        using var input = CvDnn.BlobFromImage(resized, 1.0 / 255, swapRB: true);
        network.SetInput(input);
        using var output = network.Forward();
        if (output.Type() != MatType.CV_32FC1 || output.Total() != 256 * 256)
            throw new InvalidDataException("深度模型输出尺寸不正确。");
        using var flat = output.Reshape(1, 256);
        return CreateFrame(color, flat, watch);
    }

    public static DepthPreviewFrame CreateDemo()
    {
        var watch = Stopwatch.StartNew();
        using var color = new Mat(480, 640, MatType.CV_8UC3, new Scalar(180, 175, 158));
        using var inverse = new Mat(480, 640, MatType.CV_32FC1, new Scalar(1));
        Cv2.Rectangle(color, new Rect(0, 320, 640, 160), new Scalar(70, 110, 150), -1);
        for (var y = 320; y < 480; y++)
            Cv2.Line(inverse, new(0, y), new(639, y), new Scalar(3 + (y - 320) / 30.0));
        Cv2.Rectangle(color, new Rect(190, 70, 210, 290), new Scalar(110, 160, 193), -1);
        Cv2.Rectangle(inverse, new Rect(190, 70, 210, 290), new Scalar(6), -1);
        Point[] side = [new(400, 70), new(465, 110), new(465, 325), new(400, 360)];
        Cv2.FillConvexPoly(color, side, new Scalar(75, 120, 158));
        Cv2.FillConvexPoly(inverse, side, new Scalar(5));
        Cv2.Rectangle(color, new Rect(255, 170, 72, 55), Scalar.White, -1);
        return CreateFrame(color, inverse, watch);
    }

    private static DepthPreviewFrame CreateFrame(Mat color, Mat flat, Stopwatch watch)
    {
        var width = 128;
        var height = Math.Clamp((int)Math.Round(width * (double)color.Height / color.Width), 32, 160);
        using var inverse = new Mat();
        using var thumbnail = new Mat();
        Cv2.Resize(flat, inverse, new(width, height), interpolation: InterpolationFlags.Linear);
        Cv2.Resize(color, thumbnail, new(width, height), interpolation: InterpolationFlags.Area);
        var values = new float[width * height];
        Marshal.Copy(inverse.Data, values, 0, values.Length);
        var normalized = Normalize(values);
        using var gray = new Mat(height, width, MatType.CV_8UC1);
        var pixels = normalized.Select(value => (byte)Math.Clamp(value * 255, 0, 255)).ToArray();
        Marshal.Copy(pixels, 0, gray.Data, pixels.Length);
        using var heat = new Mat();
        Cv2.ApplyColorMap(gray, heat, ColormapTypes.Turbo);
        var points = new List<DepthPoint>(values.Length);
        var focal = width * 0.9f;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (!float.IsFinite(values[index])) continue;
                // This bounded inverse-depth transform supplies visual relief in arbitrary units.
                // Sonar readings never scale or validate the full monocular field.
                var z = 1f / (0.35f + normalized[index]);
                var rgb = thumbnail.At<Vec3b>(y, x);
                points.Add(new((x - (width - 1) / 2f) * z / focal,
                    (y - (height - 1) / 2f) * z / focal, z,
                    (uint)(rgb.Item2 << 16 | rgb.Item1 << 8 | rgb.Item0)));
            }
        using var display = new Mat();
        var displayWidth = Math.Min(color.Width, 800);
        Cv2.Resize(color, display, new(displayWidth, Math.Max(1, color.Height * displayWidth / color.Width)));
        Cv2.ImEncode(".jpg", display, out var colorJpeg);
        Cv2.ImEncode(".png", heat, out var depthJpeg);
        return new(colorJpeg, depthJpeg, points.ToArray(), watch.Elapsed.TotalMilliseconds);
    }

    internal static float[] Normalize(float[] values)
    {
        var sorted = values.Where(float.IsFinite).Order().ToArray();
        if (sorted.Length == 0) throw new InvalidDataException("深度推理没有有效输出。");
        var low = sorted[(sorted.Length - 1) * 2 / 100];
        var high = sorted[(sorted.Length - 1) * 98 / 100];
        var span = high - low;
        return values.Select(value => !float.IsFinite(value) || span <= 1e-6
            ? 0.5f : Math.Clamp((value - low) / span, 0, 1)).ToArray();
    }

    public void Dispose() => network.Dispose();
}
