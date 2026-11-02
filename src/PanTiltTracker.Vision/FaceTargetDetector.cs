using OpenCvSharp;
using PanTiltTracker.Core;

namespace PanTiltTracker.Vision;

public sealed record FaceDetectionResult(TargetObservation Observation, Rect[] Boxes)
{
    public Rect? PrimaryBox => Boxes.Length == 0 ? null : Boxes[0];
}

/// <summary>Frontal face detection only. Owns the classifier; borrows, never modifies the input image.</summary>
public sealed class FaceTargetDetector : IDisposable
{
    public const int WorkingMaxWidth = 640;
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory,
        "models", "face", "haarcascade_frontalface_default.xml");
    private readonly CascadeClassifier classifier;

    public FaceTargetDetector(string? modelPath = null)
    {
        modelPath ??= DefaultModelPath;
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("缺少人脸模型，请完整解压或覆盖更新包中的 models 文件夹。", modelPath);
        // This OpenCvSharp version marshals filenames as ANSI. Keep Unicode paths in .NET;
        // the upstream XML is ASCII and can safely be passed to OpenCV as in-memory data.
        using var storage = new FileStorage(File.ReadAllText(modelPath), FileStorage.Modes.Read | FileStorage.Modes.Memory);
        using var node = storage.GetFirstTopLevelNode() ?? throw new InvalidDataException("人脸模型缺少级联节点。");
        classifier = new CascadeClassifier();
        try
        {
            if (!classifier.Read(node) || classifier.Empty()) throw new InvalidDataException("无法加载人脸检测模型。");
        }
        catch
        {
            classifier.Dispose();
            throw;
        }
    }

    public FaceDetectionResult Detect(Mat image, long number, TimeSpan capturedAt)
    {
        if (image.Empty()) throw new ArgumentException("Cannot detect an empty image.", nameof(image));
        using var gray = new Mat();
        switch (image.Channels())
        {
            case 1: image.CopyTo(gray); break;
            case 3: Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY); break;
            case 4: Cv2.CvtColor(image, gray, ColorConversionCodes.BGRA2GRAY); break;
            default: throw new ArgumentException("Expected Gray8, BGR8 or BGRA8.", nameof(image));
        }
        if (gray.Width > WorkingMaxWidth)
            Cv2.Resize(gray, gray, new(WorkingMaxWidth, Math.Max(1,
                (int)Math.Round(gray.Height * (double)WorkingMaxWidth / gray.Width))), interpolation: InterpolationFlags.Area);
        Cv2.EqualizeHist(gray, gray);
        var rectangles = classifier.DetectMultiScale(gray, 1.1, 5, HaarDetectionTypes.ScaleImage, new Size(30, 30));
        var boxes = rectangles.Select(r =>
        {
            var left = Math.Clamp((int)Math.Round(r.X * (double)image.Width / gray.Width), 0, image.Width);
            var top = Math.Clamp((int)Math.Round(r.Y * (double)image.Height / gray.Height), 0, image.Height);
            var right = Math.Clamp((int)Math.Round(r.Right * (double)image.Width / gray.Width), left, image.Width);
            var bottom = Math.Clamp((int)Math.Round(r.Bottom * (double)image.Height / gray.Height), top, image.Height);
            return new Rect(left, top, right - left, bottom - top);
        }).Where(r => r.Width > 0 && r.Height > 0)
            .OrderByDescending(r => (long)r.Width * r.Height).ThenBy(r => r.X).ThenBy(r => r.Y).ToArray();
        var primary = boxes.Length == 0 ? (Rect?)null : boxes[0];
        return new(new(number, capturedAt, image.Width, image.Height,
            primary is { } b ? b.X + b.Width / 2.0 : null,
            primary is { } p ? p.Y + p.Height / 2.0 : null), boxes);
    }

    public static void DrawResult(Mat image, FaceDetectionResult result)
    {
        for (var index = 0; index < result.Boxes.Length; index++)
        {
            var box = result.Boxes[index];
            var color = index == 0 ? new Scalar(70, 190, 70) : new Scalar(220, 180, 40);
            Cv2.Rectangle(image, box, color, index == 0 ? 3 : 2);
            Cv2.PutText(image, index == 0 ? "face / largest" : "face",
                new(box.X, Math.Max(18, box.Y - 6)), HersheyFonts.HersheySimplex, 0.55, color, 2, LineTypes.AntiAlias);
        }
    }

    public void Dispose() => classifier.Dispose();
}
