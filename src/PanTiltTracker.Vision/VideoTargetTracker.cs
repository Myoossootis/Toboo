using OpenCvSharp;
using OpenCvSharp.Tracking;
using PanTiltTracker.Core;

namespace PanTiltTracker.Vision;

public enum VideoAlgorithm { Aruco, Csrt, FrameDifference }

public sealed record VideoTrackingResult(TargetObservation Observation, Rect? Box,
    string Status, int CandidateCount = 0);

/// <summary>Single-threaded recorded-video experiments. Owns its native buffers, never the input Mat.</summary>
public sealed class VideoTargetTracker : IDisposable
{
    public const int WorkingMaxWidth = 960;
    private readonly VideoAlgorithm algorithm;
    private readonly Rect? initialRegion;
    private readonly TrackerCSRT? csrt;
    private readonly Mat previous = new();
    private readonly Mat mask = new();
    private Size sourceSize;
    private Rect trackedBox;
    private bool started;
    private bool lost;
    private long lastNumber = -1;
    private bool disposed;

    // Borrowed buffer; valid until the next Process call or Dispose. Do not dispose it.
    public Mat MotionMask => mask;

    public VideoTargetTracker(VideoAlgorithm algorithm, Rect? initialRegion = null)
    {
        if (algorithm is not (VideoAlgorithm.Csrt or VideoAlgorithm.FrameDifference))
            throw new ArgumentException("Use ArucoTargetDetector for marker detection.", nameof(algorithm));
        if (algorithm == VideoAlgorithm.Csrt && initialRegion is null)
            throw new ArgumentException("CSRT requires a first-frame region.", nameof(initialRegion));
        this.algorithm = algorithm;
        this.initialRegion = initialRegion;
        if (algorithm == VideoAlgorithm.Csrt) csrt = TrackerCSRT.Create();
    }

    public VideoTrackingResult Process(Mat image, long number, TimeSpan capturedAt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (image.Empty() || image.Type() != MatType.CV_8UC3)
            throw new ArgumentException("Expected a nonempty BGR8 frame.", nameof(image));
        if (number <= lastNumber) throw new ArgumentException("Frame numbers must increase.", nameof(number));
        if (started && image.Size() != sourceSize) throw new ArgumentException("Frame size changed during tracking.");
        sourceSize = image.Size();
        using var working = new Mat();
        var width = Math.Min(WorkingMaxWidth, image.Width);
        var height = Math.Max(1, (int)Math.Round(image.Height * (double)width / image.Width));
        Cv2.Resize(image, working, new Size(width, height), interpolation: InterpolationFlags.Area);

        Rect? box = null;
        string status;
        var candidates = 0;
        if (algorithm == VideoAlgorithm.Csrt)
        {
            if (!started)
            {
                var region = initialRegion!.Value;
                ValidateRegion(region, sourceSize);
                var scaled = Map(region, sourceSize, working.Size());
                if (scaled.Width < 4 || scaled.Height < 4) throw new ArgumentException("Initial region is too small after resizing.");
                csrt!.Init(working, scaled);
                trackedBox = scaled;
                box = region;
                status = "manual_initialization";
            }
            else if (!lost && csrt!.Update(working, ref trackedBox))
            {
                box = Map(trackedBox, working.Size(), sourceSize);
                if (box.Value.Width > 0 && box.Value.Height > 0) status = "tracker_reported_target";
                else { box = null; lost = true; status = "lost_reinitialize"; }
            }
            else { lost = true; status = "lost_reinitialize"; }
        }
        else
        {
            using var gray = new Mat();
            Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
            status = "motion_warmup";
            if (started)
            {
                Cv2.Absdiff(gray, previous, mask);
                Cv2.Threshold(mask, mask, 25, 255, ThresholdTypes.Binary);
                using var close = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
                using var open = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, close);
                Cv2.MorphologyEx(mask, mask, MorphTypes.Open, open);
                Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);
                var area = width * (double)height;
                var eligible = contours.Select(c => (Contour: c, Area: Cv2.ContourArea(c)))
                    .Where(c => c.Area >= area * 0.0005 && c.Area <= area * 0.35)
                    .OrderByDescending(c => c.Area).ToArray();
                candidates = eligible.Length;
                if (candidates > 0) box = Map(Cv2.BoundingRect(eligible[0].Contour), working.Size(), sourceSize);
                status = box is null ? "no_motion_candidate" : "motion_candidate_not_classified";
            }
            gray.CopyTo(previous);
        }
        started = true;
        lastNumber = number;
        var observation = new TargetObservation(number, capturedAt, image.Width, image.Height,
            box is { } b ? b.X + b.Width / 2.0 : null, box is { } r ? r.Y + r.Height / 2.0 : null);
        return new(observation, box, status, candidates);
    }

    public static void ValidateRegion(Rect region, Size imageSize)
    {
        if (region.X < 0 || region.Y < 0 || region.Width < 8 || region.Height < 8 ||
            (long)region.X + region.Width > imageSize.Width || (long)region.Y + region.Height > imageSize.Height)
            throw new ArgumentException("Initial region must be inside the image and at least 8×8 pixels.");
    }

    private static Rect Map(Rect box, Size from, Size to)
    {
        var x = Math.Clamp((int)Math.Round(box.X * (double)to.Width / from.Width), 0, to.Width);
        var y = Math.Clamp((int)Math.Round(box.Y * (double)to.Height / from.Height), 0, to.Height);
        var right = Math.Clamp((int)Math.Round(((double)box.X + box.Width) * to.Width / from.Width), 0, to.Width);
        var bottom = Math.Clamp((int)Math.Round(((double)box.Y + box.Height) * to.Height / from.Height), 0, to.Height);
        return new(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    public static void DrawResult(Mat color, VideoTrackingResult result)
    {
        if (result.Box is { } box) Cv2.Rectangle(color, box, new Scalar(40, 185, 220), 2);
        Cv2.PutText(color, result.Status, new Point(12, 28), HersheyFonts.HersheySimplex,
            0.6, new Scalar(30, 130, 230), 2, LineTypes.AntiAlias);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        csrt?.Dispose();
        previous.Dispose();
        mask.Dispose();
    }
}
