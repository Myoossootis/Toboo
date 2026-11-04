using OpenCvSharp;
using PanTiltTracker.Core;

namespace PanTiltTracker.Vision;

public sealed record DroneTrackingResult(TargetObservation Observation, Rect? Box, string Status,
    float? DetectionScore, int? CandidateCount, string? BoxSource, int TrackEpoch);

/// <summary>Detector-initiated CSRT; owns its tracker, borrows its detector and input images.</summary>
public sealed class DroneTargetTracker : IDisposable
{
    public static readonly TimeSpan DetectionInterval = TimeSpan.FromSeconds(0.2);
    public static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxFrameGap = TimeSpan.FromSeconds(0.2);
    private readonly Func<Mat, long, TimeSpan, DroneDetectionResult> detect;
    private VideoTargetTracker? tracker;
    private TimeSpan? lastDetection;
    private TimeSpan? lastVerified;
    private TimeSpan previousTime;
    private long previousNumber = -1;
    private Size sourceSize;
    private int epoch;
    private bool disposed;

    public DroneTargetTracker(DroneTargetDetector detector) : this(detector.Detect) { }
    internal DroneTargetTracker(Func<Mat, long, TimeSpan, DroneDetectionResult> detect) => this.detect = detect;

    public DroneTrackingResult Process(Mat image, long number, TimeSpan capturedAt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (image.Empty() || image.Type() != MatType.CV_8UC3) throw new ArgumentException("Expected a BGR8 frame.");
        if (number <= previousNumber || (previousNumber >= 0 && capturedAt < previousTime))
            throw new ArgumentException("Frame numbers and capture time must be monotonic.");
        if (previousNumber >= 0 && image.Size() != sourceSize) throw new ArgumentException("Frame size changed during tracking.");
        if (previousNumber >= 0 && capturedAt - previousTime > MaxFrameGap) { Reset(); lastDetection = null; }
        previousTime = capturedAt; previousNumber = number; sourceSize = image.Size();

        Rect? trackedBox = null;
        if (tracker is not null && lastVerified is { } verified && capturedAt - verified <= VerificationTimeout)
        {
            trackedBox = tracker.Process(image, number, capturedAt).Box;
            if (trackedBox is null) Reset();
        }
        else Reset();

        var runDetection = tracker is null || lastDetection is null || capturedAt - lastDetection.Value >= DetectionInterval;
        var proposals = runDetection ? detect(image, number, capturedAt).Detections : null;
        if (runDetection) lastDetection = capturedAt;
        DroneDetection? selected = null;
        if (proposals is not null)
        {
            if (trackedBox is { } existing && tracker is not null)
            {
                // A higher score elsewhere must not silently switch an active target.
                selected = proposals.Where(p => IoU(existing, p.Box) >= 0.1)
                    .OrderByDescending(p => IoU(existing, p.Box)).ThenByDescending(p => p.Score)
                    .Select(p => (DroneDetection?)p).FirstOrDefault();
            }
            else selected = proposals.OrderByDescending(p => p.Score).Select(p => (DroneDetection?)p).FirstOrDefault();
        }
        if (selected is { } detection)
        {
            var newLock = tracker is null;
            Reset();
            if (newLock) epoch++;
            var scale = Math.Min(1, VideoTargetTracker.WorkingMaxWidth / (double)image.Width);
            if (detection.Box.Width >= 8 && detection.Box.Height >= 8 &&
                detection.Box.Width * scale >= 4 && detection.Box.Height * scale >= 4)
            {
                var replacement = new VideoTargetTracker(VideoAlgorithm.Csrt, detection.Box);
                try { replacement.Process(image, number, capturedAt); }
                catch { replacement.Dispose(); throw; }
                tracker = replacement; lastVerified = capturedAt;
            }
            return Result(detection.Box, newLock ? "drone_acquired" : "drone_verified", detection.Score, "detector");
        }
        if (tracker is not null && trackedBox is { } current)
            return Result(current, "drone_tracked", null, "csrt");
        return Result(null, "drone_searching", null, null);

        DroneTrackingResult Result(Rect? box, string status, float? score, string? source) => new(
            new(number, capturedAt, image.Width, image.Height,
                box is { } b ? b.X + b.Width / 2.0 : null, box is { } r ? r.Y + r.Height / 2.0 : null),
            box, status, score, proposals?.Length, source, epoch);
    }

    internal static double IoU(Rect a, Rect b)
    {
        var left = Math.Max(a.X, b.X); var top = Math.Max(a.Y, b.Y);
        var right = Math.Min((long)a.X + a.Width, (long)b.X + b.Width);
        var bottom = Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height);
        var intersection = Math.Max(0, right - left) * (double)Math.Max(0, bottom - top);
        var union = a.Width * (double)a.Height + b.Width * (double)b.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }

    public static void DrawResult(Mat color, DroneTrackingResult result)
    {
        if (result.Box is { } box)
        {
            Cv2.Rectangle(color, box, result.BoxSource == "detector" ? new Scalar(45,190,90) : new Scalar(40,185,220), 2);
            Cv2.PutText(color, FormattableString.Invariant($"{result.BoxSource} lock {result.TrackEpoch}"),
                new Point(box.X, Math.Max(16, box.Y - 6)), HersheyFonts.HersheySimplex, 0.5, new Scalar(45,190,90), 1);
        }
    }

    private void Reset() { tracker?.Dispose(); tracker = null; lastVerified = null; }
    public void Dispose() { if (disposed) return; disposed = true; Reset(); }
}
