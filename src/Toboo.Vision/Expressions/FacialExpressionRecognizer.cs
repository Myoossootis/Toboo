using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using Toboo.Core;

namespace Toboo.Vision;

public sealed record ExpressionInference(double[]? Scores, string Reason, double InferenceMilliseconds = 0,
    double? Brightness = null, double? Sharpness = null);

/// <summary>Local CPU expression classifier. All Mats and the network are owned and disposed here.</summary>
public sealed class FacialExpressionRecognizer : IDisposable
{
    public const string ModelSha256 = "4f61307602fc089ce20488a31d4e4614e3c9753a7d6c41578c854858b183e1a9";
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", "expression",
        "facial_expression_recognition_mobilefacenet_2022july.onnx");
    internal static readonly Point2f[] Reference = [new(38.2946f, 51.6963f), new(73.5318f, 51.5014f),
        new(56.0252f, 71.7366f), new(41.5493f, 92.3655f), new(70.7299f, 92.2041f)];
    private readonly Net network;
    public FacialExpressionRecognizer(string? path = null)
    {
        path ??= DefaultModelPath;
        var data = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(data)).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("表情模型与已验证版本不一致，请恢复 models/expression 文件夹。");
        network = Net.ReadNetFromONNX(data) ?? throw new InvalidDataException("无法载入表情模型。");
        try { network.SetPreferableBackend(Backend.OPENCV); network.SetPreferableTarget(Target.CPU); }
        catch { network.Dispose(); throw; }
    }
    public ExpressionInference Infer(Mat image, FaceLandmarks face)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (image.Empty() || image.Channels() != 3) throw new ArgumentException("Expected nonempty BGR image.");
        if (face.Box.Width < 64 || face.Box.Height < 64) return new(null, "face_too_small");
        if (face.Box.X <= 1 || face.Box.Y <= 1 || face.Box.Right >= image.Width - 1 || face.Box.Bottom >= image.Height - 1)
            return new(null, "face_clipped");
        if (face.Points.Length != 5 || face.Points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) ||
                p.X < 0 || p.X >= image.Width || p.Y < 0 || p.Y >= image.Height)) return new(null, "landmarks_invalid");
        var eyeDistance = Math.Sqrt(Math.Pow(face.Points[1].X - face.Points[0].X, 2) + Math.Pow(face.Points[1].Y - face.Points[0].Y, 2));
        var nose = face.Points[2];
        if (eyeDistance < face.Box.Width * 0.20 || nose.X < Math.Min(face.Points[0].X, face.Points[1].X) ||
            nose.X > Math.Max(face.Points[0].X, face.Points[1].X)) return new(null, "pose_or_occlusion");
        using var aligned = Align(image, face.Points);
        using var gray = new Mat(); Cv2.CvtColor(aligned, gray, ColorConversionCodes.BGR2GRAY);
        var brightness = Cv2.Mean(gray).Val0;
        using var laplacian = new Mat(); Cv2.Laplacian(gray, laplacian, MatType.CV_64FC1);
        Cv2.MeanStdDev(laplacian, out _, out var deviation);
        var sharpness = deviation.Val0 * deviation.Val0;
        if (brightness is < 25 or > 235 || sharpness < 35)
            return new(null, "lighting_or_blur", watch.Elapsed.TotalMilliseconds, brightness, sharpness);
        var probabilities = PredictAligned(aligned);
        return new(probabilities, "inferred", watch.Elapsed.TotalMilliseconds, brightness, sharpness);
    }
    internal double[] PredictAligned(Mat aligned)
    {
        using var blob = CvDnn.BlobFromImage(aligned, 1 / 127.5, new(112, 112), new(127.5, 127.5, 127.5), swapRB: true);
        network.SetInput(blob, "data");
        using var output = network.Forward("label");
        if (output.Total() != 7) throw new InvalidDataException("表情模型输出必须为七类 logits。");
        var logits = new float[7]; Marshal.Copy(output.Data, logits, 0, 7);
        return ExpressionTracker.Softmax(logits);
    }
    // Least-squares, non-reflecting similarity fit to all five landmarks, equivalent to Zoo's alignment.
    internal static Mat Align(Mat image, Point2f[] source)
    {
        if (source.Length != 5) throw new ArgumentException("Five landmarks required.");
        var x = source.Average(p => (double)p.X); var y = source.Average(p => (double)p.Y);
        var u = Reference.Average(p => (double)p.X); var v = Reference.Average(p => (double)p.Y);
        double denominator = 0, a = 0, b = 0;
        for (var i = 0; i < 5; i++)
        {
            var dx = source[i].X - x; var dy = source[i].Y - y;
            var du = Reference[i].X - u; var dv = Reference[i].Y - v;
            denominator += dx * dx + dy * dy; a += dx * du + dy * dv; b += dx * dv - dy * du;
        }
        if (!double.IsFinite(denominator) || denominator < 1e-6) throw new ArgumentException("Degenerate landmarks.");
        a /= denominator; b /= denominator;
        using var transform = new Mat(2, 3, MatType.CV_64FC1);
        transform.Set(0, 0, a); transform.Set(0, 1, -b); transform.Set(0, 2, u - a * x + b * y);
        transform.Set(1, 0, b); transform.Set(1, 1, a); transform.Set(1, 2, v - b * x - a * y);
        var aligned = new Mat();
        try { Cv2.WarpAffine(image, aligned, transform, new(112, 112)); return aligned; }
        catch { aligned.Dispose(); throw; }
    }
    public void Dispose() => network.Dispose();
}
