using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PanTiltTracker.Core;

namespace PanTiltTracker.Desktop.ViewModels;

public sealed partial class FaceAimViewModel : ObservableObject
{
    private readonly string settingsFile;
    private FaceAimSettings baseline = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UseFov), nameof(UseIntrinsics))]
    private int geometryMode;
    [ObservableProperty] private int calibrationWidth = 640;
    [ObservableProperty] private int calibrationHeight = 480;
    [ObservableProperty] private double horizontalFov = 60;
    [ObservableProperty] private double verticalFov = 45;
    [ObservableProperty] private double fx = 554.26;
    [ObservableProperty] private double fy = 579.41;
    [ObservableProperty] private double cx = 320;
    [ObservableProperty] private double cy = 240;
    [ObservableProperty] private bool intrinsicsCalibrated;
    [ObservableProperty] private int panDirectionIndex;
    [ObservableProperty] private int tiltDirectionIndex = 1;
    [ObservableProperty] private bool directionsConfirmed;
    [ObservableProperty] private double deadZoneEnter = 1;
    [ObservableProperty] private double deadZoneExit = 1.8;
    [ObservableProperty] private double speedLimit = 20;
    [ObservableProperty] private int followAxesIndex = 2;
    [ObservableProperty] private bool trialRangeEnabled;
    [ObservableProperty] private int trialHalfRange = 5;
    [ObservableProperty] private string status = "先预览。视场角默认值只是示例，需填写本摄像头参数。";
    public bool UseFov => GeometryMode == 1;
    public bool UseIntrinsics => GeometryMode == 2;

    public FaceAimViewModel(string? localSettingsFile = null)
    {
        settingsFile = localSettingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PanTiltTracker", "face-aim.local.json");
        try
        {
            var path = File.Exists(settingsFile) ? settingsFile : Path.Combine(AppContext.BaseDirectory, "face-aim.json");
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            baseline = JsonSerializer.Deserialize<FaceAimSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("配置为空。");
            using var saved = JsonDocument.Parse(json);
            if (!saved.RootElement.TryGetProperty("TiltServoProfile", out var profile) || profile.ValueKind != JsonValueKind.String ||
                profile.GetString() != ServoViewModel.TiltProfileId)
            {
                baseline = baseline with { TiltMinDegrees = 45, TiltMaxDegrees = 90, DirectionsConfirmed = false };
                Status = "旧 S2 对准校准已作废；相机和 S1 方向保留，请重新确认新 S2 方向并保存。";
            }
            baseline.Validate();
            var camera = baseline.Camera;
            if (camera is not null)
            {
                CalibrationWidth = camera.Width; CalibrationHeight = camera.Height;
                Fx = camera.Fx; Fy = camera.Fy; Cx = camera.Cx; Cy = camera.Cy;
                IntrinsicsCalibrated = !camera.Approximate;
                // Centred FOV estimates should reopen as FOV inputs. Preserve off-centre models as intrinsics.
                if (camera.Approximate && Math.Abs(camera.Cx - camera.Width / 2.0) < 1e-6 &&
                    Math.Abs(camera.Cy - camera.Height / 2.0) < 1e-6)
                {
                    HorizontalFov = 2 * Math.Atan(camera.Width / (2 * camera.Fx)) * 180 / Math.PI;
                    VerticalFov = 2 * Math.Atan(camera.Height / (2 * camera.Fy)) * 180 / Math.PI;
                    GeometryMode = 1;
                }
                else GeometryMode = 2;
            }
            PanDirectionIndex = baseline.PanDirection == 1 ? 0 : 1;
            TiltDirectionIndex = baseline.TiltDirection == 1 ? 0 : 1;
            DirectionsConfirmed = baseline.DirectionsConfirmed;
            DeadZoneEnter = baseline.DeadZoneEnterDegrees; DeadZoneExit = baseline.DeadZoneExitDegrees;
            SpeedLimit = Math.Min(20, baseline.MaxSpeedDegreesPerSecond);
            FollowAxesIndex = baseline.PanEnabled && baseline.TiltEnabled ? 2 : baseline.PanEnabled ? 0
                : baseline.TiltEnabled ? 1 : throw new ArgumentException("至少需要选择一个跟随轴。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            baseline = new(); GeometryMode = 0; DirectionsConfirmed = false;
            Status = $"配置未载入，请重新设置：{error.Message}";
        }
    }

    partial void OnPanDirectionIndexChanged(int value) => DirectionsConfirmed = false;
    partial void OnTiltDirectionIndexChanged(int value) => DirectionsConfirmed = false;

    public FaceAimSettings Snapshot()
    {
        if (GeometryMode is < 0 or > 2 || PanDirectionIndex is < 0 or > 1 || TiltDirectionIndex is < 0 or > 1 ||
            FollowAxesIndex is < 0 or > 2)
            throw new ArgumentException("请选择相机参数和两轴方向。");
        var camera = GeometryMode switch
        {
            1 => CameraAngleModel.FromFieldOfView(CalibrationWidth, CalibrationHeight, HorizontalFov, VerticalFov),
            2 => new CameraAngleModel(CalibrationWidth, CalibrationHeight, Fx, Fy, Cx, Cy, !IntrinsicsCalibrated),
            _ => null
        };
        if (SpeedLimit > 20) throw new ArgumentException("当前跟随固件的速度上限为 20°/s。");
        var result = baseline with { Camera = camera, DirectionsConfirmed = DirectionsConfirmed,
            PanDirection = PanDirectionIndex == 0 ? 1 : -1, TiltDirection = TiltDirectionIndex == 0 ? 1 : -1,
            PanEnabled = FollowAxesIndex != 1, TiltEnabled = FollowAxesIndex != 0,
            DeadZoneEnterDegrees = DeadZoneEnter, DeadZoneExitDegrees = DeadZoneExit,
            MaxSpeedDegreesPerSecond = SpeedLimit };
        result.Validate();
        return result;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var snapshot = Snapshot();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsFile))!);
            var temporary = settingsFile + ".tmp";
            var saved = JsonSerializer.SerializeToNode(snapshot)!;
            saved["TiltServoProfile"] = ServoViewModel.TiltProfileId;
            File.WriteAllText(temporary, saved.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, settingsFile, overwrite: true);
            baseline = snapshot;
            Status = "已保存，下次开始生效。相机型号、裁剪或安装改变后需重新确认。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { Status = $"未保存：{error.Message}"; }
    }
}
