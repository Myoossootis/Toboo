using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PanTiltTracker.Shared;

namespace PanTiltTracker.Desktop.ViewModels;

public sealed partial class ServoViewModel : ObservableObject
{
    private const int PanLowerLimit = 30, PanUpperLimit = 175, TiltMin = 35, TiltMax = 65;
    [ObservableProperty] private int panMin = PanLowerLimit;
    [ObservableProperty] private int panMax = PanUpperLimit;
    private readonly string calibrationFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PanTiltTracker", "servo.json");
    private bool readyFirmware;
    private CancellationTokenSource? sweepCancellation;
    private int? panLast, tiltLast;

    [ObservableProperty] private string port = "COM6";
    [ObservableProperty] private bool ready;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool sweepReady;
    [ObservableProperty] private int tiltFloor = TiltMin;
    [ObservableProperty] private int tiltCeiling = TiltMax;
    [ObservableProperty] private string status = "先读取串口状态。";

    public bool CanControl => readyFirmware && Ready && !Busy;
    public bool CanStopSweep => Busy && sweepCancellation is { IsCancellationRequested: false };
    public bool CanPanDown => CanControl && panLast > PanMin;
    public bool CanPanUp => CanControl && panLast < PanMax;
    public bool CanTiltDown => CanControl && tiltLast > TiltFloor;
    public bool CanTiltUp => CanControl && tiltLast < TiltCeiling && tiltLast >= TiltFloor - 1;
    public string LastRequests => $"末次指令 · S1 {AngleLabel(panLast)} / S2 {AngleLabel(tiltLast)}";
    public int CenterPan => (PanMin + PanMax + 1) / 2;
    public int CenterTilt => (TiltFloor + TiltCeiling + 1) / 2;
    public string CenterLabel => $"中心指令 · S1 {CenterPan}° / S2 {CenterTilt}°";
    public bool CanPattern => CanControl && SweepReady;

    public ServoViewModel()
    {
        try
        {
            if (!File.Exists(calibrationFile)) return;
            var saved = JsonSerializer.Deserialize<Calibration>(File.ReadAllText(calibrationFile));
            if (saved is null || saved.TiltFloor < TiltMin || saved.TiltCeiling > TiltMax || saved.TiltFloor >= saved.TiltCeiling ||
                saved.PanFloor < PanLowerLimit || saved.PanCeiling > PanUpperLimit || saved.PanFloor >= saved.PanCeiling)
                throw new InvalidDataException("回位角度或下限超出试调范围。");
            PanMin = saved.PanFloor;
            PanMax = saved.PanCeiling;
            TiltFloor = saved.TiltFloor;
            TiltCeiling = saved.TiltCeiling;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Status = "无法读取上次的校准，已显示默认值。";
        }
    }

    partial void OnReadyChanged(bool value)
    {
        if (!value) SweepReady = false;
        RefreshControls();
    }
    partial void OnBusyChanged(bool value) => RefreshControls();
    partial void OnSweepReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPattern));
    }
    partial void OnTiltFloorChanged(int value) => RefreshControls();
    partial void OnTiltCeilingChanged(int value) => RefreshControls();
    partial void OnPanMinChanged(int value) => RefreshControls();
    partial void OnPanMaxChanged(int value) => RefreshControls();
    partial void OnPortChanged(string value)
    {
        readyFirmware = false;
        panLast = tiltLast = null;
        Ready = false;
        Status = "串口已修改，请重新读取状态。";
        RefreshControls();
    }

    [RelayCommand] private Task InspectAsync() => SendAsync("inspect", [], "已读取两轴指令状态。");

    [RelayCommand]
    private Task HomeAsync()
    {
        if (!CanControl) return Task.CompletedTask;
        if (!TryCalibration(out var calibration)) return Task.CompletedTask;
        return RunMotionAsync("home", BoundaryArguments(calibration), "已平滑回到边界矩形中心。");
    }

    [RelayCommand] private Task PanDownAsync() => NudgeAsync("S1", -1, CanPanDown);
    [RelayCommand] private Task PanUpAsync() => NudgeAsync("S1", 1, CanPanUp);
    [RelayCommand] private Task TiltDownAsync() => NudgeAsync("S2", -1, CanTiltDown);
    [RelayCommand] private Task TiltUpAsync() => NudgeAsync("S2", 1, CanTiltUp);

    [RelayCommand]
    private Task PatternAsync()
    {
        if (!CanPattern || !TryCalibration(out var calibration)) return Task.CompletedTask;
        SweepReady = false;
        return RunMotionAsync("pattern", BoundaryArguments(calibration), "矩形与交叉对角线扫动完成，已回中心。");
    }

    private static string[] BoundaryArguments(Calibration calibration) =>
        ["--left", Number(calibration.PanFloor), "--right", Number(calibration.PanCeiling),
         "--bottom", Number(calibration.TiltFloor), "--top", Number(calibration.TiltCeiling), "--execute"];

    private async Task RunMotionAsync(string action, string[] arguments, string success)
    {
        using var cancellation = new CancellationTokenSource();
        sweepCancellation = cancellation;
        try { await SendAsync(action, arguments, success, cancellation.Token); }
        finally { sweepCancellation = null; OnPropertyChanged(nameof(CanStopSweep)); }
    }

    [RelayCommand]
    private void StopSweep()
    {
        if (!CanStopSweep) return;
        sweepCancellation!.Cancel();
        Status = "正在停止扫动…";
        OnPropertyChanged(nameof(CanStopSweep));
    }

    [RelayCommand]
    private void SaveCalibration()
    {
        if (Busy || !TryCalibration(out var calibration)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(calibrationFile)!);
            var temporary = calibrationFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(calibration));
            File.Move(temporary, calibrationFile, overwrite: true);
            Status = $"已保存边界。{CenterLabel}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status = $"保存失败：{error.Message}";
        }
    }

    private bool TryCalibration(out Calibration calibration)
    {
        if (PanMin >= PanLowerLimit && PanMax <= PanUpperLimit && PanMin < PanMax &&
            TiltFloor >= TiltMin && TiltCeiling <= TiltMax && TiltFloor < TiltCeiling)
        {
            calibration = new(CenterPan, CenterTilt, TiltFloor, PanMin, PanMax, TiltCeiling);
            return true;
        }
        Status = "请输入有效边界。S1 30–175°，S2 35–65°，下限须小于上限。";
        calibration = null!;
        return false;
    }

    private Task NudgeAsync(string axis, int delta, bool enabled) => enabled
        ? SendAsync("nudge", ["--axis", axis, "--delta", Number(delta), "--tilt-floor", Number(TiltFloor), "--execute"],
            $"{axis} 微调指令已完成。")
        : Task.CompletedTask;

    private async Task SendAsync(string action, string[] arguments, string success, CancellationToken cancellationToken = default)
    {
        if (Busy) return;
        var serialPort = (Port ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(serialPort, @"\ACOM[1-9][0-9]{0,3}\z"))
        {
            Status = "请输入 Windows 串口号，例如 COM6。";
            return;
        }
        Busy = true;
        Status = action switch
        {
            "inspect" => "正在读取状态…",
            "home" => "两轴正在平滑快速回到矩形中心…",
            "pattern" => "正在走矩形与交叉对角线路径，结束后回中心…",
            "sweep" => arguments[1] == "S2" ? "S2 正前往 40°，再扫向 65°…" : "S1 正前往 45°，再扫向 90°…",
            _ => "正在发送指令…"
        };
        try
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PanTiltTracker", "servo-logs");
            Directory.CreateDirectory(logDirectory);
            var logFile = Path.Combine(logDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fffffff}-{action}.jsonl");
            var result = await ServoTool.RunAsync(["--port", serialPort, "--action", action, "--log", logFile, .. arguments], cancellationToken);
            if (result.ExitCode != 0) throw new IOException(result.Error.Trim());
            using var data = JsonDocument.Parse(result.Output);
            var root = data.RootElement;
            var axes = root.GetProperty("axes");
            panLast = ReadLast(axes, "S1");
            tiltLast = ReadLast(axes, "S2");
            var firmware = root.GetProperty("firmware").GetString();
            readyFirmware = firmware == "0.4.0";
            var stopped = root.GetProperty("final_state").GetString() == "DISARMED";
            Status = !readyFirmware ? "读取成功。同步平滑运动需要固件 v0.4.0。"
                : !stopped ? "固件状态异常，请检查连接并复位 ESP32。"
                : success;
            readyFirmware &= stopped;
            if (action == "nudge" && readyFirmware)
            {
                var step = root.GetProperty("nudge");
                var axis = step.GetProperty("axis").GetString();
                var from = step.GetProperty("from").GetInt32();
                var to = step.GetProperty("to").GetInt32();
                var frames = step.GetProperty("uart_frames_sent").GetInt32();
                Status = $"{axis} {from}° → {to}° · ESP32 UART 已发 {frames} 帧";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            panLast = tiltLast = null;
            readyFirmware = false;
            Ready = false;
            await Task.Delay(550);
            Status = "已停止上位机发送。舵机仍可能保持，异常时请拔 5V。继续前请重新读状态。";
        }
        catch (Exception error)
        {
            panLast = tiltLast = null;
            readyFirmware = false;
            Ready = false;
            Status = $"操作未完成。{error.Message}";
        }
        finally { Busy = false; }
    }

    private void RefreshControls()
    {
        OnPropertyChanged(nameof(CanControl));
        OnPropertyChanged(nameof(CanStopSweep));
        OnPropertyChanged(nameof(CanPanDown));
        OnPropertyChanged(nameof(CanPanUp));
        OnPropertyChanged(nameof(CanTiltDown));
        OnPropertyChanged(nameof(CanTiltUp));
        OnPropertyChanged(nameof(LastRequests));
        OnPropertyChanged(nameof(CenterPan));
        OnPropertyChanged(nameof(CenterTilt));
        OnPropertyChanged(nameof(CenterLabel));
        OnPropertyChanged(nameof(CanPattern));
    }

    private static int? ReadLast(JsonElement axes, string axis) =>
        axes.TryGetProperty(axis, out var position) && position.GetProperty("last_requested").GetInt32() is var last && last >= 0 ? last : null;
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string AngleLabel(int? value) => value is { } angle ? $"{angle}°" : "—";
    private sealed record Calibration(int Pan, int Tilt, int TiltFloor, int PanFloor = 80, int PanCeiling = 100, int TiltCeiling = 65);
}
