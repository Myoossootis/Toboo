namespace Toboo.Core;

public sealed record ControllerSettings
{
    public double PanInitialDegrees { get; init; } = 90;
    public double TiltInitialDegrees { get; init; } = 90;
    public double PanMinDegrees { get; init; } = 60;
    public double PanMaxDegrees { get; init; } = 120;
    public double TiltMinDegrees { get; init; } = 70;
    public double TiltMaxDegrees { get; init; } = 110;
    public int PanDirection { get; init; } = 1;
    public int TiltDirection { get; init; } = 1;
    public double PanGain { get; init; } = 80;
    public double TiltGain { get; init; } = 80;
    public double MaxSpeedDegreesPerSecond { get; init; } = 30;
    public double DeadZone { get; init; } = 0.02;
    public double LostTimeoutSeconds { get; init; } = 0.5;
    public double MaxFrameAgeSeconds { get; init; } = 0.2;
    public double MaxUpdateGapSeconds { get; init; } = 0.15;

    public void Validate()
    {
        double[] values = [PanInitialDegrees, TiltInitialDegrees, PanMinDegrees,
            PanMaxDegrees, TiltMinDegrees, TiltMaxDegrees, PanGain, TiltGain,
            MaxSpeedDegreesPerSecond, DeadZone, LostTimeoutSeconds,
            MaxFrameAgeSeconds, MaxUpdateGapSeconds];
        if (values.Any(value => !double.IsFinite(value)) ||
            PanMinDegrees < 0 || PanMaxDegrees > 180 || PanMinDegrees >= PanMaxDegrees ||
            TiltMinDegrees < 0 || TiltMaxDegrees > 180 || TiltMinDegrees >= TiltMaxDegrees ||
            PanInitialDegrees < PanMinDegrees || PanInitialDegrees > PanMaxDegrees ||
            TiltInitialDegrees < TiltMinDegrees || TiltInitialDegrees > TiltMaxDegrees ||
            PanDirection is not (-1 or 1) || TiltDirection is not (-1 or 1) ||
            PanGain <= 0 || TiltGain <= 0 || MaxSpeedDegreesPerSecond <= 0 ||
            DeadZone < 0 || DeadZone >= 0.5 || LostTimeoutSeconds <= 0 ||
            MaxFrameAgeSeconds <= 0 || MaxUpdateGapSeconds <= 0)
        {
            throw new ArgumentException("Invalid controller settings. Check limits, signs, gains and timing.");
        }
    }
}
