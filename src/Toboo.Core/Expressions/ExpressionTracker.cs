namespace Toboo.Core;

// Order matches the pinned OpenCV Zoo model. These are visible-expression categories, not diagnoses.
public enum FacialExpression { Angry, Disgust, Fearful, Happy, Neutral, Sad, Surprised, Unknown }

public sealed record ExpressionEstimate(FacialExpression Label, double Score, double Margin,
    string Reason, TimeSpan? CapturedAt, double[] Scores)
{
    public static ExpressionEstimate Unknown(string reason) => new(FacialExpression.Unknown, 0, 0, reason, null, []);
    public string DisplayName => Label switch {
        FacialExpression.Angry => "皱眉 / 愤怒表情", FacialExpression.Disgust => "厌恶表情",
        FacialExpression.Fearful => "恐惧表情", FacialExpression.Happy => "微笑 / 愉快表情",
        FacialExpression.Neutral => "中性表情", FacialExpression.Sad => "悲伤表情",
        FacialExpression.Surprised => "惊讶表情", _ => "暂不判断" };
    public string Emoticon => Label switch {
        FacialExpression.Happy => "(^_^)", FacialExpression.Surprised => "(o_o)",
        FacialExpression.Sad => "(T_T)", FacialExpression.Angry => "(>_<)",
        FacialExpression.Disgust => "(-_-)" , FacialExpression.Fearful => "(O_O)",
        FacialExpression.Neutral => "(._.)", _ => "(?)" };
}

public sealed record ExpressionSettings
{
    public double MinimumScore { get; init; } = 0.60;
    public double MinimumMargin { get; init; } = 0.15;
    public double ConfirmationSeconds { get; init; } = 0.35;
    public double FilterSeconds { get; init; } = 0.25;
    public double MaximumAgeSeconds { get; init; } = 0.50;
    public double MaximumGapSeconds { get; init; } = 0.50;
    public void Validate()
    {
        double[] values = [MinimumScore, MinimumMargin, ConfirmationSeconds, FilterSeconds, MaximumAgeSeconds, MaximumGapSeconds];
        if (values.Any(v => !double.IsFinite(v)) || MinimumScore is <= 0 or > 1 || MinimumMargin is < 0 or > 1 ||
            ConfirmationSeconds <= 0 || FilterSeconds <= 0 || MaximumAgeSeconds <= 0 || MaximumGapSeconds <= 0)
            throw new ArgumentException("Invalid expression settings.");
    }
}

public sealed record ExpressionAnalysis(ExpressionEstimate Estimate, double InferenceMilliseconds = 0,
    double? Brightness = null, double? Sharpness = null, int TargetEpoch = 0, bool WasInferred = false);

/// <summary>Single-target time-aware smoothing. Unknown input clears the old expression immediately.</summary>
public sealed class ExpressionTracker
{
    private readonly ExpressionSettings settings;
    private double[]? filtered;
    private TimeSpan? lastSample, candidateSince, lastNow;
    private long lastFrame = -1;
    private int epoch = -1;
    private FacialExpression candidate = FacialExpression.Unknown;
    public ExpressionEstimate Current { get; private set; } = ExpressionEstimate.Unknown("waiting");
    public ExpressionTracker(ExpressionSettings? settings = null)
    {
        this.settings = settings ?? new(); this.settings.Validate();
    }
    public ExpressionEstimate Clear(string reason)
    {
        filtered = null; lastSample = candidateSince = null; candidate = FacialExpression.Unknown;
        Current = ExpressionEstimate.Unknown(reason); return Current;
    }
    public ExpressionEstimate At(TimeSpan now)
    {
        CheckClock(now);
        return lastSample is { } sample && (now < sample || (now - sample).TotalSeconds > settings.MaximumAgeSeconds)
            ? Clear("stale") : Current;
    }
    public ExpressionEstimate Update(double[] probabilities, long frame, int targetEpoch, TimeSpan capturedAt, TimeSpan now, bool singleImage = false)
    {
        CheckClock(now);
        if (frame <= lastFrame) return Clear("repeated_frame");
        lastFrame = frame;
        if (capturedAt < TimeSpan.Zero || capturedAt > now || (now - capturedAt).TotalSeconds > settings.MaximumAgeSeconds)
            return Clear("stale");
        if (probabilities.Length != 7 || probabilities.Any(p => !double.IsFinite(p) || p is < 0 or > 1) ||
            Math.Abs(probabilities.Sum() - 1) > 0.001) return Clear("invalid_scores");
        if (lastSample is { } old && capturedAt <= old) return Clear("repeated_sample");
        if (epoch != targetEpoch || lastSample is { } previous && (capturedAt - previous).TotalSeconds > settings.MaximumGapSeconds)
            Clear("target_changed_or_gap");
        epoch = targetEpoch;
        var alpha = lastSample is { } last ? 1 - Math.Exp(-(capturedAt - last).TotalSeconds / settings.FilterSeconds) : 1;
        filtered = filtered is null ? [.. probabilities] : filtered.Select((v, i) => v + alpha * (probabilities[i] - v)).ToArray();
        lastSample = capturedAt;
        var ranked = Enumerable.Range(0, 7).OrderByDescending(i => filtered[i]).ToArray();
        var winner = (FacialExpression)ranked[0];
        var score = filtered[ranked[0]]; var margin = score - filtered[ranked[1]];
        // Both the fresh model output and smoothed score must be decisive. Old certainty cannot hide a new ambiguous frame.
        var raw = probabilities.OrderDescending().ToArray();
        if (score < settings.MinimumScore || margin < settings.MinimumMargin || raw[0] < settings.MinimumScore ||
            raw[0] - raw[1] < settings.MinimumMargin || Array.IndexOf(probabilities, raw[0]) != ranked[0])
        {
            candidate = FacialExpression.Unknown; candidateSince = null;
            Current = new(FacialExpression.Unknown, score, margin, "uncertain", capturedAt, [.. filtered]);
            return Current;
        }
        if (winner != candidate) { candidate = winner; candidateSince = capturedAt; }
        var confirmed = singleImage || (capturedAt - candidateSince!.Value).TotalSeconds >= settings.ConfirmationSeconds;
        Current = new(confirmed ? winner : FacialExpression.Unknown, score, margin,
            confirmed ? singleImage ? "single_image" : "stable" : "confirming", capturedAt, [.. filtered]);
        return Current;
    }
    private void CheckClock(TimeSpan now)
    {
        if (now < TimeSpan.Zero || lastNow is { } previous && now < previous) throw new ArgumentException("Use a monotonic clock.");
        lastNow = now;
    }
    public static double[] Softmax(IReadOnlyList<float> logits)
    {
        if (logits.Count != 7 || logits.Any(x => !float.IsFinite(x))) throw new ArgumentException("Expected seven finite logits.");
        var maximum = logits.Max();
        var values = logits.Select(x => Math.Exp((double)x - maximum)).ToArray();
        var sum = values.Sum(); return values.Select(x => x / sum).ToArray();
    }
}
