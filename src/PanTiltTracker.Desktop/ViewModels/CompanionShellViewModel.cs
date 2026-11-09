using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PanTiltTracker.Desktop.ViewModels;

/// <summary>Product navigation and presentation over the existing acquisition/control owner.</summary>
public sealed partial class CompanionShellViewModel : ObservableObject, IDisposable
{
    public MainViewModel Tracking { get; }
    public event Action<int>? AdvancedRequested;
    public event Action? SensorsRequested;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompanionPage), nameof(IsDevicesPage), nameof(IsSettingsPage), nameof(PageTitle))]
    private int pageIndex;
    [ObservableProperty] private bool showVisualPanel = true;
    [ObservableProperty] private int settingsSection;
    public bool IsCompanionPage => PageIndex == 0;
    public bool IsDevicesPage => PageIndex == 1;
    public bool IsSettingsPage => PageIndex == 2;
    public string PageTitle => PageIndex switch { 1 => "设备", 2 => "设置", _ => "陪伴" };
    public string RobotState => Tracking.IsRunning ? "感知中" : "待机中";
    public string RobotDetail => Tracking.IsRunning ? Tracking.IsFace && Tracking.ExpressionEnabled
        ? "正在观察可见的表情线索" : "正在预览" + Tracking.SourceLabel : "准备好了，随时开始";
    public string CameraState => Tracking.IsRunning ? Tracking.SourceLabel + " · 运行中"
        : Tracking.SelectedCamera is null ? "未开启 · 请选择摄像头" : "已发现 · 未开启";
    public string RobotConnection => Tracking.IsFollowing ? "云台跟随中" : Tracking.Servo.CanControl ? "云台已就绪" : "云台待确认";
    public string FollowState => Tracking.IsFollowing ? "实物跟随中" : "实物跟随已关闭";
    public string SourceCaption => !Tracking.HasPreview ? "开启摄像头后显示画面"
        : Tracking.IsCameraSource && !Tracking.IsRunning ? "USB 摄像头 · 已停止" : Tracking.SourceLabel;
    public string StopLabel => Tracking.IsFollowing ? "停止跟随" : "停止感知";
    public bool HasNotice => Tracking.HasError || Tracking.HasServoNotice;
    public bool CanStartVision => Tracking.CanStart && Tracking.SelectedCamera is not null;
    public bool CanFollow => Tracking.CanStartFollow;
    public string ConversationStatus => "对话服务尚未接入";

    public CompanionShellViewModel(MainViewModel tracking)
    {
        Tracking = tracking;
        tracking.PropertyChanged += Changed;
        tracking.Servo.PropertyChanged += Changed;
        tracking.Cameras.CollectionChanged += CamerasChanged;
    }
    [RelayCommand] private void Navigate(string page) => PageIndex = int.Parse(page, System.Globalization.CultureInfo.InvariantCulture);
    [RelayCommand] private void OpenAdvanced() => AdvancedRequested?.Invoke(0);
    [RelayCommand] private void OpenCalibration() => AdvancedRequested?.Invoke(2);
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
            or nameof(ServoViewModel.CanControl))) return;
        RefreshStatus();
    }
    private void CamerasChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshStatus();
    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(RobotState)); OnPropertyChanged(nameof(RobotDetail));
        OnPropertyChanged(nameof(CameraState)); OnPropertyChanged(nameof(RobotConnection));
        OnPropertyChanged(nameof(SourceCaption)); OnPropertyChanged(nameof(CanFollow));
        OnPropertyChanged(nameof(CanStartVision)); OnPropertyChanged(nameof(FollowState));
        OnPropertyChanged(nameof(StopLabel)); OnPropertyChanged(nameof(HasNotice));
        StartVisionCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        Tracking.PropertyChanged -= Changed;
        Tracking.Servo.PropertyChanged -= Changed;
        Tracking.Cameras.CollectionChanged -= CamerasChanged;
        AdvancedRequested = null; SensorsRequested = null;
    }
}
