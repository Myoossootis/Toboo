using OpenCvSharp;
using PanTiltTracker.Core;

namespace PanTiltTracker.Vision;

/// <summary>Optional 5Hz classifier bound to the already selected spatial face, never to a second larger face.</summary>
public sealed class FaceExpressionSession : IDisposable
{
    private readonly FacialExpressionRecognizer? recognizer;
    private readonly ExpressionTracker tracker = new();
    private readonly string? unavailable;
    private TimeSpan? lastAttempt;
    private int epoch = -1;
    private ExpressionAnalysis latest = new(ExpressionEstimate.Unknown("waiting"));
    public const double IntervalSeconds = 0.2;
    public FaceExpressionSession(string? path = null)
    {
        try { recognizer = new(path); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OpenCVException)
        { unavailable = "model_unavailable"; Diagnostic = error.Message; }
    }
    public string? Diagnostic { get; private set; }
    public ExpressionAnalysis Analyze(Mat image, FaceDetectionResult faces, FaceAimResult? aim, TimeSpan now, bool singleImage = false, bool realTime = false)
    {
        var captured = faces.Observation.CapturedAt;
        ExpressionAnalysis Clear(string reason) => latest = new(tracker.Clear(reason), TargetEpoch: epoch);
        if (unavailable is not null) return Clear(unavailable);
        var target = aim?.SelectedBox;
        var box = aim is null ? faces.PrimaryBox : target is not null && aim.Observation.HasTarget
            ? new Rect((int)target.X, (int)target.Y, (int)target.Width, (int)target.Height) : (Rect?)null;
        if (box is null) { lastAttempt = null; return Clear("no_selected_face"); }
        if (captured > now || (now - captured).TotalSeconds > 0.2) return Clear("stale");
        var selected = faces.Landmarks?.FirstOrDefault(l => l.Box == box.Value);
        if (selected is null) return Clear("landmarks_invalid");
        var targetEpoch = aim?.LockEpoch ?? 0;
        if (targetEpoch != epoch) { epoch = targetEpoch; lastAttempt = null; Clear("target_changed"); }
        if (lastAttempt is { } previous && captured >= previous && (captured - previous).TotalSeconds < IntervalSeconds)
            return latest with { Estimate = tracker.At(now), WasInferred = false };
        lastAttempt = captured;
        try
        {
            var result = recognizer!.Infer(image, selected);
            if (result.Scores is null) return Clear(result.Reason) with {
                InferenceMilliseconds = result.InferenceMilliseconds, Brightness = result.Brightness, Sharpness = result.Sharpness };
            // Native inference may finish after the freshness budget; never show its late result as current.
            var completed = realTime ? now + TimeSpan.FromMilliseconds(result.InferenceMilliseconds) : now;
            latest = new(tracker.Update(result.Scores, faces.Observation.FrameNumber, epoch, captured, completed, singleImage),
                result.InferenceMilliseconds, result.Brightness, result.Sharpness, epoch, true);
            return latest;
        }
        catch (Exception error) when (error is OpenCVException or ArgumentException or InvalidDataException)
        { Diagnostic = error.Message; return Clear("inference_error"); }
    }
    public void Dispose() => recognizer?.Dispose();
}
