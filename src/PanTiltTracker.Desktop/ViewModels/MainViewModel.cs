using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PanTiltTracker.Core;
using PanTiltTracker.Desktop.Services;
using PanTiltTracker.Vision;

namespace PanTiltTracker.Desktop.ViewModels;

public readonly record struct ErrorSample(double? X, double? Y);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly string workspace;
    private readonly ControllerSettings initialSettings;
    private readonly List<ErrorSample> history = [];
    private CancellationTokenSource? cancellation;
    private Task? activeSession;
    private TaskCompletionSource? sessionFinished;
    private readonly Stopwatch displayClock = new();
    private long detectedFrames;
    private bool disposed;
    private bool cameraFaulted;
    private string? preparedVideoPath;
    private int previewWidth;
    private int previewHeight;
    private OpenCvSharp.Rect? initialRegion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigure), nameof(CanSelectRegion))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(LoadFirstFrameCommand), nameof(LoadExampleCommand), nameof(ResetParametersCommand), nameof(RefreshCamerasCommand))]
    private bool isLoadingPreview;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigure), nameof(CanExport), nameof(CanOcclude), nameof(RunLabel))]
    [NotifyPropertyChangedFor(nameof(CanSelectRegion))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ResetParametersCommand), nameof(LoadFirstFrameCommand), nameof(LoadExampleCommand), nameof(RefreshCamerasCommand))]
    private bool isRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport), nameof(RunLabel))]
    private bool hasRun;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileSource), nameof(IsCameraSource), nameof(IsSimulation),
        nameof(CanOcclude), nameof(SourceDescription), nameof(SourceLabel), nameof(IsVideoSource), nameof(IsAruco), nameof(IsCsrt),
        nameof(CanChooseTarget), nameof(IsFace), nameof(AlgorithmDescription))]
    private int sourceIndex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCsrt), nameof(IsAruco), nameof(AlgorithmDescription), nameof(CanSelectRegion), nameof(CanChooseTarget), nameof(IsFace))]
    private int algorithmIndex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFace), nameof(IsAruco), nameof(AlgorithmDescription))]
    private int targetIndex;
    [ObservableProperty] private string regionLabel = "尚未框选";
    [ObservableProperty] private string inputPath = "";
    [ObservableProperty] private int cameraIndex;
    [ObservableProperty] private CameraDevice? selectedCamera;
    [ObservableProperty] private int cameraModeIndex;
    [ObservableProperty] private int cameraBackendIndex;
    [ObservableProperty] private string captureSummary = "连接成功后显示实际尺寸和驱动信息。";
    [ObservableProperty] private int markerId = 7;
    [ObservableProperty] private double panGain;
    [ObservableProperty] private double tiltGain;
    [ObservableProperty] private double maxSpeed;
    [ObservableProperty] private double deadZone;
    [ObservableProperty] private bool reversePan;
    [ObservableProperty] private bool reverseTilt;
    [ObservableProperty] private volatile bool hideTarget;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string errorMessage = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush))]
    private string stateLabel = "准备就绪";
    [ObservableProperty] private string detectionLabel = "等待图像";
    [ObservableProperty] private string targetPosition = "—";
    [ObservableProperty] private string faceSightError = "等待锁定人脸";
    [ObservableProperty] private string faceCorrection = "仅预览，不发送舵机指令";
    [ObservableProperty] private string faceAlignment = "绿框：进入对准区；橙框：离开后恢复修正";
    private FaceAimSettings? activeFaceSettings;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OutputLabel))] private bool isFollowing;
    public string OutputLabel => IsFollowing ? "人脸实物跟随 · 请求角度" : "视觉预览 · 独立于舵机";
    public bool CanStartFollow => CanStart && IsFace && IsCameraSource && Servo.CanFollow && FaceAim.DirectionsConfirmed;
    public string PreviewStartLabel => IsFace ? "仅预览（不转动）" : "开始实验";
    public string FollowStartHint => IsRunning
        ? IsFollowing ? "正在控制舵机；停止后可继续跟随。" : "当前仅预览，不转动。停止后点绿色「开始跟随」。"
        : Servo.CanFollow && !FaceAim.DirectionsConfirmed ? "请先在「对准」页确认两轴修正方向。"
        : Servo.CanFollow ? "已就绪：绿色按钮控制舵机，灰色按钮仅预览。"
        : !Servo.Ready ? "先在「舵机」页读状态，并勾选「5V 已接，机构可动」。"
        : $"跟随尚未就绪：{Servo.FollowReadiness}";
    private string faceBoundaryMessage = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasServoNotice))] private string servoNotice = "";
    public bool HasServoNotice => !string.IsNullOrWhiteSpace(ServoNotice);
    [ObservableProperty] private string errorX = "—";
    [ObservableProperty] private string errorY = "—";
    [ObservableProperty] private string panCommand = "—";
    [ObservableProperty] private string tiltCommand = "—";
    [ObservableProperty] private string processingTime = "—";
    [ObservableProperty] private string processingRate = "—";
    [ObservableProperty] private string detectionRate = "—";
    [ObservableProperty] private string frameSize = "640 × 480";
    [ObservableProperty] private string elapsed = "00:00.0";
    [ObservableProperty] private long processedFrames;
    [ObservableProperty] private string lastRunDirectory = "";
    [ObservableProperty] private bool hasPreview;
    [ObservableProperty] private WriteableBitmap? preview;
    [ObservableProperty] private IReadOnlyList<ErrorSample> plotSamples = Array.Empty<ErrorSample>();

    public ObservableCollection<string> Events { get; } = [];
    public ServoViewModel Servo { get; } = new();
    public FaceAimViewModel FaceAim { get; } = new();
    public bool LiveFaceConfirmed { get; private set; }
    public ObservableCollection<CameraDevice> Cameras { get; } = [];
    public event Action? FramePresented;
    public bool CanConfigure => !IsRunning && !IsLoadingPreview;
    public bool IsVideoSource => SourceIndex == 2;
    public bool IsCsrt => IsVideoSource && AlgorithmIndex == 1;
    public bool CanChooseTarget => !IsSimulation && (!IsVideoSource || AlgorithmIndex == 0);
    public bool IsFace => CanChooseTarget && TargetIndex == 1;
    public bool IsAruco => !IsFace && (!IsVideoSource || AlgorithmIndex == 0);
    public bool CanSelectRegion => CanConfigure && IsCsrt && preparedVideoPath == InputPath && HasPreview;
    public OpenCvSharp.Rect? InitialRegion => initialRegion;
    public int PreviewWidth => previewWidth;
    public int PreviewHeight => previewHeight;
    public string AlgorithmDescription => (IsVideoSource ? AlgorithmIndex : 0) switch
    {
        1 => "载入首帧，在画面上拖框，再开始。丢失后需停止并重新载入。",
        2 => "帧差与形态学：取最大运动区域，不进行类别识别。",
        _ => IsFace ? (SourceIndex == 1 ? "正面与侧面人脸检测；图片取最大框，不识别身份。"
                : "连续确认并锁定人脸，黄十字为滤波中心。绿色「开始跟随」转动云台；「仅预览」只看画面。角度和方向在「对准」页设置。")
                : "检测固定字典中的标记 ID。"
    };
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool CanExport => !IsRunning && HasRun && File.Exists(Path.Combine(LastRunDirectory, "telemetry.csv"));
    public bool CanStart => CanConfigure && !(IsCameraSource && cameraFaulted);
    public bool CanOcclude => IsSimulation && IsRunning;
    public bool IsFileSource => SourceIndex is 1 or 2;
    public bool IsCameraSource => SourceIndex == 3;
    public bool IsSimulation => SourceIndex == 0;
    public IBrush StateBrush => Brush.Parse(StateLabel.Contains("异常", StringComparison.Ordinal)
        ? "#A44F37" : StateLabel.Contains("丢失", StringComparison.Ordinal) || StateLabel.Contains("旧帧", StringComparison.Ordinal)
            ? "#A17328" : "#126F65");
    public string RunLabel => IsRunning ? "运行中" : HasRun ? "已停止" : "待运行";
    public string SourceLabel => SourceIndex switch { 0 => "合成场景", 1 => "本地图片", 2 => "视频回放", _ => "USB 摄像头" };
    public string LimitsLabel => $"水平 {initialSettings.PanMinDegrees:0}–{initialSettings.PanMaxDegrees:0}° / 俯仰 {initialSettings.TiltMinDegrees:0}–{initialSettings.TiltMaxDegrees:0}°";
    public string SourceDescription => SourceIndex switch
    {
        0 => "理想相机模型反馈命令角度，适合到货前验证。",
        1 => "检测单张图片；只记录结果，不构成闭环。",
        2 => "按视频标称帧率回放；命令不影响原录像。",
        _ => "读取运行本程序的电脑上的摄像头。先刷新设备；未检测到标记也可正常预览。"
    };

    public MainViewModel(string? workspace = null)
    {
        this.workspace = workspace ?? FindWorkspace();
        var config = Path.Combine(AppContext.BaseDirectory, "tracker.json");
        initialSettings = File.Exists(config)
            ? JsonSerializer.Deserialize<ControllerSettings>(File.ReadAllText(config),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
            : new();
        initialSettings.Validate();
        ApplySettings(initialSettings);
        Servo.PropertyChanged += ServoChanged;
        FaceAim.PropertyChanged += FaceAimChanged;
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(CanConfigure) or nameof(IsFace) or nameof(IsCameraSource) or nameof(IsRunning))
            {
                OnPropertyChanged(nameof(CanStartFollow));
                OnPropertyChanged(nameof(PreviewStartLabel));
                OnPropertyChanged(nameof(FollowStartHint));
            }
        };
        AddEvent("toboo 已准备好。可选择摄像头人脸模式或离线实验。");
    }

    private void ServoChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Servo.CanFollow)) OnPropertyChanged(nameof(CanStartFollow));
        if (args.PropertyName is nameof(Servo.CanFollow) or nameof(Servo.Status) or nameof(Servo.FollowReadiness)) OnPropertyChanged(nameof(FollowStartHint));
    }

    private void FaceAimChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(FaceAim.DirectionsConfirmed)) return;
        OnPropertyChanged(nameof(CanStartFollow));
        OnPropertyChanged(nameof(FollowStartHint));
    }

    partial void OnSourceIndexChanged(int value)
    {
        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStart));
        if (!IsRunning) ResetPresentation();
    }

    partial void OnInputPathChanged(string value) { if (!IsRunning) ResetPresentation(); }
    partial void OnAlgorithmIndexChanged(int value) { if (!IsRunning) ResetPresentation(); }
    partial void OnTargetIndexChanged(int value) { if (!IsRunning) ResetPresentation(); }
    partial void OnSelectedCameraChanged(CameraDevice? value) { if (value is not null) CameraIndex = value.Index; }

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    public async Task RefreshCamerasAsync()
    {
        try
        {
            var devices = await Task.Run(CameraDevices.List);
            if (disposed) return;
            Cameras.Clear();
            foreach (var device in devices) Cameras.Add(device);
            SelectedCamera = Cameras.FirstOrDefault(c => c.Index == CameraIndex) ?? Cameras.FirstOrDefault();
            AddEvent($"DirectShow 枚举到 {Cameras.Count} 个摄像头；MSMF 的编号顺序可能不同。");
        }
        catch (Exception error) { if (!disposed) ReportError($"枚举设备失败，仍可手动填写编号：{error.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    public async Task LoadExampleAsync()
    {
        SourceIndex = 2;
        AlgorithmIndex = 1;
        InputPath = Path.Combine(workspace, "data", "processed", "example.mp4");
        await LoadFirstFrameAsync();
    }

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    public async Task LoadFirstFrameAsync()
    {
        if (!CanConfigure) return;
        if (!IsVideoSource || !File.Exists(InputPath)) { ReportError("请先选择存在的视频文件。"); return; }
        var path = InputPath;
        ResetPresentation();
        IsLoadingPreview = true;
        try
        {
            var frame = await Task.Run(() =>
            {
                using var capture = new OpenCvSharp.VideoCapture(path);
                using var image = new OpenCvSharp.Mat();
                if (!capture.IsOpened() || !capture.Read(image) || image.Empty()) throw new IOException("无法读取视频首帧。");
                using var bgra = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(image, bgra, OpenCvSharp.ColorConversionCodes.BGR2BGRA);
                var bytes = new byte[bgra.Width * bgra.Height * 4];
                Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
                return (Pixels: bytes, Width: image.Width, Height: image.Height);
            });
            if (disposed || InputPath != path || !IsVideoSource) return;
            preparedVideoPath = path;
            UpdatePreview(frame.Pixels, frame.Width, frame.Height);
            FrameSize = $"{frame.Width} × {frame.Height}";
            StateLabel = "首帧已载入";
            AddEvent("已载入第 0 帧。拖框时包含机身和机臂，尽量少含背景。");
        }
        catch (Exception error) { if (!disposed) ReportError(error.Message); }
        finally { IsLoadingPreview = false; }
    }

    public void SetInitialRegion(OpenCvSharp.Rect region)
    {
        if (!CanSelectRegion) throw new InvalidOperationException("请先在 CSRT 视频模式载入首帧。");
        VideoTargetTracker.ValidateRegion(region, new(previewWidth, previewHeight));
        initialRegion = region;
        RegionLabel = $"({region.X}, {region.Y})  {region.Width}×{region.Height} px";
        FramePresented?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    public Task StartAsync() => RunSessionAsync(false);

    [RelayCommand]
    public Task StartFollowAsync() => CanStartFollow ? RunSessionAsync(true) : Task.CompletedTask;

    private async Task RunSessionAsync(bool follow)
    {
        if (!CanStart) return;
        ErrorMessage = "";
        ServoNotice = "";
        if ((IsAruco && (MarkerId is < 0 or >= 50)) || CameraIndex < 0)
        {
            ErrorMessage = "标记 ID 应为 0–49，相机编号应为非负整数。";
            return;
        }
        if (IsFileSource && !File.Exists(InputPath))
        {
            ErrorMessage = "请先选择存在的图片或视频文件。";
            AddEvent(ErrorMessage);
            return;
        }
        if (IsCsrt && (preparedVideoPath != InputPath || initialRegion is null))
        {
            ReportError("CSRT 需要先载入首帧，并在画面上拖框选择目标。");
            return;
        }
        var settings = initialSettings with
        {
            PanGain = PanGain, TiltGain = TiltGain, MaxSpeedDegreesPerSecond = MaxSpeed,
            DeadZone = DeadZone, PanDirection = ReversePan ? -1 : 1, TiltDirection = ReverseTilt ? -1 : 1
        };
        try { settings.Validate(); }
        catch (ArgumentException) { ErrorMessage = "参数无效，请检查增益、速度和死区。"; return; }
        try { activeFaceSettings = IsFace ? FaceAim.Snapshot() : null; }
        catch (ArgumentException error) { ReportError($"人脸对准参数无效：{error.Message}"); return; }
        if (activeFaceSettings is not null && Servo.TiltCalibrationSaved)
            activeFaceSettings = activeFaceSettings with { PanMinDegrees = Servo.PanMin, PanMaxDegrees = Servo.PanMax,
                TiltMinDegrees = Servo.TiltFloor, TiltMaxDegrees = Servo.TiltCeiling };
        if (follow)
        {
            if (activeFaceSettings?.Camera is null || !activeFaceSettings.DirectionsConfirmed)
            { ReportError("请先配置相机参数，并在微调验证后确认两轴方向。"); return; }
            if (FaceAim.FollowAxesIndex is < 0 or > 2) { ReportError("请选择跟随测试轴。"); return; }
            if (!Servo.ReserveFollow()) return;
        }

        LastRunDirectory = Path.Combine(workspace, "artifacts", "desktop",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "Z");
        var options = new SessionOptions((InputSource)SourceIndex, InputPath, CameraIndex,
            MarkerId, settings, LastRunDirectory, IsVideoSource ? (VideoAlgorithm)AlgorithmIndex : VideoAlgorithm.Aruco,
            initialRegion, CameraModeIndex switch
            {
                1 => new(1280, 720, 30, CameraBackendIndex == 1 ? OpenCvSharp.VideoCaptureAPIs.MSMF : OpenCvSharp.VideoCaptureAPIs.DSHOW),
                2 => new(1920, 1080, 30, CameraBackendIndex == 1 ? OpenCvSharp.VideoCaptureAPIs.MSMF : OpenCvSharp.VideoCaptureAPIs.DSHOW),
                _ => new(640, 480, 30, CameraBackendIndex == 1 ? OpenCvSharp.VideoCaptureAPIs.MSMF : OpenCvSharp.VideoCaptureAPIs.DSHOW)
            }, DetectFaces: IsFace, FaceAim: activeFaceSettings);
        IsRunning = true;
        ResetPresentation();
        HasRun = true;
        HideTarget = false;
        ProcessedFrames = 0;
        detectedFrames = 0;
        history.Clear();
        PlotSamples = Array.Empty<ErrorSample>();
        displayClock.Restart();
        StateLabel = "正在启动";
        AddEvent($"开始：{SourceLabel}，{(IsFace ? SourceIndex == 1 ? "人脸 / 单帧最大框" : "人脸 / 连续确认与空间锁定" : IsAruco ? $"标记 ID {MarkerId}" : ((VideoAlgorithm)AlgorithmIndex).ToString())}。视觉会话仅计算请求。");
        cancellation = new();
        sessionFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FaceServoSession? transport = null;
        FaceFollowOutputGuard? guardedOutput = null;
        var normalStop = false;
        IsFollowing = follow;
        try
        {
            if (follow)
            {
                transport = await FaceServoSession.OpenAsync(Servo.Port.Trim().ToUpperInvariant(), Servo.FollowBounds,
                    FaceAim.FollowAxesIndex switch { 0 => "S1", 1 => "S2", _ => "both" }, cancellation.Token);
                if (FaceAim.TrialRangeEnabled) transport.RestrictTrialRange(FaceAim.TrialHalfRange);
                var bounds = transport.Bounds;
                activeFaceSettings = activeFaceSettings! with { PanMinDegrees = bounds[0], PanMaxDegrees = bounds[1],
                    TiltMinDegrees = bounds[2], TiltMaxDegrees = bounds[3] };
                options = options with { FaceAimReference = transport.Reference, FaceAim = activeFaceSettings };
                guardedOutput = new(transport.UpdateAsync, transport.DisposeAsync, () => transport.Reference, () => transport.LimitNotice);
                AddEvent($"本轮请求边界：S1 {bounds[0]}–{bounds[1]}°，S2 {bounds[2]}–{bounds[3]}°。");
                AddEvent("实物跟随已启用。只有新鲜且确认的人脸可更新目标；停止不会切断 5V。");
            }
            activeSession = TrackingSession.RunAsync(options, frame =>
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!disposed) Present(frame);
                }).GetTask(), () => HideTarget, cancellation.Token, guardedOutput is null ? null : guardedOutput.UpdateAsync);
            await activeSession;
            normalStop = true;
            StateLabel = "处理完成";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { normalStop = true; StateLabel = "已停止"; }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
            StateLabel = "运行异常";
            if (IsCameraSource && error.Message.Contains("重启应用", StringComparison.Ordinal)) cameraFaulted = true;
            AddEvent($"错误：{error.Message}");
        }
        finally
        {
            if (transport is not null)
            {
                try { await transport.DisposeAsync(); }
                catch (Exception error) { normalStop = false; AddEvent($"停止串口：{error.Message}"); }
            }
            if (follow) await Servo.ReleaseFollowAsync(normalStop && transport is not null && guardedOutput?.Suspended != true);
            if (guardedOutput?.Suspended != true) ServoNotice = "";
            IsFollowing = false;
            displayClock.Stop();
            cancellation.Dispose();
            cancellation = null;
            activeSession = null;
            IsRunning = false;
            OnPropertyChanged(nameof(CanExport));
            AddEvent(File.Exists(Path.Combine(LastRunDirectory, "telemetry.csv"))
                ? $"结束：已处理 {ProcessedFrames} 帧，日志已保存。"
                : $"结束：已处理 {ProcessedFrames} 帧，未生成日志。");
            sessionFinished.TrySetResult();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (sessionFinished is not null) await sessionFinished.Task;
    }

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private void ResetParameters() => ApplySettings(initialSettings);

    public void ExportCsvTo(string destination)
    {
        if (!CanExport) throw new InvalidOperationException("请先停止一次已运行的实验再导出。");
        var source = Path.Combine(LastRunDirectory, "telemetry.csv");
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, destination, overwrite: true);
        AddEvent($"CSV 已导出：{Path.GetFileName(destination)}");
    }

    public void ReportError(string text) { ErrorMessage = text; AddEvent(text); }
    public void AddEvent(string text)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (Events.Count > 20) Events.RemoveAt(Events.Count - 1);
    }

    internal void Present(SessionFrame frame)
    {
        LiveFaceConfirmed = frame.FaceAim?.Observation.HasTarget == true && frame.VisionStatus == "face_locked";
        if (frame.OutputSuspended && IsFollowing)
        {
            IsFollowing = false;
            Servo.SuspendFollow(frame.OutputNotice ?? "舵机通信中断，已停发；画面继续。");
            AddEvent(Servo.Status);
        }
        var observation = frame.Observation;
        var command = frame.Command;
        PresentFaceAim(frame.FaceAim);
        if (frame.CaptureSummary is not null) CaptureSummary = frame.CaptureSummary;
        ProcessedFrames++;
        if (observation.HasTarget) detectedFrames++;
        DetectionLabel = frame.VisionStatus switch
        {
            "face_detected" => $"检测到 {frame.TargetCount} 张人脸 · 目标为最大框",
            "no_face_detected" => "未检测到人脸",
            "face_confirming" => "正在连续确认人脸 · 暂不移动",
            "face_lock_missing" => "原目标暂时丢失 · 保持位置",
            "face_locked" => frame.FaceAim?.Readiness switch
            {
                "ready" => "人脸已锁定 · 平滑对准预览",
                "camera_geometry_required" => "人脸已锁定 · 待配置相机视场或内参",
                "mounting_directions_required" => "人脸已锁定 · 待确认舵机修正方向",
                "request_reference_required" => "人脸已锁定 · 等待舵机请求参考，仅预览",
                "servo_output_suspended" => "人脸已锁定 · 舵机停发，画面继续",
                _ => "人脸已锁定 · 相机参数与画面不匹配，仅预览"
            },
            "face_stale_frame" => "人脸画面已过期 · 保持位置",
            "face_repeated_frame" => "重复人脸画面 · 保持位置",
            "manual_initialization" => "人工首框 · 非自动识别",
            "tracker_reported_target" => "CSRT 报告有效 · 需核查漂移",
            "lost_reinitialize" => "CSRT 丢失 · 请重新框选",
            "motion_warmup" => "帧差初始化",
            "motion_candidate_not_classified" => "运动候选 · 未分类",
            "no_motion_candidate" => "没有运动候选",
            _ => observation.HasTarget ? $"已检测 · ID {MarkerId}" : "未检测到目标"
        };
        TargetPosition = observation.HasTarget ? $"({observation.CenterX:0.0}, {observation.CenterY:0.0}) px" : "—";
        ErrorX = Format(command.ErrorX);
        ErrorY = Format(command.ErrorY);
        PanCommand = frame.FaceAim is { HasReference: false } ? "—" : $"{command.PanDegrees:0.0}°";
        TiltCommand = frame.FaceAim is { HasReference: false } ? "—" : $"{command.TiltDegrees:0.0}°";
        ProcessingTime = $"{frame.AlgorithmMs:0.0} ms";
        ProcessingRate = $"{ProcessedFrames / Math.Max(0.01, displayClock.Elapsed.TotalSeconds):0.0} Hz";
        DetectionRate = $"{100.0 * detectedFrames / ProcessedFrames:0.0}%";
        FrameSize = $"{frame.Width} × {frame.Height}";
        Elapsed = frame.Time.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        var previous = StateLabel;
        StateLabel = command.State switch
        {
            TrackingState.Waiting => "等待目标", TrackingState.Tracking => "正在跟踪",
            TrackingState.Centered => "目标已居中", TrackingState.Missing => "短暂丢失 · 保持",
            TrackingState.Lost => "目标丢失 · 保持", TrackingState.Stale => "旧帧 · 保持",
            _ => "重新同步 · 保持"
        };
        if (frame.FaceAim is { Observation.HasTarget: true, Readiness: not "ready" })
            StateLabel = "人脸已锁定 · 仅预览";
        if (IsFollowing && faceBoundaryMessage.Length > 0) StateLabel = faceBoundaryMessage;
        ServoNotice = frame.OutputSuspended ? frame.OutputNotice ?? "舵机通信中断，已停发；画面继续。"
            : faceBoundaryMessage.Length > 0
                ? $"目标超出当前可达范围：{faceBoundaryMessage}。该轴保持边界，画面与其余跟随继续。"
                : frame.OutputNotice ?? "";
        if (frame.OutputSuspended)
        {
            StateLabel = "通信中断 · 画面继续";
            FaceCorrection = "舵机已停发；停止预览后读状态，再重新启动跟随";
        }
        if (StateLabel != previous && command.State is TrackingState.Lost or TrackingState.Stale)
            AddEvent(StateLabel);
        history.Add(new(command.ErrorX, command.ErrorY));
        if (history.Count > 180) history.RemoveAt(0);
        PlotSamples = history.ToArray();
        UpdatePreview(frame.Pixels, frame.Width, frame.Height);
    }

    internal void PresentFaceAim(FaceAimResult? aim)
    {
        faceBoundaryMessage = "";
        if (aim?.SightError is not { } error)
        {
            FaceSightError = aim?.Readiness == "camera_geometry_mismatch" ? "相机参数与画面比例不匹配" : "等待新鲜目标及相机参数";
            FaceCorrection = "保持 · 不生成修正";
            FaceAlignment = "绿框：进入对准区；橙框：离开后恢复修正";
            return;
        }
        FaceSightError = $"水平 {error.HorizontalDegrees:+0.00;-0.00;0.00}° / 垂直 {error.VerticalDegrees:+0.00;-0.00;0.00}°";
        var settings = activeFaceSettings ?? FaceAim.Snapshot();
        FaceCorrection = settings.DirectionsConfirmed
            ? $"修正方向 · S1 {(settings.PanEnabled ? Sign(error.HorizontalDegrees, settings.PanDirection) : "保持")} / S2 {(settings.TiltEnabled ? Sign(error.VerticalDegrees, settings.TiltDirection) : "保持")} · {(IsFollowing ? "实物跟随" : "仅预览") }"
            : "视线正方向：右 / 下；舵机修正方向待确认";
        FaceAlignment = Math.Abs(error.HorizontalDegrees) <= settings.DeadZoneEnterDegrees &&
            Math.Abs(error.VerticalDegrees) <= settings.DeadZoneEnterDegrees ? "人脸位于绿色对准区"
            : Math.Abs(error.HorizontalDegrees) > settings.DeadZoneExitDegrees || Math.Abs(error.VerticalDegrees) > settings.DeadZoneExitDegrees
                ? "人脸位于橙色区外 · 需要修正" : "滞回过渡区 · 避免反复启停";
        if (IsFollowing)
        {
            faceBoundaryMessage = BoundaryMessage(aim, settings);
            if (faceBoundaryMessage.Length > 0) FaceAlignment = faceBoundaryMessage + " · 当前转动受边界限制";
        }
        string Sign(double value, int direction) => Math.Abs(value) <= settings.DeadZoneEnterDegrees ? "保持"
            : value * direction > 0 ? "+ 增大角度" : "− 减小角度";
    }

    internal static string BoundaryMessage(FaceAimResult aim, FaceAimSettings settings)
    {
        if (aim.SightError is not { } error || !aim.Observation.HasTarget || aim.Readiness != "ready") return "";
        var limits = new List<string>();
        Add("水平", settings.PanEnabled, aim.Command.PanDegrees, error.HorizontalDegrees * settings.PanDirection,
            settings.PanMinDegrees, settings.PanMaxDegrees);
        Add("俯仰", settings.TiltEnabled, aim.Command.TiltDegrees, error.VerticalDegrees * settings.TiltDirection,
            settings.TiltMinDegrees, settings.TiltMaxDegrees);
        return string.Join(" / ", limits);
        void Add(string axis, bool enabled, double request, double correction, double min, double max)
        {
            if (!enabled) return;
            if (request >= max - 0.001 && correction > settings.DeadZoneEnterDegrees)
                limits.Add($"{axis}请求已达 {max:0.##}° 上限");
            else if (request <= min + 0.001 && correction < -settings.DeadZoneEnterDegrees)
                limits.Add($"{axis}请求已达 {min:0.##}° 下限");
        }
    }

    private void UpdatePreview(byte[] pixels, int width, int height)
    {
        previewWidth = width;
        previewHeight = height;
        if (Preview is null || Preview.PixelSize.Width != width || Preview.PixelSize.Height != height)
        {
            Preview?.Dispose();
            Preview = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var locked = Preview.Lock())
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(pixels, row * width * 4, IntPtr.Add(locked.Address, row * locked.RowBytes), width * 4);
        }
        HasPreview = true;
        OnPropertyChanged(nameof(CanSelectRegion));
        FramePresented?.Invoke();
    }

    private void ApplySettings(ControllerSettings settings)
    {
        PanGain = settings.PanGain;
        TiltGain = settings.TiltGain;
        MaxSpeed = settings.MaxSpeedDegreesPerSecond;
        DeadZone = settings.DeadZone;
        ReversePan = settings.PanDirection < 0;
        ReverseTilt = settings.TiltDirection < 0;
    }

    private void ResetPresentation()
    {
        LiveFaceConfirmed = false;
        CaptureSummary = "连接成功后显示实际尺寸和驱动信息。";
        preparedVideoPath = null;
        initialRegion = null;
        RegionLabel = "尚未框选";
        ErrorMessage = "";
        Preview?.Dispose();
        Preview = null;
        HasPreview = false;
        HasRun = false;
        ProcessedFrames = 0;
        ErrorX = ErrorY = PanCommand = TiltCommand = ProcessingTime = ProcessingRate = DetectionRate = "—";
        DetectionLabel = "等待图像";
        TargetPosition = "—";
        FaceSightError = "等待锁定人脸";
        FaceCorrection = "仅预览，不发送舵机指令";
        StateLabel = "准备就绪";
        Elapsed = "00:00.0";
        history.Clear();
        PlotSamples = Array.Empty<ErrorSample>();
        OnPropertyChanged(nameof(CanSelectRegion));
        FramePresented?.Invoke();
    }

    private static string Format(double? value) => value?.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) ?? "—";
    private static string FindWorkspace()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.mode"))) return AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PanTiltTracker.sln"))) return directory.FullName;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanTiltTracker");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Servo.PropertyChanged -= ServoChanged;
        FaceAim.PropertyChanged -= FaceAimChanged;
        cancellation?.Cancel();
        Preview?.Dispose();
        FramePresented = null;
    }
}
