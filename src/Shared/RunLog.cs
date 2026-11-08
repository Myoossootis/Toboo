using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using PanTiltTracker.Core;

namespace PanTiltTracker;

internal sealed class RunLog : IDisposable
{
    private readonly StreamWriter csv;

    public RunLog(string directory, string mode, int markerId, ControllerSettings settings,
        string algorithm = "aruco", object? experiment = null, string hardwareOutput = "disabled")
    {
        Directory.CreateDirectory(directory);
        csv = new StreamWriter(Path.Combine(directory, "telemetry.csv"));
        csv.WriteLine("frame,time_s,capture_time_s,detected,width,height,u_px,v_px,error_x,error_y,pan_command_deg,tilt_command_deg,state,algorithm_ms,box_x,box_y,box_width,box_height,vision_status,candidate_count,target_score,target_class,target_source,track_epoch,face_filtered_u_px,face_filtered_v_px,sight_pan_error_deg,sight_tilt_error_deg,pan_request_velocity_deg_s,tilt_request_velocity_deg_s,face_lock_epoch,aim_readiness,aim_request_permitted,aim_has_reference");
        File.WriteAllText(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(new
        {
            startedUtc = DateTimeOffset.UtcNow,
            mode,
            simulated = mode == "simulate",
            hardwareOutput,
            algorithm,
            dictionary = algorithm == "aruco" ? "DICT_4X4_50" : null,
            markerId = algorithm == "aruco" ? (int?)markerId : null,
            experiment,
            controller = settings,
            note = "Angles are commands, not measured shaft angles. Arrival timestamps exclude device-side buffering."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Write(TargetObservation observation, TrackingCommand command, TimeSpan now, double algorithmMs,
        Rect? box = null, string visionStatus = "aruco_detection", int? candidateCount = null,
        double? targetScore = null, string? targetClass = null, string? targetSource = null, int? trackEpoch = null,
        FaceAimResult? faceAim = null)
    {
        csv.WriteLine(string.Join(',',
            observation.FrameNumber.ToString(CultureInfo.InvariantCulture), Number(now.TotalSeconds),
            Number(observation.CapturedAt.TotalSeconds), observation.HasTarget ? "1" : "0",
            observation.Width.ToString(CultureInfo.InvariantCulture), observation.Height.ToString(CultureInfo.InvariantCulture),
            Number(observation.CenterX), Number(observation.CenterY), Number(command.ErrorX), Number(command.ErrorY),
            Number(faceAim is { HasReference: false } ? null : command.PanDegrees),
            Number(faceAim is { HasReference: false } ? null : command.TiltDegrees), command.State, Number(algorithmMs),
            Number(box?.X), Number(box?.Y), Number(box?.Width), Number(box?.Height), visionStatus, Number(candidateCount),
            Number(targetScore), targetClass ?? "", targetSource ?? "", Number(trackEpoch),
            Number(faceAim?.FilteredX), Number(faceAim?.FilteredY), Number(faceAim?.SightError?.HorizontalDegrees),
            Number(faceAim?.SightError?.VerticalDegrees), Number(faceAim?.PanVelocity), Number(faceAim?.TiltVelocity),
            Number(faceAim is { SelectedBox: not null } ? faceAim.LockEpoch : null),
            faceAim?.Readiness ?? "", faceAim is null ? "" : faceAim.RequestPermitted ? "1" : "0",
            faceAim is null ? "" : faceAim.HasReference ? "1" : "0"));
    }

    private static string Number(double? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
    public void Dispose() => csv.Dispose();
}
