namespace Toboo.Core;

public sealed record FaceAimResult(TargetObservation Observation, TrackingCommand Command,
    FaceRegion? SelectedBox, double? FilteredX, double? FilteredY, SightAngles? SightError,
    double PanVelocity, double TiltVelocity, string Status, string Readiness, int LockEpoch,
    bool RequestPermitted, bool HasReference);

/// <summary>Single-threaded visual servoing requests. No transport, actuator output or shaft feedback.</summary>
public sealed class FaceAimController
{
    private readonly FaceAimSettings settings;
    private readonly FaceTargetLock targetLock;
    private readonly bool hasReference;
    private double pan, tilt, panVelocity, tiltVelocity;
    private double? filteredX, filteredY;
    private bool panSettled = true, tiltSettled = true;
    private long lastFrame = -1;
    private TimeSpan? lastNow, lastCapture, lastControl, lastFilter, lastSeen;
    private int width, height;

    public FaceAimController(FaceAimSettings settings, AimReference? reference = null)
    {
        settings.Validate();
        this.settings = settings;
        targetLock = new(settings);
        if (reference is not null && (!double.IsFinite(reference.PanDegrees) || !double.IsFinite(reference.TiltDegrees) ||
            reference.PanDegrees < settings.PanMinDegrees || reference.PanDegrees > settings.PanMaxDegrees ||
            reference.TiltDegrees < settings.TiltMinDegrees || reference.TiltDegrees > settings.TiltMaxDegrees))
            throw new ArgumentException("Reference must be inside the configured request limits.", nameof(reference));
        hasReference = reference is not null;
        pan = reference?.PanDegrees ?? (settings.PanMinDegrees + settings.PanMaxDegrees) / 2;
        tilt = reference?.TiltDegrees ?? (settings.TiltMinDegrees + settings.TiltMaxDegrees) / 2;
    }

    public FaceAimResult Step(IReadOnlyList<FaceRegion> faces, long frameNumber, TimeSpan capturedAt,
        int frameWidth, int frameHeight, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(faces);
        if (now < TimeSpan.Zero || (lastNow.HasValue && now < lastNow.Value))
            throw new ArgumentOutOfRangeException(nameof(now), "Use a monotonic clock.");
        if (frameWidth <= 0 || frameHeight <= 0 || frameNumber < 0 || capturedAt < TimeSpan.Zero ||
            faces.Any(f => f is null || !double.IsFinite(f.X) || !double.IsFinite(f.Y) ||
                !double.IsFinite(f.Width) || !double.IsFinite(f.Height) || f.Width <= 0 || f.Height <= 0 ||
                f.X < 0 || f.Y < 0 || f.X + f.Width > frameWidth || f.Y + f.Height > frameHeight))
            throw new ArgumentException("Face boxes must be finite and inside a positive frame.");
        lastNow = now;
        var observation = new TargetObservation(frameNumber, capturedAt, frameWidth, frameHeight, null, null);
        if (frameNumber <= lastFrame || (lastCapture.HasValue && capturedAt <= lastCapture.Value))
            return Hold(observation, TrackingState.Stale, "face_repeated_frame");
        lastFrame = frameNumber;
        if (capturedAt > now || (now - capturedAt).TotalSeconds > settings.MaxFrameAgeSeconds)
            return Hold(observation, TrackingState.Stale, "face_stale_frame");
        var timingGap = lastCapture.HasValue && ((capturedAt - lastCapture.Value).TotalSeconds > settings.MaxUpdateGapSeconds ||
            (lastControl.HasValue && (now - lastControl.Value).TotalSeconds > settings.MaxUpdateGapSeconds));
        if (timingGap || width != frameWidth || height != frameHeight)
        {
            targetLock.Reset();
            ResetMotion();
        }
        width = frameWidth; height = frameHeight; lastCapture = capturedAt;
        var selected = targetLock.Select(faces, capturedAt);
        if (selected is null)
            return Hold(observation, timingGap ? TrackingState.TimingGap : !lastSeen.HasValue ? TrackingState.Waiting :
                (now - lastSeen.Value).TotalSeconds >= settings.LockRetentionSeconds ? TrackingState.Lost : TrackingState.Missing,
                targetLock.Status);

        observation = observation with { CenterX = selected.CenterX, CenterY = selected.CenterY };
        lastSeen = now;
        var alpha = lastFilter.HasValue
            ? 1 - Math.Exp(-(capturedAt - lastFilter.Value).TotalSeconds / settings.FilterTimeConstantSeconds) : 1;
        filteredX = filteredX.HasValue ? filteredX + alpha * (selected.CenterX - filteredX) : selected.CenterX;
        filteredY = filteredY.HasValue ? filteredY + alpha * (selected.CenterY - filteredY) : selected.CenterY;
        lastFilter = capturedAt;
        var normalizedX = (filteredX.Value - width / 2.0) / width;
        var normalizedY = (filteredY.Value - height / 2.0) / height;
        SightAngles? angles = null;
        var readiness = settings.Camera is null ? "camera_geometry_required" : !settings.DirectionsConfirmed
            ? "mounting_directions_required" : !hasReference ? "request_reference_required" : "ready";
        if (settings.Camera is not null)
        {
            try { angles = settings.Camera.Project(filteredX.Value, filteredY.Value, width, height); }
            catch (ArgumentException) { readiness = "camera_geometry_mismatch"; }
        }
        var dt = lastControl.HasValue ? (now - lastControl.Value).TotalSeconds : 0;
        lastControl = now;
        if (readiness != "ready" || timingGap)
        {
            panVelocity = tiltVelocity = 0;
            return Result(timingGap ? TrackingState.TimingGap : TrackingState.Waiting, false);
        }
        if (settings.PanEnabled) MoveAxis(ref pan, ref panVelocity, ref panSettled, angles!.HorizontalDegrees,
            settings.PanDirection, settings.PanMinDegrees, settings.PanMaxDegrees, dt);
        if (settings.TiltEnabled) MoveAxis(ref tilt, ref tiltVelocity, ref tiltSettled, angles!.VerticalDegrees,
            settings.TiltDirection, settings.TiltMinDegrees, settings.TiltMaxDegrees, dt);
        return Result(panSettled && tiltSettled && panVelocity == 0 && tiltVelocity == 0
            ? TrackingState.Centered : TrackingState.Tracking, dt > 0);

        FaceAimResult Result(TrackingState state, bool permit) => new(observation,
            new(pan, tilt, state, normalizedX, normalizedY), selected, filteredX, filteredY, angles,
            panVelocity, tiltVelocity, targetLock.Status, readiness, targetLock.Epoch, permit, hasReference);
    }

    /// <summary>After an explicit transport hold, discard any unexecuted request lead. This is UART state, not shaft feedback.</summary>
    public void SynchronizeHeldRequest(AimReference reference)
    {
        if (!hasReference || !double.IsFinite(reference.PanDegrees) || !double.IsFinite(reference.TiltDegrees) ||
            reference.PanDegrees < settings.PanMinDegrees || reference.PanDegrees > settings.PanMaxDegrees ||
            reference.TiltDegrees < settings.TiltMinDegrees || reference.TiltDegrees > settings.TiltMaxDegrees)
            throw new ArgumentException("Invalid held request reference.", nameof(reference));
        pan = reference.PanDegrees; tilt = reference.TiltDegrees;
        panVelocity = tiltVelocity = 0;
    }

    private void MoveAxis(ref double angle, ref double velocity, ref bool settled,
        double error, int direction, double minimum, double maximum, double dt)
    {
        if (settled ? Math.Abs(error) > settings.DeadZoneExitDegrees : Math.Abs(error) <= settings.DeadZoneEnterDegrees)
            settled = !settled;
        var desired = settled ? 0 : Math.Clamp(direction * settings.GainPerSecond *
            (error - Math.Sign(error) * settings.DeadZoneEnterDegrees),
            -settings.MaxSpeedDegreesPerSecond, settings.MaxSpeedDegreesPerSecond);
        if (dt <= 0) return;
        var nextVelocity = velocity + Math.Clamp(desired - velocity,
            -settings.MaxAccelerationDegreesPerSecondSquared * dt, settings.MaxAccelerationDegreesPerSecondSquared * dt);
        var requested = angle + (velocity + nextVelocity) * 0.5 * dt;
        angle = Math.Clamp(requested, minimum, maximum);
        velocity = requested < minimum || requested > maximum ? 0 : nextVelocity;
    }

    private FaceAimResult Hold(TargetObservation observation, TrackingState state, string status)
    {
        ResetMotion();
        return new(observation, new(pan, tilt, state, null, null), null, null, null, null,
            0, 0, status, "fresh_confirmed_target_required", targetLock.Epoch, false, hasReference);
    }

    private void ResetMotion()
    {
        panVelocity = tiltVelocity = 0;
        panSettled = tiltSettled = true;
        filteredX = filteredY = null;
        lastControl = lastFilter = null;
    }
}
