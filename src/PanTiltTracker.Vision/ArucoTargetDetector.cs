using OpenCvSharp;
using OpenCvSharp.Aruco;
using PanTiltTracker.Core;

namespace PanTiltTracker.Vision;

public sealed class ArucoTargetDetector : IDisposable
{
    private readonly OpenCvSharp.Aruco.Dictionary dictionary =
        CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
    private readonly DetectorParameters parameters = new();
    private readonly int targetId;

    public int TargetId => targetId;

    public ArucoTargetDetector(int targetId = 7)
    {
        if (targetId is < 0 or >= 50)
            throw new ArgumentOutOfRangeException(nameof(targetId), "DICT_4X4_50 IDs are 0–49.");
        this.targetId = targetId;
    }

    public TargetObservation Detect(Mat image, long frameNumber, TimeSpan capturedAt)
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

        CvAruco.DetectMarkers(gray, dictionary, out var corners, out var ids, parameters, out _);
        var matches = Enumerable.Range(0, ids.Length).Where(index => ids[index] == targetId).ToArray();
        // Duplicate IDs are ambiguous: hold position instead of selecting an arbitrary target.
        if (matches.Length != 1)
            return new(frameNumber, capturedAt, image.Width, image.Height, null, null);

        var points = corners[matches[0]];
        return new(frameNumber, capturedAt, image.Width, image.Height,
            points.Average(point => (double)point.X), points.Average(point => (double)point.Y));
    }

    public static Mat CreateMarker(int id = 7, int sidePixels = 600)
    {
        if (id is < 0 or >= 50 || sidePixels < 60)
            throw new ArgumentOutOfRangeException(nameof(id), "Use ID 0–49 and marker size >= 60 pixels.");
        using var dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
        using var marker = new Mat();
        dictionary.GenerateImageMarker(id, sidePixels, marker, 1);
        var margin = Math.Max(20, sidePixels / 6);
        var printable = new Mat();
        Cv2.CopyMakeBorder(marker, printable, margin, margin, margin, margin,
            BorderTypes.Constant, Scalar.White);
        return printable;
    }

    public void Dispose()
    {
        dictionary.Dispose();
    }
}
