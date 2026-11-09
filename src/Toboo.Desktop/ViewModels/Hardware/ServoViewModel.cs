using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toboo.Runtime;
using Toboo.Desktop.Services;

namespace Toboo.Desktop.ViewModels;

public sealed partial class ServoViewModel : ObservableObject
{
    internal const string TiltProfileId = "labkit-blue-sg90-s2-v1";
    private const int PanLowerLimit = 30, PanUpperLimit = 175, TiltMin = 0, TiltMax = 180;
    [ObservableProperty] private int panMin = PanLowerLimit;
    [ObservableProperty] private int panMax = PanUpperLimit;
    private readonly string calibrationFile;
    private readonly Func<IEnumerable<string>, CancellationToken, Task<ServoTool.Result>> runTool;
    private readonly Func<string[]> getPorts;
    private bool readyFirmware;
    private bool followFirmware;
    private int firmwareTiltMin = 35, firmwareTiltMax = 90, pairedTiltMin = 35, pairedTiltMax = 65;
    private CancellationTokenSource? sweepCancellation;
    private int? panLast, tiltLast;

    [ObservableProperty] private string port = "COM6";
    [ObservableProperty] private string[] availablePorts = [];
    [ObservableProperty] private string portsHint = "请选择 ESP32 串口。";
    [ObservableProperty] private bool ready;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool sweepReady;
    [ObservableProperty] private int tiltFloor = 45;
    [ObservableProperty] private int tiltCeiling = 90;
    [ObservableProperty] private bool tiltCalibrating = true;
    [ObservableProperty] private bool tiltCalibrationSaved;
    [ObservableProperty] private string status = "先读取串口状态。";

    public bool CanControl => readyFirmware && Ready && !Busy;
    public bool FirmwareReady => readyFirmware;
    public bool CanRefreshPorts => !Busy;
    public bool CanCalibratedMotion => CanControl && TiltCalibrationSaved && !TiltCalibrating &&
        TiltFloor >= pairedTiltMin && TiltCeiling <= pairedTiltMax;
    public bool CanFollow => CanCalibratedMotion && followFirmware &&
        panLast >= PanMin && panLast <= PanMax && tiltLast >= TiltFloor && tiltLast <= TiltCeiling;
    public int[] FollowBounds => [PanMin, PanMax, TiltFloor, TiltCeiling];

    internal bool ReserveFollow()
    {
        if (!CanFollow || !TryCalibration(out _)) return false;
        Busy = true;
        Status = "人脸跟随占用串口；停止跟随后才能回位或微调。";
        return true;
    }

    internal async Task ReleaseFollowAsync(bool normalStop)
    {
        if (!normalStop)
        {
            SuspendFollow("跟随输出已停发。请重新读状态并确认供电；停止不会切断 5V。");
            return;
        }
        readyFirmware = followFirmware = false;
        panLast = tiltLast = null;
        // Preserve the user's unchanged supply confirmation after a normal stop,
        // but only allow restart after a new read-only DISARMED status check.
        Busy = false;
        await SendAsync("inspect", [], "跟随已停止，可直接再次开始。停止不会切断 5V。");
        if (!CanFollow) Ready = false;
    }

    internal void SuspendFollow(string message)
    {
        readyFirmware = followFirmware = false;
        panLast = tiltLast = null;
        Ready = false; Busy = false;
        Status = message;
        RefreshControls();
    }
    public bool CanStopSweep => Busy && sweepCancellation is { IsCancellationRequested: false };
    public bool CanPanDown => CanControl && panLast > PanMin;
    public bool CanPanUp => CanControl && panLast < PanMax;
    public bool CanTiltDown => CanControl && tiltLast > (TiltCalibrating ? firmwareTiltMin : TiltFloor);
    public bool CanTiltUp => CanControl && tiltLast < (TiltCalibrating ? firmwareTiltMax : TiltCeiling);
    public bool ShowTiltReference => TiltCalibrating && TiltFloor <= 45 && TiltCeiling >= 45;
    public bool CanInitializeTilt => CanControl && ShowTiltReference;
    public bool CanCaptureTilt => !Busy && tiltLast.HasValue;
    public string TiltCalibrationHint => TiltCalibrating
        ? !readyFirmware ? "新 S2 待校准 · 先读状态。45°–90°已试转，机械边界待确认。"
            : $"新 S2 校准中 · 每次 1°，请求范围 {firmwareTiltMin}–{firmwareTiltMax}°。45°–90°已试转；机械极限待确认。"
        : "S2 新边界已保存；中心取新边界中点。";
    public string LastRequests => $"末次指令 · S1 {AngleLabel(panLast)} / S2 {AngleLabel(tiltLast)}";
    public int CenterPan => (PanMin + PanMax + 1) / 2;
    public int CenterTilt => (TiltFloor + TiltCeiling + 1) / 2;
    public string CenterLabel => $"中心指令 · S1 {CenterPan}° / S2 {CenterTilt}°";
    public string WorkingBoundsLabel => $"工作区 · S1 {PanMin}–{PanMax}° / S2 {TiltFloor}–{TiltCeiling}°";
    public string FollowReadiness => !readyFirmware ? "先读状态。"
        : !Ready ? "请确认 5V 已接、机构可动。"
        : Busy ? "串口正在使用中。"
        : !TiltCalibrationSaved || TiltCalibrating ? "先保存新 S2 的工作边界。"
        : !panLast.HasValue || !tiltLast.HasValue ? "先点「平滑回中心」，建立两轴请求参考。"
        : !CanFollow ? "当前请求不在工作区内，请先「平滑回中心」。"
        : "两轴请求与工作边界已就绪。";
    public bool CanPattern => CanCalibratedMotion && SweepReady;

    public ServoViewModel() : this(ServoTool.RunAsync) { }

    internal ServoViewModel(Func<IEnumerable<string>, CancellationToken, Task<ServoTool.Result>> runTool,
        string? calibrationFile = null, Func<string[]>? getPorts = null)
    {
        this.runTool = runTool;
        this.getPorts = getPorts ?? SerialPortCatalog.List;
        RefreshPorts();
        this.calibrationFile = calibrationFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PanTiltTracker", "servo.json");
        try
        {
            if (!File.Exists(this.calibrationFile)) return;
            var saved = JsonSerializer.Deserialize<Calibration>(File.ReadAllText(this.calibrationFile));
            if (saved is null) throw new InvalidDataException("校准配置为空。");
            if (saved.PanFloor >= PanLowerLimit && saved.PanCeiling <= PanUpperLimit && saved.PanFloor < saved.PanCeiling)
            { PanMin = saved.PanFloor; PanMax = saved.PanCeiling; }
            if (saved.TiltProfile != TiltProfileId)
            { Status = "旧 S2 校准已作废。请为新舵机逐度测边界，再保存。"; return; }
            if (saved.TiltFloor < TiltMin || saved.TiltCeiling > TiltMax || saved.TiltFloor >= saved.TiltCeiling)
                throw new InvalidDataException("S2 边界无效。");
            TiltFloor = saved.TiltFloor; TiltCeiling = saved.TiltCeiling;
            TiltCalibrationSaved = saved.TiltConfirmed;
            TiltCalibrating = !saved.TiltConfirmed;
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
    partial void OnTiltFloorChanged(int value) { TiltCalibrationSaved = false; SweepReady = false; RefreshControls(); }
    partial void OnTiltCeilingChanged(int value) { TiltCalibrationSaved = false; SweepReady = false; RefreshControls(); }
    partial void OnTiltCalibratingChanged(bool value) => RefreshControls();
    partial void OnTiltCalibrationSavedChanged(bool value) => RefreshControls();
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

    [RelayCommand(CanExecute = nameof(CanRefreshPorts))]
    private void RefreshPorts()
    {
        if (Busy) return;
        var selected = Port;
        try
        {
            var ports = SerialPortCatalog.Normalize(getPorts());
            AvailablePorts = ports;
            Port = ports.FirstOrDefault(value => value == selected) ?? "";
            PortsHint = ports.Length == 0 ? "未发现串口，请连接 ESP32 后刷新。" : "请选择 ESP32 串口，不要选择 LCD 的 Uno。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { AvailablePorts = []; Port = ""; PortsHint = "无法读取串口列表，请刷新重试。"; }
    }

    [RelayCommand]
    private Task HomeAsync()
    {
        if (!CanCalibratedMotion) return Task.CompletedTask;
        if (!TryCalibration(out var calibration)) return Task.CompletedTask;
        return RunMotionAsync("home", BoundaryArguments(calibration), "已平滑回到边界矩形中心。");
    }

    [RelayCommand] private Task PanDownAsync() => NudgeAsync("S1", -1, CanPanDown);
    [RelayCommand] private Task PanUpAsync() => NudgeAsync("S1", 1, CanPanUp);
    [RelayCommand] private Task TiltDownAsync() => NudgeAsync("S2", -1, CanTiltDown);
    [RelayCommand] private Task TiltUpAsync() => NudgeAsync("S2", 1, CanTiltUp);

    [RelayCommand] private Task InitializeTiltAsync() => CanInitializeTilt
        ? RunMotionAsync("reference", ["--axis", "S2", "--execute"], "S2 45°定位请求已完成；请观察实际姿态。")
        : Task.CompletedTask;

    [RelayCommand]
    private void BeginTiltCalibration()
    {
        if (Busy) return;
        if (TiltCalibrationSaved && !tiltLast.HasValue)
        { Status = "请先读状态并平滑回中心，再重新校准 S2。"; return; }
        TiltCalibrationSaved = false; TiltCalibrating = true;
        SweepReady = false;
        if (!TryCalibration(out var calibration)) return;
        try
        {
            WriteCalibration(calibration with { TiltConfirmed = false });
            Status = "只重设 S2，S1 保留。逐度试探并记录新边界；保存前不启动跟随或整条扫动。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Status = $"S2 当前已进入校准，但重置未保存：{error.Message}"; }
    }

    [RelayCommand] private void CaptureTiltFloor()
    {
        if (!CanCaptureTilt) return;
        TiltFloor = tiltLast!.Value; TiltCalibrationSaved = false;
        Status = $"S2 下边界暂记为请求 {TiltFloor}°；请在碰撞/卡住前留出余量。";
    }
    [RelayCommand] private void CaptureTiltCeiling()
    {
        if (!CanCaptureTilt) return;
        TiltCeiling = tiltLast!.Value; TiltCalibrationSaved = false;
        Status = $"S2 上边界暂记为请求 {TiltCeiling}°；保存后取两边界中点。";
    }

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
            WriteCalibration(calibration);
            TiltCalibrationSaved = true; TiltCalibrating = false;
            Status = $"已保存边界。{CenterLabel}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status = $"保存失败：{error.Message}";
        }
    }

    private void WriteCalibration(Calibration calibration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(calibrationFile))!);
        var temporary = calibrationFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(calibration));
        File.Move(temporary, calibrationFile, overwrite: true);
    }

    private bool TryCalibration(out Calibration calibration)
    {
        if (PanMin >= PanLowerLimit && PanMax <= PanUpperLimit && PanMin < PanMax &&
            TiltFloor >= TiltMin && TiltCeiling <= TiltMax && TiltFloor < TiltCeiling)
        {
            calibration = new(CenterPan, CenterTilt, TiltFloor, PanMin, PanMax, TiltCeiling, TiltProfileId, true);
            return true;
        }
        Status = "请输入有效边界。S1 30–175°，S2 0–180°，下限须小于上限。范围是请求值，须先实测。";
        calibration = null!;
        return false;
    }

    private Task NudgeAsync(string axis, int delta, bool enabled) => enabled
        ? SendAsync("nudge", ["--axis", axis, "--delta", Number(delta),
            "--tilt-floor", Number(TiltCalibrating ? firmwareTiltMin : TiltFloor),
            "--tilt-ceiling", Number(TiltCalibrating ? firmwareTiltMax : TiltCeiling), "--execute"],
            $"{axis} 微调指令已完成。")
        : Task.CompletedTask;

    private async Task SendAsync(string action, string[] arguments, string success, CancellationToken cancellationToken = default)
    {
        if (Busy) return;
        var serialPort = (Port ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(serialPort, @"\ACOM[1-9][0-9]{0,3}\z"))
        {
            Status = "请先选择 ESP32 串口；连接设备后可点击刷新。";
            return;
        }
        Busy = true;
        Status = action switch
        {
            "inspect" => "正在读取状态…",
            "home" => "两轴正在平滑快速回到矩形中心…",
            "pattern" => "正在走矩形与交叉对角线路径，结束后回中心…",
            "reference" => "仅 S2 正在前往 45°定位请求…",
            "sweep" => arguments[1] == "S2" ? "S2 正前往 40°，再扫向 65°…" : "S1 正前往 45°，再扫向 90°…",
            _ => "正在发送指令…"
        };
        try
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PanTiltTracker", "servo-logs");
            Directory.CreateDirectory(logDirectory);
            var logFile = Path.Combine(logDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fffffff}-{action}.jsonl");
            var result = await runTool(["--port", serialPort, "--action", action, "--log", logFile, .. arguments], cancellationToken);
            if (result.ExitCode != 0) throw new IOException(result.Error.Trim());
            using var data = JsonDocument.Parse(result.Output);
            var root = data.RootElement;
            var axes = root.GetProperty("axes");
            panLast = ReadLast(axes, "S1");
            tiltLast = ReadLast(axes, "S2");
            var firmware = root.GetProperty("firmware").GetString();
            readyFirmware = firmware is "0.4.0" or "0.5.0" or "0.5.1" or "0.5.2" or "0.5.3";
            followFirmware = firmware is "0.5.0" or "0.5.1" or "0.5.2" or "0.5.3";
            firmwareTiltMin = firmware == "0.5.3" ? 0 : 35;
            firmwareTiltMax = firmware == "0.5.3" ? 180 : firmware == "0.5.2" ? 90 : 65;
            pairedTiltMin = firmware == "0.5.3" ? 0 : 35;
            pairedTiltMax = firmware == "0.5.3" ? 180 : 65;
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
        OnPropertyChanged(nameof(FirmwareReady));
        OnPropertyChanged(nameof(CanRefreshPorts));
        RefreshPortsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanControl));
        OnPropertyChanged(nameof(CanCalibratedMotion));
        OnPropertyChanged(nameof(CanFollow));
        OnPropertyChanged(nameof(CanStopSweep));
        OnPropertyChanged(nameof(CanPanDown));
        OnPropertyChanged(nameof(CanPanUp));
        OnPropertyChanged(nameof(CanTiltDown));
        OnPropertyChanged(nameof(CanTiltUp));
        OnPropertyChanged(nameof(CanInitializeTilt));
        OnPropertyChanged(nameof(ShowTiltReference));
        OnPropertyChanged(nameof(CanCaptureTilt));
        OnPropertyChanged(nameof(TiltCalibrationHint));
        OnPropertyChanged(nameof(LastRequests));
        OnPropertyChanged(nameof(CenterPan));
        OnPropertyChanged(nameof(CenterTilt));
        OnPropertyChanged(nameof(CenterLabel));
        OnPropertyChanged(nameof(WorkingBoundsLabel));
        OnPropertyChanged(nameof(FollowReadiness));
        OnPropertyChanged(nameof(CanPattern));
    }

    private static int? ReadLast(JsonElement axes, string axis) =>
        axes.TryGetProperty(axis, out var position) && position.GetProperty("last_requested").GetInt32() is var last && last >= 0 ? last : null;
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string AngleLabel(int? value) => value is { } angle ? $"{angle}°" : "—";
    private sealed record Calibration(int Pan, int Tilt, int TiltFloor, int PanFloor = 80, int PanCeiling = 100,
        int TiltCeiling = 65, string TiltProfile = "", bool TiltConfirmed = false);
}
