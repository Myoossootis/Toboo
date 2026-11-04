namespace PanTiltTracker.Core;

public readonly record struct EvaluationBox(double X, double Y, double Width, double Height)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) &&
        double.IsFinite(Height) && Width > 0 && Height > 0 && double.IsFinite(Width * Height) && double.IsFinite(X + Width) && double.IsFinite(Y + Height);
    public double IoU(EvaluationBox other)
    {
        var intersection = Math.Max(0, Math.Min(X + Width, other.X + other.Width) - Math.Max(X, other.X)) *
            Math.Max(0, Math.Min(Y + Height, other.Y + other.Height) - Math.Max(Y, other.Y));
        return intersection / (Width * Height + other.Width * other.Height - intersection);
    }
}

public sealed record EvaluationFrame(long Number, EvaluationBox? Reference, EvaluationBox? Prediction);
public sealed record TrackingEvaluationSummary(int Frames, int PositiveFrames, int NegativeFrames,
    int TruePositives, int FalsePositives, int FalseNegatives, double? Precision, double? Recall,
    double? MeanIoUOnVisibleFrames, double? MeanCenterErrorOnReportedVisibleFramesPx,
    int FalsePositiveFramesOnAbsentTarget, int FalseAlarmEvents, double FalseAlarmEventsPerMinute,
    long? InitialAcquisitionFrame, int LongestMissingRunFrames, int LostEpisodes, int UnrecoveredEpisodes,
    double? MeanReacquisitionSeconds, double IouThreshold, double NominalFps);

/// <summary>Evaluation of the selected target per frame, not every detector proposal or a multi-object AP benchmark.</summary>
public static class TrackingEvaluation
{
    public static TrackingEvaluationSummary Evaluate(IReadOnlyList<EvaluationFrame> frames, double fps, double iouThreshold = 0.5)
    {
        if (frames.Count == 0 || !double.IsFinite(fps) || fps <= 0 || !double.IsFinite(iouThreshold) || iouThreshold is <= 0 or > 1)
            throw new ArgumentException("Expected nonempty aligned frames, positive nominal FPS and IoU in (0,1].");
        var tp = 0; var fp = 0; var fn = 0; var positives = 0; var falseAbsent = 0; var alarms = 0;
        var iouSum = 0.0; var centerSum = 0.0; var centerCount = 0;
        var missing = 0; var longest = 0; var lost = 0; var unrecovered = 0;
        var delays = new List<double>(); long? acquired = null; long? lostAt = null;
        var previousMatched = false; var previousFalseAlarm = false;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f.Number != i || (f.Reference is { } r && !r.IsValid) || (f.Prediction is { } p && !p.IsValid))
                throw new ArgumentException("Frames must be contiguous from zero with finite positive-size boxes.");
            var matched = false; var falseAlarm = false;
            if (f.Reference is { } reference)
            {
                positives++;
                if (f.Prediction is { } prediction)
                {
                    var iou = reference.IoU(prediction); iouSum += iou;
                    var dx = prediction.X + prediction.Width / 2 - reference.X - reference.Width / 2;
                    var dy = prediction.Y + prediction.Height / 2 - reference.Y - reference.Height / 2;
                    centerSum += Math.Sqrt(dx * dx + dy * dy); centerCount++;
                    matched = iou >= iouThreshold;
                    if (matched) tp++; else { fp++; fn++; }
                }
                else fn++;
                if (matched)
                {
                    acquired ??= f.Number;
                    if (lostAt is { } start) { delays.Add((f.Number - start) / fps); lostAt = null; }
                    missing = 0;
                }
                else
                {
                    longest = Math.Max(longest, ++missing);
                    if (previousMatched) { lost++; lostAt = f.Number; }
                }
            }
            else
            {
                if (f.Prediction is not null) { fp++; falseAbsent++; falseAlarm = true; }
                if (lostAt is not null) { unrecovered++; lostAt = null; }
                missing = 0;
            }
            if (falseAlarm && !previousFalseAlarm) alarms++;
            previousFalseAlarm = falseAlarm; previousMatched = matched;
        }
        if (lostAt is not null) unrecovered++;
        return new(frames.Count, positives, frames.Count - positives, tp, fp, fn,
            tp + fp == 0 ? null : tp / (double)(tp + fp), positives == 0 ? null : tp / (double)positives,
            positives == 0 ? null : iouSum / positives, centerCount == 0 ? null : centerSum / centerCount,
            falseAbsent, alarms, alarms / (frames.Count / fps / 60), acquired, longest, lost, unrecovered,
            delays.Count == 0 ? null : delays.Average(), iouThreshold, fps);
    }
}
