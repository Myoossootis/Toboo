using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Toboo.Core;
using Toboo.Desktop.Services;
using Toboo.Desktop.ViewModels;
using Toboo.Vision;

namespace Toboo.Desktop;

public sealed partial class MappingWindow : Window
{
    private readonly CameraDevice? camera;
    private readonly LcdViewModel? lcd;
    private readonly string calibrationPath;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<OpenCvSharp.Point2f[]> samples = [];
    private MappingIntrinsics? intrinsics;
    private ObstacleMap? map;
    private CancellationTokenSource? active;
    private Task? cameraJob;
    private Bitmap? preview;
    private Bitmap? depthPreview;
    private bool hasDepthPreview;
    private long origin;
    private bool reading, closeAfterStop, calibrating;
    private int requestSample, sampleWidth, sampleHeight;

    public MappingWindow() : this(null, null) { }

    internal MappingWindow(CameraDevice? camera, LcdViewModel? lcd, string? calibrationPath = null)
    {
        this.camera = camera;
        this.lcd = lcd;
        this.calibrationPath = calibrationPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PanTiltTracker", "mapping-camera.local.json");
        InitializeComponent();
        timer.Tick += PollRange;
        DeviceStatus.Text = $"{camera?.Name ?? "未选择 USB 摄像头"} · {UnoStatus()}";
        StartButton.IsEnabled = camera is not null;
        LoadCalibration();
    }

    private sealed record SavedCalibration(int CameraIndex, string CameraName, MappingIntrinsics Intrinsics);

    private void LoadCalibration()
    {
        CalibrationStatus.Text = "尚未标定此摄像头。";
        if (camera is null || !File.Exists(calibrationPath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedCalibration>(File.ReadAllText(calibrationPath));
            if (saved is { Intrinsics.IsValid: true } && saved.CameraIndex == camera.Index && saved.CameraName == camera.Name)
            {
                intrinsics = saved.Intrinsics;
                CalibrationStatus.Text = CalibrationLabel(intrinsics);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { CalibrationStatus.Text = "无法读取相机标定，请重新采样。"; }
    }

    private string UnoStatus() => lcd?.CanReadRange == true ? "Uno 表情屏与超声波已连接" :
        lcd?.IsConnected == true ? "Uno 尚未启用超声波" :
        $"请在设备页连接装有合并固件的 Uno{(lcd is null ? "" : $"（{lcd.Status}）")}";

    private void UpdateDevices() => DeviceStatus.Text = $"实物采集 · {camera!.Name} · {UnoStatus()}";

    private static string CalibrationLabel(MappingIntrinsics value) =>
        $"已标定 {value.Width} × {value.Height} · 重投影 RMS {value.RmsPixels:0.00} px";

    private void StartCamera(object? sender, RoutedEventArgs args)
    {
        if (camera is null || active is not null) return;
        var mount = new RangeMount((double)(RightOffset.Value ?? 0) / 1000,
            (double)(DownOffset.Value ?? 0) / 1000, (double)(ForwardOffset.Value ?? 0) / 1000);
        map = new(mount, (int)(NearDistance.Value ?? 350));
        var tagMeters = (double)(TagSize.Value ?? 100) / 1000;
        origin = Stopwatch.GetTimestamp();
        var opening = active = new();
        var cancel = opening.Token;
        StartButton.IsEnabled = DemoButton.IsEnabled = PhotoButton.IsEnabled = MountInputs.IsEnabled = false;
        StopButton.IsEnabled = true;
        SampleButton.IsEnabled = !calibrating;
        CameraPreview.Source = null;
        preview?.Dispose();
        preview = null;
        hasDepthPreview = false;
        DepthPreview.Source = null; depthPreview?.Dispose(); depthPreview = null;
        Cloud.Show([]);
        DepthStatus.Text = "正在载入深度模型…";
        CameraPlaceholder.IsVisible = true;
        CameraPlaceholder.Text = "正在打开摄像头…";
        PoseStatus.Text = "正在打开相机…";
        UpdateDevices();
        cameraJob = RunCameraAsync(camera.Index, origin, tagMeters, opening, cancel);
        timer.Start();
        UpdateMap(TimeSpan.Zero);
    }

    private async Task RunCameraAsync(int index, long started, double tagMeters, CancellationTokenSource opening, CancellationToken token)
    {
        try
        {
            await Task.Run(() => MappingCameraSession.RunAsync(index, new(), started,
                () => Volatile.Read(ref intrinsics), tagMeters, () => Interlocked.Exchange(ref requestSample, 0) != 0,
                async frame => await Dispatcher.UIThread.InvokeAsync(() => Present(frame, token)), token,
                async (depth, at) => await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                        PresentDepth(depth, $"实时估计 · 输入距今 {Stopwatch.GetElapsedTime(started).TotalSeconds - at.TotalSeconds:0.0} s");
                }),
                message => Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested) DepthStatus.Text = "深度预览不可用 · " + message;
                })), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or OpenCvSharp.OpenCVException)
        { PoseStatus.Text = "摄像头采集结束 · " + error.Message; }
        finally
        {
            opening.Cancel(); timer.Stop(); active = null;
            opening.Dispose();
            Interlocked.Exchange(ref requestSample, 0);
            StartButton.IsEnabled = camera is not null;
            DemoButton.IsEnabled = PhotoButton.IsEnabled = MountInputs.IsEnabled = true;
            StopButton.IsEnabled = SampleButton.IsEnabled = false;
            Advice.Text = "采集已停止";
            RangeStatus.Text = "距离 — · 采集已停止";
            DepthStatus.Text = "已停止 · 保留最后一帧";
            CameraPlaceholder.Text = "摄像头未开启";
            Map.Show(map, false);
        }
    }

    private void Present(MappingFrame frame, CancellationToken token)
    {
        if (token.IsCancellationRequested || map is null) return;
        var now = Stopwatch.GetElapsedTime(origin);
        var pose = now - frame.At <= ObstacleMap.PairWindow ? frame.Pose : null;
        map.ObservePose(frame.At, pose);
        if (!hasDepthPreview || frame.ChessboardRequested) ReplaceImage(CameraPreview, ref preview, frame.Jpeg);
        CameraPlaceholder.IsVisible = false;
        PoseStatus.Text = pose is { } located
            ? $"ArUco 7 已定位 · X {located.Position.X:0.00} m · Z {located.Position.Z:0.00} m"
            : intrinsics is null ? "先标定相机，再看向 ArUco 7。"
            : frame.Width != intrinsics.Width || frame.Height != intrinsics.Height ? "采集尺寸与标定不一致，请重新标定。"
            : frame.TagVisible ? "标记可见，位姿质量不足。" : "未看到 ArUco 7 · 地图暂停更新";
        if (frame.ChessboardRequested) _ = AcceptSampleAsync(frame, token);
        UpdateMap(now);
    }

    private async void PollRange(object? sender, EventArgs args)
    {
        if (active is null || map is null) return;
        var now = Stopwatch.GetElapsedTime(origin);
        if (lcd?.CanReadRange != true)
        {
            if (map.DistanceMm.HasValue) map.ObserveRange(now, null);
            UpdateMap(now);
            UpdateDevices();
            return;
        }
        UpdateMap(now);
        if (reading) return;
        var token = active.Token;
        var current = map;
        reading = true;
        try
        {
            if (await lcd.ReadRangeAsync(token) is { } sample && !token.IsCancellationRequested)
                current.ObserveRange(Stopwatch.GetElapsedTime(origin, sample.Timestamp), sample.Millimeters);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (!token.IsCancellationRequested)
            {
                current.ObserveRange(Stopwatch.GetElapsedTime(origin), null);
                UpdateDevices();
            }
        }
        finally
        {
            reading = false;
            if (!token.IsCancellationRequested) UpdateMap(Stopwatch.GetElapsedTime(origin));
        }
    }

    private void UpdateMap(TimeSpan now)
    {
        if (map is null) return;
        var advice = map.Advice(now);
        Advice.Text = advice switch
        {
            ObstacleAdvice.Stop => "停止建议 · 前方有近障",
            ObstacleAdvice.NoNearEcho => "前方波束内未见近障",
            _ => "信息不足 · 等待有效观测"
        };
        Advice.Foreground = advice == ObstacleAdvice.Stop ? Brush.Parse("#A64923") : Brush.Parse("#234540");
        RangeStatus.Text = map.FreshDistance(now) is { } mm ? $"前方 {mm} mm" : "距离 — · 无回波、过期或未连接";
        var fresh = map.HasFreshPose(now);
        if (!fresh && map.Pose.HasValue) PoseStatus.Text = "画面已过期 · 地图暂停更新";
        MapStatus.Text = $"{map.MappedEchoes} 次回波入图 · {map.Path.Count} 个轨迹点 · 空白为未知";
        Map.Show(map, fresh);
    }

    private void SampleCalibration(object? sender, RoutedEventArgs args)
    {
        if (active is null || calibrating) return;
        Interlocked.Exchange(ref requestSample, 1);
        SampleButton.IsEnabled = false;
        CalibrationStatus.Text = "正在读取棋盘格…";
    }

    private async Task AcceptSampleAsync(MappingFrame frame, CancellationToken token)
    {
        SampleButton.IsEnabled = true;
        if (frame.ChessboardCorners is not { } corners)
        { CalibrationStatus.Text = $"未找到完整棋盘格 · 已采 {samples.Count}/12"; return; }
        if (sampleWidth != frame.Width || sampleHeight != frame.Height) samples.Clear();
        sampleWidth = frame.Width; sampleHeight = frame.Height;
        if (!MarkerMappingVision.IsNewCalibrationView(samples, corners, frame.Width))
        { CalibrationStatus.Text = $"取景与已有样本太接近 · 已采 {samples.Count}/12"; return; }
        samples.Add(corners);
        if (samples.Count < MarkerMappingVision.CalibrationSamples)
        { CalibrationStatus.Text = $"已采 {samples.Count}/12 · 换个位置或角度"; return; }
        calibrating = true; SampleButton.IsEnabled = false;
        CalibrationStatus.Text = "正在计算相机内参…";
        var captured = samples.ToArray(); samples.Clear();
        try
        {
            var done = await Task.Run(() => MarkerMappingVision.Calibrate(captured, frame.Width, frame.Height), token);
            if (token.IsCancellationRequested) return;
            Directory.CreateDirectory(Path.GetDirectoryName(calibrationPath)!);
            var temporary = calibrationPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new SavedCalibration(camera!.Index, camera.Name, done)), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, calibrationPath, overwrite: true);
            Volatile.Write(ref intrinsics, done);
            CalibrationStatus.Text = CalibrationLabel(done);
            map = new(new((double)(RightOffset.Value ?? 0) / 1000, (double)(DownOffset.Value ?? 0) / 1000,
                (double)(ForwardOffset.Value ?? 0) / 1000), (int)(NearDistance.Value ?? 350));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OpenCvSharp.OpenCVException or ArgumentException)
        { if (!token.IsCancellationRequested) CalibrationStatus.Text = "标定失败 · " + error.Message; }
        finally { calibrating = false; SampleButton.IsEnabled = active is not null; }
    }

    private async void SavePatterns(object? sender, RoutedEventArgs args)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new() { Title = "选择标定图保存位置" });
        if (dirs.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            MarkerMappingVision.WritePatterns(path);
            CalibrationStatus.Text = "已保存棋盘格和 7 号标记。保持等比例打印，测量标记黑边边长。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OpenCvSharp.OpenCVException)
        { CalibrationStatus.Text = "保存失败 · " + error.Message; }
    }

    private void ShowDemo(object? sender, RoutedEventArgs args) => DisplayDemo();

    private static void ReplaceImage(Image target, ref Bitmap? current, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var next = new Bitmap(stream);
        var previous = current;
        current = next; target.Source = next; previous?.Dispose();
    }

    private void PresentDepth(DepthPreviewFrame frame, string source)
    {
        ReplaceImage(CameraPreview, ref preview, frame.ColorJpeg);
        ReplaceImage(DepthPreview, ref depthPreview, frame.DepthPng);
        CameraPlaceholder.IsVisible = false;
        hasDepthPreview = true;
        Cloud.Show(frame.Points);
        DepthStatus.Text = $"{source} · {frame.InferenceMilliseconds:0} ms · {frame.Points.Length:N0} 个采样点";
    }

    private void ResetCloudView(object? sender, RoutedEventArgs args) => Cloud.ResetView();

    private async void OpenPhoto(object? sender, RoutedEventArgs args)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "选择空间预览照片", AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await LoadPhotoAsync(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { DepthStatus.Text = "打开照片失败 · " + error.Message; }
    }

    internal Task LoadPhotoAsync(string path)
    {
        if (active is not null) return Task.CompletedTask;
        var opening = active = new();
        cameraJob = InferPhotoAsync(path, opening);
        return cameraJob;
    }

    private async Task InferPhotoAsync(string path, CancellationTokenSource opening)
    {
        var token = opening.Token;
        timer.Stop(); map = null; Map.Show(null, false);
        StartButton.IsEnabled = PhotoButton.IsEnabled = DemoButton.IsEnabled = MountInputs.IsEnabled = false;
        StopButton.IsEnabled = true;
        Cloud.Show([]);
        CameraPreview.Source = DepthPreview.Source = null;
        preview?.Dispose(); preview = null; depthPreview?.Dispose(); depthPreview = null;
        CameraPlaceholder.IsVisible = true;
        CameraPlaceholder.Text = "正在分析照片…";
        DeviceStatus.Text = "照片分析 · 本机相对深度估计";
        DepthStatus.Text = "正在载入模型并推理…";
        Advice.Text = "照片模式"; RangeStatus.Text = "实测距离 —";
        PoseStatus.Text = "照片预览"; MapStatus.Text = "实时定位后积累地图";
        try
        {
            var result = await Task.Run(() =>
            {
                using var image = OpenCvSharp.Cv2.ImDecode(File.ReadAllBytes(path), OpenCvSharp.ImreadModes.Color);
                using var model = new RelativeDepthPreview();
                return model.Infer(image);
            }, token);
            token.ThrowIfCancellationRequested();
            PresentDepth(result, "照片估计");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { DepthStatus.Text = "照片分析已停止"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            OpenCvSharp.OpenCVException or ArgumentException)
        { DepthStatus.Text = "照片分析失败 · " + error.Message; }
        finally
        {
            active = null; opening.Dispose();
            StartButton.IsEnabled = camera is not null;
            PhotoButton.IsEnabled = DemoButton.IsEnabled = MountInputs.IsEnabled = true;
            StopButton.IsEnabled = false;
            CameraPlaceholder.Text = "打开照片或开启摄像头";
        }
    }

    internal void DisplayDemo()
    {
        if (active is not null) return;
        var example = new ObstacleMap(default, 350);
        for (var i = 0; i <= 26; i++)
        {
            var time = TimeSpan.FromMilliseconds(i * 80);
            var z = 2.85 - i * 0.08;
            var x = 0.3 * Math.Cos(i / 26.0 * Math.PI / 2);
            var delta = new MapPoint(-x, 0.5 - z);
            var forward = delta * (1 / delta.Length);
            example.ObservePose(time, new(new(x, z), new(-forward.Z, forward.X), default, forward));
            example.ObserveRange(time, (int)(delta.Length * 1000));
        }
        map = example;
        DeviceStatus.Text = "合成演示 · 同一个纸箱从不同位置被观测";
        PoseStatus.Text = "合成位姿与距离 · 摄像头未开启";
        PresentDepth(RelativeDepthPreview.CreateDemo(), "合成几何演示");
        CalibrationOptions.IsExpanded = false;
        UpdateMap(TimeSpan.FromMilliseconds(26 * 80));
    }

    private void StopCamera(object? sender, RoutedEventArgs args) => active?.Cancel();

    protected override async void OnClosing(WindowClosingEventArgs args)
    {
        base.OnClosing(args);
        if (active is not null && cameraJob is { } stopping)
        {
            args.Cancel = true;
            if (closeAfterStop) return;
            closeAfterStop = true; active.Cancel();
            await stopping;
            Close();
        }
    }

    protected override void OnClosed(EventArgs args)
    {
        timer.Stop(); timer.Tick -= PollRange;
        CameraPreview.Source = null; preview?.Dispose();
        DepthPreview.Source = null; depthPreview?.Dispose();
        Cloud.Show([]);
        base.OnClosed(args);
    }
}
