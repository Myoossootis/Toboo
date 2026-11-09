using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toboo.Core;
using Toboo.Runtime;
using Toboo.Desktop.Services;

namespace Toboo.Desktop.ViewModels;

public sealed partial class SensorViewModel : ObservableObject, IDisposable
{
    private SensorTelemetry telemetry = new();
    private CompanionFusionPreview fusion = new();
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer;
    private Process? process;
    private StreamWriter? log;
    private bool resume;
    private bool disposed;
    private bool stopping;
    private double lastEventMilliseconds;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DemoCommand), nameof(ConnectCommand), nameof(StopCommand), nameof(ResumeCommand), nameof(RefreshPortsCommand))]
    private bool busy;
    [ObservableProperty] private string port = "";
    [ObservableProperty] private string[] availablePorts = [];
    [ObservableProperty] private string portsHint = "请选择传感器 Uno 串口。";
    [ObservableProperty] private string source = "未连接 · 融合预览不控制舵机";
    [ObservableProperty] private string acceleration = "等待加速度";
    [ObservableProperty] private string distance = "等待距离";
    [ObservableProperty] private string state = "等待传感器";
    [ObservableProperty] private string reason = "先运行合成演示，或核对 UNO 端口后连接。";
    [ObservableProperty] private string statistics = "拒收 0 · 丢包 0";
    [ObservableProperty] private string logPath = "";
    public FusionPreview? Latest { get; private set; }
    public bool IsLive { get; private set; }

    public SensorViewModel()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += Tick;
        RefreshPorts();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void RefreshPorts()
    {
        if (!CanStart()) return;
        var selected = Port;
        try
        {
            var ports = SerialPortCatalog.List();
            AvailablePorts = ports;
            Port = ports.FirstOrDefault(value => value == selected) ?? "";
            PortsHint = ports.Length == 0 ? "未发现串口，请连接传感器 Uno 后刷新。" : "请选择传感器 Uno；同一个端口不能同时连接 LCD。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { AvailablePorts = []; Port = ""; PortsHint = "无法读取串口列表，请刷新重试。"; }
    }

    public void ObserveFace(bool confirmed)
    {
        if (Busy && IsLive && !stopping && confirmed)
        {
            try { Apply(new(clock.Elapsed.TotalMilliseconds, FaceConfirmed: true)); }
            catch (IOException error) { Reason = "日志写入失败：" + error.Message; Stop(); }
        }
    }

    private bool CanStart() => !Busy && !disposed;
    private bool CanStop() => Busy;

    private void Begin(bool live)
    {
        telemetry = new(); fusion = new(); resume = false; stopping = false; lastEventMilliseconds = 0; IsLive = live; clock.Restart();
        var directory = Path.Combine(Workspace(), "artifacts", "sensors", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(new {
            source = live ? "hardware-telemetry" : "synthetic", hardwareOutput = "disabled",
            thresholds = "preliminary-unvalidated", actuatorIntegration = "preview-only", startedAt = DateTimeOffset.UtcNow }));
        LogPath = Path.Combine(directory, "events.jsonl");
        log = new StreamWriter(LogPath) { AutoFlush = true };
        Busy = true;
        Source = live ? "UNO 实物遥测 · 融合预览 · 不控制舵机" : "合成演示 · 含脚本恢复操作 · 无硬件验证";
        timer.Start();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task DemoAsync()
    {
        try
        {
            Begin(false);
            foreach (var item in SensorExperiment.Demo())
            {
                if (!Busy || disposed || stopping) break;
                var delay = item.AtMilliseconds - clock.Elapsed.TotalMilliseconds;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay));
                if (!Busy || disposed || stopping) break;
                Apply(item);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { Reason = "演示失败：" + error.Message; }
        finally { Stop(); Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task ConnectAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Port)) throw new ArgumentException("请先选择传感器 Uno 串口。");
            var start = ServoTool.CreateStartInfo("sensor_telemetry.py", ["--port", Port.Trim()]);
            Begin(true);
            process = Process.Start(start) ?? throw new IOException("无法启动采集进程。");
            var errors = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!Busy || disposed || stopping) break;
                Apply(new(clock.Elapsed.TotalMilliseconds, line));
            }
            await process.WaitForExitAsync();
            var detail = await errors;
            if (!stopping) Reason = "采集连接已结束：" + detail.Trim();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Reason = "采集失败：" + error.Message; }
        finally
        {
            Stop(); process?.Dispose(); process = null; Busy = false;
        }
    }

    private void Apply(SensorEvent item)
    {
        lastEventMilliseconds = item.AtMilliseconds;
        if (resume) { item = item with { Resume = true }; resume = false; }
        Latest = SensorExperiment.Apply(item, telemetry, fusion);
        log?.WriteLine(JsonSerializer.Serialize(item));
        Present(TimeSpan.FromMilliseconds(item.AtMilliseconds));
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (!Busy || !IsLive) return;
        try { Apply(new(clock.Elapsed.TotalMilliseconds)); }
        catch (IOException error) { Reason = "日志写入失败：" + error.Message; Stop(); }
    }

    // Called with replay time in headless verification; it uses the same receiver/presenter as live input.
    internal void PreviewEvent(SensorEvent item)
    {
        Latest = SensorExperiment.Apply(item, telemetry, fusion);
        Present(TimeSpan.FromMilliseconds(item.AtMilliseconds));
    }

    private void Present(TimeSpan now)
    {
        var a = telemetry.Acceleration;
        var d = telemetry.Distance;
        Acceleration = Latest!.AccelerationFresh && a is not null
            ? $"X {a.Packet.X} · Y {a.Packet.Y} · Z {a.Packet.Z} mg\n倾斜估计 {Latest.TiltDegrees:0.0}° · 年龄 {(now - a.SampledAt).TotalMilliseconds:0}ms"
            : "加速度：缺失 / 无效 / 过期";
        Distance = Latest.DistanceFresh && d is not null
            ? $"{d.Packet.X} mm · 年龄 {(now - d.SampledAt).TotalMilliseconds:0}ms" : "距离：缺失 / 无回波 / 过期";
        State = Latest.State switch {
            CompanionState.Companion => "陪伴候选", CompanionState.Resting => "休息",
            CompanionState.PostureProtection => "姿态保护", CompanionState.DistanceProtection => "距离保护",
            CompanionState.AwaitingResume => "等待显式恢复", _ => "等待有效传感器" };
        Reason = Latest.Reason;
        Statistics = $"会话 {telemetry.Sessions} · 拒收 {telemetry.Rejected} · 丢包 {telemetry.MissingPackets}\n{telemetry.Notice}";
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Resume() => resume = true;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (stopping) return;
        stopping = true;
        timer.Stop();
        if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        if (Busy)
        {
            var finalReason = Reason;
            try { Apply(new(Math.Max(lastEventMilliseconds, clock.Elapsed.TotalMilliseconds), Disconnected: true)); }
            catch (IOException) { telemetry.Disconnect(); }
            Reason = finalReason;
        }
        Acceleration = "采集已停止"; Distance = "采集已停止"; State = "已停止 · 候选运动暂停";
        log?.Dispose(); log = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop(); timer.Tick -= Tick;
    }

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Toboo.sln"))) return directory.FullName;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanTiltTracker");
    }
}
