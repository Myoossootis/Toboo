namespace PanTiltTracker.Core;

public sealed record FaceRegion(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public double Area => Width * Height;
}

/// <summary>Spatial association only, not biometric identity recognition. Uses current detections only.</summary>
internal sealed class FaceTargetLock(FaceAimSettings settings)
{
    private FaceRegion? previous;
    private TimeSpan lastMatched;
    private int confirmations;
    private bool locked;
    public int Epoch { get; private set; }
    public string Status { get; private set; } = "no_face_detected";

    public void Reset()
    {
        previous = null;
        confirmations = 0;
        locked = false;
    }

    public FaceRegion? Select(IReadOnlyList<FaceRegion> faces, TimeSpan capturedAt)
    {
        if (previous is not null && (capturedAt - lastMatched).TotalSeconds > settings.LockRetentionSeconds)
            Reset();
        var match = previous is null ? null : faces.Where(f => Matches(previous, f))
            .OrderBy(f => Distance(previous, f)).ThenByDescending(f => f.Area).FirstOrDefault();
        if (previous is not null && match is null && locked)
        {
            Status = faces.Count == 0 ? "no_face_detected" : "face_lock_missing";
            return null; // Do not jump to another person's larger face or reuse the old box.
        }
        if (match is null)
        {
            confirmations = 0;
            match = faces.OrderByDescending(f => f.Area).ThenBy(f => f.X).ThenBy(f => f.Y).FirstOrDefault();
        }
        if (match is null)
        {
            previous = null;
            Status = "no_face_detected";
            return null;
        }
        previous = match;
        lastMatched = capturedAt;
        if (!locked && ++confirmations < settings.ConfirmationFrames)
        {
            Status = "face_confirming";
            return null;
        }
        if (!locked) { locked = true; Epoch++; }
        Status = "face_locked";
        return match;
    }

    private static double Distance(FaceRegion a, FaceRegion b) =>
        Math.Pow(a.CenterX - b.CenterX, 2) + Math.Pow(a.CenterY - b.CenterY, 2);

    private static bool Matches(FaceRegion a, FaceRegion b)
    {
        var ratio = b.Area / a.Area;
        if (ratio < 0.25 || ratio > 4) return false;
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) *
            Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / (a.Area + b.Area - intersection) >= 0.15 ||
            Distance(a, b) <= 0.25 * (a.Width * a.Width + a.Height * a.Height);
    }
}
