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

namespace PanTiltTracker.Desktop.ViewModels;

public readonly record struct ErrorSample(double? X, double? Y);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly string workspace;
    private readonly ControllerSettings initialSettings;
    private readonly List<ErrorSample> history = [];
    private CancellationTokenSource? cancellation;
    private Task? activeSession;
    private readonly Stopwatch displayClock = new();
    private long detectedFrames;
    private bool disposed;
    private bool cameraFaulted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigure), nameof(CanExport), nameof(CanOcclude), nameof(RunLabel))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ResetParametersCommand))]
    private bool isRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport), nameof(RunLabel))]
    private bool hasRun;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileSource), nameof(IsCameraSource), nameof(IsSimulation),
        nameof(CanOcclude), nameof(SourceDescription), nameof(SourceLabel))]
    private int sourceIndex;
    [ObservableProperty] private string inputPath = "";
    [ObservableProperty] private int cameraIndex;
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
    public event Action? FramePresented;
    public bool CanConfigure => !IsRunning;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool CanExport => !IsRunning && HasRun && File.Exists(Path.Combine(LastRunDirectory, "telemetry.csv"));
    public bool CanStart => !IsRunning && !(IsCameraSource && cameraFaulted);
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
        _ => "请求 640×480 / 30fps，实际模式以设备返回为准。"
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
        AddEvent("实验台已准备好。选择合成场景即可开始。");
    }

    partial void OnSourceIndexChanged(int value)
    {
        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStart));
        if (!IsRunning) ResetPresentation();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        if (!CanStart) return;
        ErrorMessage = "";
        if (MarkerId is < 0 or >= 50 || CameraIndex < 0)
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
        var settings = initialSettings with
        {
            PanGain = PanGain, TiltGain = TiltGain, MaxSpeedDegreesPerSecond = MaxSpeed,
            DeadZone = DeadZone, PanDirection = ReversePan ? -1 : 1, TiltDirection = ReverseTilt ? -1 : 1
        };
        try { settings.Validate(); }
        catch (ArgumentException) { ErrorMessage = "参数无效，请检查增益、速度和死区。"; return; }

        LastRunDirectory = Path.Combine(workspace, "artifacts", "desktop",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "Z");
        var options = new SessionOptions((InputSource)SourceIndex, InputPath, CameraIndex,
            MarkerId, settings, LastRunDirectory);
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
        AddEvent($"开始：{SourceLabel}，标记 ID {MarkerId}。硬件输出保持关闭。");
        cancellation = new();
        try
        {
            activeSession = TrackingSession.RunAsync(options, frame =>
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!disposed) Present(frame);
                }).GetTask(), () => HideTarget, cancellation.Token);
            await activeSession;
            StateLabel = "处理完成";
        }
        catch (OperationCanceledException) { StateLabel = "已停止"; }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
            StateLabel = "运行异常";
            if (IsCameraSource && error.Message.Contains("重启应用", StringComparison.Ordinal)) cameraFaulted = true;
            AddEvent($"错误：{error.Message}");
        }
        finally
        {
            displayClock.Stop();
            cancellation.Dispose();
            cancellation = null;
            activeSession = null;
            IsRunning = false;
            OnPropertyChanged(nameof(CanExport));
            AddEvent(File.Exists(Path.Combine(LastRunDirectory, "telemetry.csv"))
                ? $"结束：已处理 {ProcessedFrames} 帧，日志已保存。"
                : $"结束：已处理 {ProcessedFrames} 帧，未生成日志。");
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (activeSession is not null)
        {
            try { await activeSession; }
            catch (OperationCanceledException) { }
            catch (Exception) { /* StartAsync reports the session error in the UI. */ }
        }
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

    private void Present(SessionFrame frame)
    {
        var observation = frame.Observation;
        var command = frame.Command;
        ProcessedFrames++;
        if (observation.HasTarget) detectedFrames++;
        DetectionLabel = observation.HasTarget ? $"已检测 · ID {MarkerId}" : "未检测到目标";
        TargetPosition = observation.HasTarget ? $"({observation.CenterX:0.0}, {observation.CenterY:0.0}) px" : "—";
        ErrorX = Format(command.ErrorX);
        ErrorY = Format(command.ErrorY);
        PanCommand = $"{command.PanDegrees:0.0}°";
        TiltCommand = $"{command.TiltDegrees:0.0}°";
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
        if (StateLabel != previous && command.State is TrackingState.Lost or TrackingState.Stale)
            AddEvent(StateLabel);
        history.Add(new(command.ErrorX, command.ErrorY));
        if (history.Count > 180) history.RemoveAt(0);
        PlotSamples = history.ToArray();
        if (Preview is null || Preview.PixelSize.Width != frame.Width || Preview.PixelSize.Height != frame.Height)
        {
            Preview?.Dispose();
            Preview = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var locked = Preview.Lock())
        {
            for (var row = 0; row < frame.Height; row++)
                Marshal.Copy(frame.Pixels, row * frame.Width * 4,
                    IntPtr.Add(locked.Address, row * locked.RowBytes), frame.Width * 4);
        }
        HasPreview = true;
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
        ErrorMessage = "";
        Preview?.Dispose();
        Preview = null;
        HasPreview = false;
        HasRun = false;
        ProcessedFrames = 0;
        ErrorX = ErrorY = PanCommand = TiltCommand = ProcessingTime = ProcessingRate = DetectionRate = "—";
        DetectionLabel = "等待图像";
        TargetPosition = "—";
        StateLabel = "准备就绪";
        Elapsed = "00:00.0";
        history.Clear();
        PlotSamples = Array.Empty<ErrorSample>();
    }

    private static string Format(double? value) => value?.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) ?? "—";
    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PanTiltTracker.sln"))) return directory.FullName;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanTiltTracker");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation?.Cancel();
        Preview?.Dispose();
        FramePresented = null;
    }
}
