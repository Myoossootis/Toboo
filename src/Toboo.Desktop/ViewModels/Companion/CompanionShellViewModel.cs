using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Toboo.Desktop.ViewModels;

/// <summary>Product navigation and presentation over the existing acquisition/control owner.</summary>
public sealed partial class CompanionShellViewModel : ObservableObject, IDisposable
{
    public MainViewModel Tracking { get; }
    public ChatViewModel Chat { get; }
    public event Action<int>? AdvancedRequested;
    public event Action? SensorsRequested;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompanionPage), nameof(IsDevicesPage), nameof(IsSettingsPage), nameof(PageTitle))]
    private int pageIndex;
    [ObservableProperty] private bool showVisualPanel = true;
    [ObservableProperty] private int settingsSection;
    [ObservableProperty] private bool calibrationExpanded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupConnectionStep), nameof(SetupBoundsStep), nameof(SetupDirectionStep), nameof(SetupStepLabel), nameof(SetupNextLabel), nameof(CanAdvanceSetup), nameof(SetupMessage))]
    [NotifyCanExecuteChangedFor(nameof(AdvanceSetupCommand), nameof(PreviousSetupStepCommand))]
    private int setupStep;
    public bool SetupConnectionStep => SetupStep == 0;
    public bool SetupBoundsStep => SetupStep == 1;
    public bool SetupDirectionStep => SetupStep == 2;
    public string SetupStepLabel => $"快速准备 · {SetupStep + 1}/3";
    public string SetupNextLabel => SetupStep == 2 ? "保存并完成" : "下一步";
    public string SetupMessage => Tracking.FaceAim.Status.StartsWith("未保存", StringComparison.Ordinal)
        ? Tracking.FaceAim.Status
        : Tracking.FaceAim.GeometryMode == 0 ? "还缺相机参数，请在高级设置中配置。"
        : !Tracking.FaceAim.DirectionsConfirmed ? "请先通过微调检查两轴方向。" : "保存后会复用这组设置，无需每次重做。";
    public bool CanAdvanceSetup => CanPrepareRobot && !Tracking.Servo.Busy && (SetupStep switch
    {
        0 => Tracking.Servo.FirmwareReady && Tracking.Servo.Ready,
        1 => Tracking.Servo.TiltCalibrationSaved && !Tracking.Servo.TiltCalibrating,
        _ => Tracking.FaceAim.GeometryMode > 0 && Tracking.FaceAim.DirectionsConfirmed
    });
    public bool IsCompanionPage => PageIndex == 0;
    public bool IsDevicesPage => PageIndex == 1;
    public bool IsSettingsPage => PageIndex == 2;
    public string PageTitle => PageIndex switch { 1 => "设备", 2 => "设置", _ => "陪伴" };
    public string RobotState => Chat.RemoteRunning ? "正在回应" : Tracking.IsRunning ? "感知中" : "待机中";
    public string RobotDetail => Chat.RemoteRunning ? "正在认真组织回复" : Tracking.IsRunning ? Tracking.IsFace && Tracking.ExpressionEnabled
        ? "正在观察可见的表情线索" : "正在预览" + Tracking.SourceLabel : "准备好了，随时开始";
    public string CameraState => Tracking.IsRunning ? Tracking.SourceLabel + " · 运行中"
        : Tracking.SelectedCamera is null ? "未开启 · 请选择摄像头" : "已发现 · 未开启";
    public string RobotConnection => Tracking.IsFollowing ? "云台跟随中" : CanFollow ? "跟随已就绪" : Tracking.Servo.FirmwareReady ? "已连接 · 待准备" : "云台待确认";
    public string FollowState => Tracking.IsFollowing ? "实物跟随中" : "实物跟随已关闭";
    public string SourceCaption => !Tracking.HasPreview ? "开启摄像头后显示画面"
        : Tracking.IsCameraSource && !Tracking.IsRunning ? "USB 摄像头 · 已停止" : Tracking.SourceLabel;
    public string StopLabel => Tracking.IsFollowing ? "停止跟随" : "停止感知";
    public bool HasNotice => Tracking.HasError || Tracking.HasServoNotice;
    public bool CanStartVision => Tracking.CanStart && Tracking.SelectedCamera is not null;
    public bool CanFollow => Tracking.CanStartFollow && Tracking.FaceAim.GeometryMode > 0 && Tracking.SelectedCamera is not null;
    public bool CanPrepareRobot => !Tracking.IsFollowing && !Tracking.IsChangingMode;
    public string FollowActionLabel => Tracking.IsChangingMode ? "正在切换…"
        : Tracking.IsRunning ? "从预览切换到跟随" : "开启实物跟随";
    public string PreparationHint => Tracking.IsFollowing ? "跟随已开启，可随时停止；停止不会切断动力。"
        : Tracking.IsChangingMode ? "正在结束预览并检查跟随条件…"
        : !Tracking.Servo.FirmwareReady ? "1 · 选择 ESP32 串口，连接并读状态。"
        : !Tracking.Servo.Ready ? "2 · 确认 5V 供电与机构活动空间。"
        : !Tracking.Servo.TiltCalibrationSaved || Tracking.Servo.TiltCalibrating ? "3 · 首次使用请校准并保存工作边界。"
        : !Tracking.Servo.CanFollow ? Tracking.Servo.FollowReadiness
        : Tracking.FaceAim.GeometryMode == 0 ? "4 · 在校准中设置本摄像头的角度模型。"
        : !Tracking.FaceAim.DirectionsConfirmed ? "4 · 检查两轴方向，确认并保存跟随设置。"
        : Tracking.SelectedCamera is null ? "5 · 选择 USB 摄像头；可先预览，再直接切换到跟随。"
        : "已就绪。预览可直接切换到跟随，保存的校准无需每次重做。";

    public CompanionShellViewModel(MainViewModel tracking, ChatViewModel? chat = null)
    {
        Tracking = tracking;
        Chat = chat ?? new(expression: tracking.CaptureExpressionCue);
        tracking.Lcd.CaptureChatRole = Chat.CaptureLcdRole;
        Chat.PropertyChanged += ChatChanged;
        tracking.PropertyChanged += Changed;
        tracking.Servo.PropertyChanged += Changed;
        tracking.FaceAim.PropertyChanged += Changed;
        tracking.Cameras.CollectionChanged += CamerasChanged;
    }

    [RelayCommand] private void Navigate(string page) => PageIndex = int.Parse(page, System.Globalization.CultureInfo.InvariantCulture);
    [RelayCommand] private void OpenAdvanced() => AdvancedRequested?.Invoke(0);
    [RelayCommand] private void OpenCalibration() { PageIndex = 1; SetupStep = 0; CalibrationExpanded = !CalibrationExpanded; }
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
    [RelayCommand] private void OpenExpression() => AdvancedRequested?.Invoke(4);
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
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MainViewModel.IsRunning) or nameof(MainViewModel.IsFollowing)
            or nameof(MainViewModel.SelectedCamera) or nameof(MainViewModel.CanConfigure) or nameof(MainViewModel.CanStart)
            or nameof(MainViewModel.CanStartFollow) or nameof(MainViewModel.SourceLabel) or nameof(MainViewModel.HasPreview)
            or nameof(MainViewModel.ExpressionEnabled) or nameof(MainViewModel.HasError) or nameof(MainViewModel.HasServoNotice)
            or nameof(MainViewModel.IsChangingMode) or nameof(ServoViewModel.CanControl) or nameof(ServoViewModel.FirmwareReady)
            or nameof(ServoViewModel.CanFollow) or nameof(ServoViewModel.Ready) or nameof(ServoViewModel.TiltCalibrationSaved)
            or nameof(ServoViewModel.TiltCalibrating) or nameof(ServoViewModel.FollowReadiness)
            or nameof(FaceAimViewModel.GeometryMode) or nameof(FaceAimViewModel.DirectionsConfirmed) or nameof(FaceAimViewModel.Status))) return;
        RefreshStatus();
    }
    private void CamerasChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshStatus();
    private void ChatChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChatViewModel.RemoteRunning))
        { OnPropertyChanged(nameof(RobotState)); OnPropertyChanged(nameof(RobotDetail)); }
    }
    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(RobotState)); OnPropertyChanged(nameof(RobotDetail));
        OnPropertyChanged(nameof(CameraState)); OnPropertyChanged(nameof(RobotConnection));
        OnPropertyChanged(nameof(SourceCaption)); OnPropertyChanged(nameof(CanFollow));
        OnPropertyChanged(nameof(CanStartVision)); OnPropertyChanged(nameof(FollowState));
        OnPropertyChanged(nameof(CanPrepareRobot)); OnPropertyChanged(nameof(PreparationHint)); OnPropertyChanged(nameof(FollowActionLabel));
        OnPropertyChanged(nameof(CanAdvanceSetup)); AdvanceSetupCommand.NotifyCanExecuteChanged(); PreviousSetupStepCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SetupMessage));
        OnPropertyChanged(nameof(StopLabel)); OnPropertyChanged(nameof(HasNotice));
        StartVisionCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        Tracking.Lcd.CaptureChatRole = null;
        Tracking.PropertyChanged -= Changed;
        Tracking.Servo.PropertyChanged -= Changed;
        Tracking.FaceAim.PropertyChanged -= Changed;
        Tracking.Cameras.CollectionChanged -= CamerasChanged;
        Chat.PropertyChanged -= ChatChanged; Chat.Dispose();
        AdvancedRequested = null; SensorsRequested = null;
    }
}
