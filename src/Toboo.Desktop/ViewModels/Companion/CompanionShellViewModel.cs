using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toboo.Desktop.Services;
using Toboo.Runtime;

namespace Toboo.Desktop.ViewModels;

/// <summary>Product navigation and presentation over the existing acquisition/control owner.</summary>
public sealed partial class CompanionShellViewModel : ObservableObject, IDisposable
{
    public MainViewModel Tracking { get; }
    public ChatViewModel Chat { get; }
    public VoiceViewModel Voice { get; }
    private readonly Action requestFollow;
    public event Action<int>? AdvancedRequested;
    public event Action? SensorsRequested;
    internal Func<Task>? StopCalibrationAsync { get; set; }
    private bool navigating;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice), nameof(HasVoiceCommandNotice))]
    private string voiceCommandNotice = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompanionPage), nameof(IsDevicesPage), nameof(IsSettingsPage), nameof(IsCalibrationPage), nameof(PageTitle))]
    private int pageIndex;
    [ObservableProperty] private bool showVisualPanel = true;
    [ObservableProperty] private int settingsSection;
    [ObservableProperty] private bool calibrationExpanded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCameraSetupNotice))]
    private string cameraSetupNotice = "";
    public bool HasCameraSetupNotice => CameraSetupNotice.Length > 0;
    public bool NeedsCameraModel => Tracking.FaceAim.GeometryMode == 0;
    public bool CanQuickConfigureCamera => NeedsCameraModel && CanPrepareRobot && !Tracking.CalibrationActive && !Tracking.Servo.Busy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupConnectionStep), nameof(SetupBoundsStep), nameof(SetupDirectionStep), nameof(SetupStepLabel), nameof(SetupNextLabel), nameof(CanAdvanceSetup), nameof(SetupMessage))]
    [NotifyCanExecuteChangedFor(nameof(AdvanceSetupCommand), nameof(PreviousSetupStepCommand))]
    private int setupStep;
    public bool SetupConnectionStep => SetupStep == 0;
    public bool SetupBoundsStep => SetupStep == 1;
    public bool SetupDirectionStep => SetupStep == 2;
    public string SetupStepLabel => $"快速准备 · {SetupStep + 1}/3";
    public string SetupNextLabel => SetupStep == 2 ? "保存并完成" : "下一步";
    public string SetupMessage => !CanPrepareRobot ? "请先停止跟随，等待当前操作结束。"
        : Tracking.Servo.PanMotionBlocked || Tracking.Servo.TiltMotionBlocked ? Tracking.Servo.MotionBlockReason
        : Tracking.Servo.Busy ? "正在与云台通信，请稍候。"
        : SetupStep == 0 ? !Tracking.Servo.FirmwareReady ? "请在上方选择 ESP32 串口并点击连接。"
            : !Tracking.Servo.Ready ? "请接好外部 5V，再勾选供电确认。" : "连接与供电已确认，可以继续。"
        : SetupStep == 1 ? !Tracking.Servo.TiltCalibrationSaved || Tracking.Servo.TiltCalibrating
            ? "工作边界尚未保存。首次安装时请在高级设置中逐度确认并保存。"
            : "已加载保存的工作边界，安装未变时直接点下一步。"
        : Tracking.FaceAim.Status.StartsWith("未保存", StringComparison.Ordinal) ? Tracking.FaceAim.Status
        : NeedsCameraModel ? "点击「使用视场角估算并保存」即可配置相机，也可在高级设置中填写实测参数。"
        : !Tracking.FaceAim.DirectionsConfirmed ? "请通过微调检查两轴方向，再勾选方向确认。已有确认会自动复用。"
        : "设置已就绪。保存完成后，若缺少请求参考，请点「回到中心」。";
    public bool CanAdvanceSetup => CanPrepareRobot && !Tracking.Servo.Busy && !Tracking.Servo.PanMotionBlocked && !Tracking.Servo.TiltMotionBlocked && (SetupStep switch
    {
        0 => Tracking.Servo.FirmwareReady && Tracking.Servo.Ready,
        1 => !Tracking.Servo.PanMotionBlocked && Tracking.Servo.TiltCalibrationSaved && !Tracking.Servo.TiltCalibrating,
        _ => Tracking.FaceAim.GeometryMode > 0 && Tracking.FaceAim.DirectionsConfirmed
    });
    public bool IsCompanionPage => PageIndex == 0;
    public bool IsDevicesPage => PageIndex == 1;
    public bool IsSettingsPage => PageIndex == 2;
    public bool IsCalibrationPage => PageIndex == 3;
    public string PageTitle => PageIndex switch { 1 => "设备", 2 => "设置", 3 => "视觉标定", _ => "陪伴" };
    public string RobotDetail => Voice.IsListening ? "正在听你说话" : Voice.IsSpeaking ? "正在读给你听"
        : Chat.RemoteRunning ? "正在认真组织回复" : Tracking.IsRunning ? Tracking.IsFace && Tracking.ExpressionEnabled
        ? "正在观察可见的表情线索" : "正在预览" + Tracking.SourceLabel : "准备好了，随时开始";
    public string CameraState => Tracking.IsRunning ? Tracking.SourceLabel + " · 运行中"
        : Tracking.SelectedCamera is null ? "未开启 · 请选择摄像头" : "已发现 · 未开启";
    public string RobotConnection => Tracking.IsFollowing ? "云台跟随中" : CanFollow ? "跟随已就绪" : Tracking.Servo.FirmwareReady ? "已连接 · 待准备" : "云台待确认";
    public string SourceCaption => !Tracking.HasPreview ? "开启摄像头后显示画面"
        : Tracking.IsCameraSource && !Tracking.IsRunning ? "USB 摄像头 · 已停止" : Tracking.SourceLabel;
    public string StopLabel => Tracking.IsFollowing ? "停止跟随" : "停止感知";
    public bool IsAnyVisionRunning => Tracking.IsRunning || Tracking.CalibrationActive;
    public bool HasVoiceCommandNotice => VoiceCommandNotice.Length > 0;
    public bool HasNotice => Tracking.HasError || Tracking.HasServoNotice || VoiceCommandNotice.Length > 0;
    public bool CanStartVision => Tracking.CanStart && Tracking.SelectedCamera is not null;
    public bool CanFollow => Tracking.CanStartCameraFollow && Tracking.FaceAim.GeometryMode > 0 && Tracking.SelectedCamera is not null;
    public bool CanPrepareRobot => !Tracking.IsFollowing && !Tracking.IsChangingMode;
    public string FollowActionLabel => Tracking.IsChangingMode ? "正在切换…"
        : Tracking.IsRunning ? "从预览切换到跟随" : "开启实物跟随";
    public string PreparationHint => Tracking.Servo.PanMotionBlocked || Tracking.Servo.TiltMotionBlocked ? Tracking.Servo.MotionBlockReason
        : Tracking.IsFollowing ? "跟随已开启，可随时停止；停止不会切断动力。"
        : Tracking.IsChangingMode ? "正在结束预览并检查跟随条件…"
        : Tracking.CameraFollowSessionHint.Length > 0 ? Tracking.CameraFollowSessionHint
        : !Tracking.Servo.FirmwareReady ? "1 · 选择 ESP32 串口，连接并读状态。"
        : !Tracking.Servo.Ready ? "2 · 确认 5V 供电与机构活动空间。"
        : !Tracking.Servo.TiltCalibrationSaved || Tracking.Servo.TiltCalibrating ? "3 · 首次使用请校准并保存工作边界。"
        : !Tracking.Servo.CanFollow ? Tracking.Servo.FollowReadiness
        : NeedsCameraModel ? "还缺相机角度模型，请点击下方「使用视场角估算并保存」。"
        : !Tracking.FaceAim.DirectionsConfirmed ? "4 · 检查两轴方向，确认并保存跟随设置。"
        : Tracking.SelectedCamera is null ? "5 · 选择 USB 摄像头；可先预览，再直接切换到跟随。"
        : "已就绪。预览可直接切换到跟随，保存的校准无需每次重做。";

    public CompanionShellViewModel(MainViewModel tracking, ChatViewModel? chat = null, VoiceViewModel? voice = null,
        Action? requestFollow = null)
    {
        Tracking = tracking;
        this.requestFollow = requestFollow ?? (() => Tracking.StartFollowCommand.Execute(null));
        Chat = chat ?? new(expression: tracking.CaptureExpressionCue);
        Voice = voice ?? new(Chat);
        Voice.LocalCommandHandler = HandleVoiceCommandAsync;
        tracking.Lcd.CaptureChatRole = () => Voice.CaptureLcdRole() ?? Chat.CaptureLcdRole(!Chat.ReadReplies || !Voice.CanSpeak);
        Voice.PropertyChanged += VoiceChanged;
        Chat.PropertyChanged += ChatChanged;
        tracking.PropertyChanged += Changed;
        tracking.Servo.PropertyChanged += Changed;
        tracking.FaceAim.PropertyChanged += Changed;
        tracking.Cameras.CollectionChanged += CamerasChanged;
    }

    [RelayCommand]
    private async Task NavigateAsync(string page)
    {
        if (navigating || !int.TryParse(page, out var target) || target is < 0 or > 3) return;
        navigating = true;
        try
        {
            if (target == 3)
            {
                if (Tracking.Servo.Busy && !Tracking.IsFollowing) return;
                if (Tracking.IsRunning) await Tracking.StopAsync();
                if (Tracking.Cameras.Count == 0) await Tracking.RefreshCamerasAsync();
            }
            else if (PageIndex == 3 && StopCalibrationAsync is { } stop) await stop();
            PageIndex = target;
        }
        finally { navigating = false; }
    }
    [RelayCommand] private async Task OpenAdvancedAsync()
    {
        await NavigateAsync("1");
        if (!Tracking.CalibrationActive) AdvancedRequested?.Invoke(0);
    }
    [RelayCommand(CanExecute = nameof(CanAdvanceSetup))]
    private void AdvanceSetup()
    {
        if (!CanAdvanceSetup) return;
        if (SetupStep < 2) { SetupStep++; return; }
        Tracking.FaceAim.SaveCommand.Execute(null);
        if (Tracking.FaceAim.LastSaveSucceeded) CalibrationExpanded = false;
    }
    private bool CanGoBack() => SetupStep > 0 && CanPrepareRobot;
    [RelayCommand(CanExecute = nameof(CanGoBack))] private void PreviousSetupStep() => SetupStep--;
    [RelayCommand] private void UsePreviewDimensions() => Tracking.UsePreviewDimensionsForCalibration();
    [RelayCommand(CanExecute = nameof(CanQuickConfigureCamera))]
    private void QuickConfigureCamera()
    {
        if (!CanQuickConfigureCamera) return;
        var aim = Tracking.FaceAim;
        aim.GeometryMode = 1;
        aim.HorizontalFov = 60;
        aim.VerticalFov = 45;
        var size = Tracking.HasPreview && Tracking.IsCameraSource && Tracking.PreviewWidth > 0 && Tracking.PreviewHeight > 0
            ? (Width: Tracking.PreviewWidth, Height: Tracking.PreviewHeight)
            : Tracking.CameraModeIndex switch { 1 => (Width: 1280, Height: 720), 2 => (Width: 1920, Height: 1080), _ => (Width: 640, Height: 480) };
        aim.CalibrationWidth = size.Width;
        aim.CalibrationHeight = size.Height;
        aim.SaveCommand.Execute(null);
        if (!aim.LastSaveSucceeded)
        {
            aim.GeometryMode = 0;
            CameraSetupNotice = aim.Status;
            return;
        }
        CameraSetupNotice = $"已保存 {aim.CalibrationWidth}×{aim.CalibrationHeight}、水平 60° / 垂直 45° 估算。此参数未经实测标定，舵机边界与方向保持原设置。";
    }
    [RelayCommand(CanExecute = nameof(CanFollow))]
    private void StartFollow()
    {
        if (!CanFollow) return;
        Tracking.SourceIndex = 3;
        Tracking.TargetIndex = 1;
        Tracking.AlgorithmIndex = 0;
        // Recheck after configuring the source; the tracking owner still gates actual output.
        if (Tracking.CanStartFollow) requestFollow();
    }
    [RelayCommand] private async Task OpenExpressionAsync()
    {
        await NavigateAsync("1");
        if (!Tracking.CalibrationActive) AdvancedRequested?.Invoke(4);
    }
    [RelayCommand] private void OpenSensors() => SensorsRequested?.Invoke();
    [RelayCommand] private void ShowPrivacy() => SettingsSection = 2;
    [RelayCommand(CanExecute = nameof(CanStartVision))]
    private async Task StartVisionAsync()
    {
        if (!CanStartVision) return;
        Tracking.SourceIndex = 3;
        Tracking.TargetIndex = 1;
        Tracking.AlgorithmIndex = 0;
        await Tracking.StartAsync(); // Preview only; never changes arming, power or follow readiness.
    }

    [RelayCommand]
    private async Task StopCaptureAsync()
    {
        if (Tracking.CalibrationActive && StopCalibrationAsync is { } stop) await stop();
        if (Tracking.IsRunning) await Tracking.StopAsync();
    }

    internal async Task<string?> HandleVoiceCommandAsync(string text, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var command = VoiceCommands.Match(text);
        string Notice(string message) => VoiceCommandNotice = message;
        if (command == VoiceCommand.StopFollow)
        {
            if (!Tracking.IsFollowing) return Notice("摄像机跟随当前未开启。");
            await Tracking.StopAsync();
            return Notice("摄像机跟随已关闭。");
        }
        if (command != VoiceCommand.StartFollow) return null;
        if (Tracking.IsFollowing) return Notice("摄像机跟随已开启。");
        if (navigating || Tracking.IsChangingMode) return Notice("正在切换视觉会话，请稍后再说一次。");
        await NavigateAsync("1");
        cancellation.ThrowIfCancellationRequested();
        if (Tracking.CalibrationActive) return Notice("视觉标定仍在结束，请稍后再试。");
        if (Tracking.SelectedCamera is null && !Tracking.IsRunning) await Tracking.RefreshCamerasAsync();
        cancellation.ThrowIfCancellationRequested();
        if (!Tracking.Servo.FirmwareReady && !Tracking.Servo.Busy)
            await Tracking.Servo.InspectCommand.ExecuteAsync(null);
        cancellation.ThrowIfCancellationRequested();
        Tracking.SourceIndex = 3; Tracking.TargetIndex = 1; Tracking.AlgorithmIndex = 0;
        if (!CanFollow) return Notice("语音命令已识别，跟随尚未就绪。" + PreparationHint);
        StartFollow();
        return Notice("语音命令已提交，正在开启摄像机跟随。");
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MainViewModel.IsRunning) or nameof(MainViewModel.IsFollowing)
            or nameof(MainViewModel.SelectedCamera) or nameof(MainViewModel.CalibrationActive) or nameof(MainViewModel.CanConfigure) or nameof(MainViewModel.CanStart)
            or nameof(MainViewModel.CanStartFollow) or nameof(MainViewModel.CanStartCameraFollow) or nameof(MainViewModel.CameraFollowSessionHint) or nameof(MainViewModel.SourceLabel) or nameof(MainViewModel.HasPreview)
            or nameof(MainViewModel.ExpressionEnabled) or nameof(MainViewModel.HasError) or nameof(MainViewModel.HasServoNotice)
            or nameof(MainViewModel.IsChangingMode) or nameof(ServoViewModel.CanControl) or nameof(ServoViewModel.FirmwareReady)
            or nameof(ServoViewModel.CanFollow) or nameof(ServoViewModel.Ready) or nameof(ServoViewModel.TiltCalibrationSaved)
            or nameof(ServoViewModel.TiltCalibrating) or nameof(ServoViewModel.PanMotionBlocked) or nameof(ServoViewModel.TiltMotionBlocked) or nameof(ServoViewModel.FollowReadiness) or nameof(ServoViewModel.Busy)
            or nameof(FaceAimViewModel.GeometryMode) or nameof(FaceAimViewModel.DirectionsConfirmed) or nameof(FaceAimViewModel.Status))) return;
        RefreshStatus();
    }
    private void CamerasChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshStatus();
    private void ChatChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChatViewModel.RemoteRunning))
            OnPropertyChanged(nameof(RobotDetail));
    }
    private void VoiceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(VoiceViewModel.IsListening) or nameof(VoiceViewModel.IsSpeaking))
            OnPropertyChanged(nameof(RobotDetail));
    }
    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(RobotDetail));
        OnPropertyChanged(nameof(CameraState)); OnPropertyChanged(nameof(RobotConnection));
        OnPropertyChanged(nameof(SourceCaption)); OnPropertyChanged(nameof(CanFollow));
        OnPropertyChanged(nameof(CanStartVision));
        OnPropertyChanged(nameof(CanPrepareRobot)); OnPropertyChanged(nameof(PreparationHint)); OnPropertyChanged(nameof(FollowActionLabel));
        OnPropertyChanged(nameof(CanAdvanceSetup)); AdvanceSetupCommand.NotifyCanExecuteChanged(); PreviousSetupStepCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SetupMessage));
        OnPropertyChanged(nameof(NeedsCameraModel)); OnPropertyChanged(nameof(CanQuickConfigureCamera));
        OnPropertyChanged(nameof(StopLabel)); OnPropertyChanged(nameof(HasNotice));
        OnPropertyChanged(nameof(IsAnyVisionRunning));
        StartVisionCommand.NotifyCanExecuteChanged();
        StartFollowCommand.NotifyCanExecuteChanged();
        QuickConfigureCameraCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        Voice.LocalCommandHandler = null;
        Voice.PropertyChanged -= VoiceChanged; Voice.Dispose();
        SharedEsp32.CloseAll();
        Tracking.Lcd.CaptureChatRole = null;
        Tracking.PropertyChanged -= Changed;
        Tracking.Servo.PropertyChanged -= Changed;
        Tracking.FaceAim.PropertyChanged -= Changed;
        Tracking.Cameras.CollectionChanged -= CamerasChanged;
        Chat.PropertyChanged -= ChatChanged; Chat.Dispose();
        AdvancedRequested = null; SensorsRequested = null; StopCalibrationAsync = null;
    }
}
