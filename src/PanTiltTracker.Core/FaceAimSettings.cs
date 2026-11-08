namespace PanTiltTracker.Core;

/// <summary>Preliminary host-side settings; firmware limits remain independently authoritative.</summary>
public sealed record FaceAimSettings
{
    public CameraAngleModel? Camera { get; init; }
    public bool DirectionsConfirmed { get; init; }
    public int PanDirection { get; init; } = 1;
    public int TiltDirection { get; init; } = 1;
    public bool PanEnabled { get; init; } = true;
    public bool TiltEnabled { get; init; } = true;
    public double PanMinDegrees { get; init; } = 30;
    public double PanMaxDegrees { get; init; } = 175;
    public double TiltMinDegrees { get; init; } = 35;
    public double TiltMaxDegrees { get; init; } = 65;
    public int ConfirmationFrames { get; init; } = 3;
    public double LockRetentionSeconds { get; init; } = 0.4;
    public double FilterTimeConstantSeconds { get; init; } = 0.10;
    public double DeadZoneEnterDegrees { get; init; } = 1;
    public double DeadZoneExitDegrees { get; init; } = 1.8;
    public double GainPerSecond { get; init; } = 3;
    public double MaxSpeedDegreesPerSecond { get; init; } = 20;
    public double MaxAccelerationDegreesPerSecondSquared { get; init; } = 80;
    public double MaxFrameAgeSeconds { get; init; } = 0.2;
    public double MaxUpdateGapSeconds { get; init; } = 0.15;

    public void Validate()
    {
        Camera?.Validate();
        double[] values = [PanMinDegrees, PanMaxDegrees, TiltMinDegrees, TiltMaxDegrees,
            LockRetentionSeconds, FilterTimeConstantSeconds, DeadZoneEnterDegrees, DeadZoneExitDegrees,
            GainPerSecond, MaxSpeedDegreesPerSecond, MaxAccelerationDegreesPerSecondSquared,
            MaxFrameAgeSeconds, MaxUpdateGapSeconds];
        if (values.Any(v => !double.IsFinite(v)) || PanMinDegrees < 0 || PanMaxDegrees > 180 ||
            PanMinDegrees >= PanMaxDegrees || TiltMinDegrees < 0 || TiltMaxDegrees > 180 ||
            TiltMinDegrees >= TiltMaxDegrees || PanDirection is not (-1 or 1) || TiltDirection is not (-1 or 1) ||
            ConfirmationFrames < 1 || LockRetentionSeconds <= 0 || FilterTimeConstantSeconds <= 0 ||
            DeadZoneEnterDegrees < 0 || DeadZoneExitDegrees <= DeadZoneEnterDegrees ||
            GainPerSecond <= 0 || MaxSpeedDegreesPerSecond <= 0 ||
            MaxAccelerationDegreesPerSecondSquared <= 0 || MaxFrameAgeSeconds <= 0 || MaxUpdateGapSeconds <= 0)
            throw new ArgumentException("Invalid face aiming settings.");
    }
}

/// <summary>Explicit last requested angles at arm time, NEVER a measurement of shaft position.</summary>
public sealed record AimReference(double PanDegrees, double TiltDegrees);
