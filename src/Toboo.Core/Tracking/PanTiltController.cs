namespace Toboo.Core;

public sealed class PanTiltController
{
    private readonly ControllerSettings settings;
    private TimeSpan? lastStep;
    private TimeSpan? lastTargetStep;
    private TimeSpan? lastSeen;
    private long lastFrame = -1;
    private double pan;
    private double tilt;

    public PanTiltController(ControllerSettings settings)
    {
        settings.Validate();
        this.settings = settings;
        pan = settings.PanInitialDegrees;
        tilt = settings.TiltInitialDegrees;
    }

    // Call from one control thread. Null means that no new frame arrived this tick.
    public TrackingCommand Step(TargetObservation? observation, TimeSpan now)
    {
        if (now < TimeSpan.Zero || (lastStep.HasValue && now < lastStep.Value))
            throw new ArgumentOutOfRangeException(nameof(now), "Use a monotonic clock.");

        lastStep = now;

        if (observation is null || observation.FrameNumber <= lastFrame)
            return Hold(MissingState(now));

        lastFrame = observation.FrameNumber;
        if (observation.Width <= 0 || observation.Height <= 0)
            throw new ArgumentException("Frame dimensions must be positive.", nameof(observation));

        var age = (now - observation.CapturedAt).TotalSeconds;
        if (age < 0 || age > settings.MaxFrameAgeSeconds)
            return Hold(TrackingState.Stale);

        if (!observation.HasTarget)
            return Hold(MissingState(now));

        var x = observation.CenterX!.Value;
        var y = observation.CenterY!.Value;
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            x < 0 || x >= observation.Width || y < 0 || y >= observation.Height)
            throw new ArgumentException("Target center must be a finite point inside the frame.", nameof(observation));

        lastSeen = now;
        var dt = lastTargetStep.HasValue ? (now - lastTargetStep.Value).TotalSeconds : 0;
        lastTargetStep = now;
        var errorX = (x - observation.Width / 2.0) / observation.Width;
        var errorY = (y - observation.Height / 2.0) / observation.Height;
        if (dt > settings.MaxUpdateGapSeconds)
            return new(pan, tilt, TrackingState.TimingGap, errorX, errorY);

        var controlX = Math.Abs(errorX) <= settings.DeadZone ? 0 : errorX;
        var controlY = Math.Abs(errorY) <= settings.DeadZone ? 0 : errorY;
        pan = Move(pan, controlX, settings.PanGain, settings.PanDirection,
            settings.PanMinDegrees, settings.PanMaxDegrees, dt);
        tilt = Move(tilt, controlY, settings.TiltGain, settings.TiltDirection,
            settings.TiltMinDegrees, settings.TiltMaxDegrees, dt);

        return new(pan, tilt,
            controlX == 0 && controlY == 0 ? TrackingState.Centered : TrackingState.Tracking,
            errorX, errorY);
    }

    private double Move(double angle, double error, double gain, int direction,
        double minimum, double maximum, double dt)
    {
        var speed = Math.Clamp(direction * gain * error,
            -settings.MaxSpeedDegreesPerSecond, settings.MaxSpeedDegreesPerSecond);
        return Math.Clamp(angle + speed * dt, minimum, maximum);
    }

    private TrackingState MissingState(TimeSpan now) => !lastSeen.HasValue
        ? TrackingState.Waiting
        : (now - lastSeen.Value).TotalSeconds >= settings.LostTimeoutSeconds
            ? TrackingState.Lost
            : TrackingState.Missing;

    private TrackingCommand Hold(TrackingState state) => new(pan, tilt, state, null, null);
}
