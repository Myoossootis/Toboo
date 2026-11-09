namespace Toboo.Core;

public sealed record TargetObservation(
    long FrameNumber,
    TimeSpan CapturedAt,
    int Width,
    int Height,
    double? CenterX,
    double? CenterY)
{
    public bool HasTarget => CenterX is not null && CenterY is not null;
}

public enum TrackingState
{
    Waiting,
    Tracking,
    Centered,
    Missing,
    Lost,
    Stale,
    TimingGap
}

public sealed record TrackingCommand(
    double PanDegrees,
    double TiltDegrees,
    TrackingState State,
    double? ErrorX,
    double? ErrorY);
