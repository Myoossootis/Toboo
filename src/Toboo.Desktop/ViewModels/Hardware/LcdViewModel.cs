using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toboo.Core;
using Toboo.Desktop.Services;

namespace Toboo.Desktop.ViewModels;

public sealed partial class LcdViewModel : ObservableObject, IDisposable
{
    private readonly Func<ChatExpression?> capture;
    private readonly Func<string, CancellationToken, Task<ILcdConnection>> connect;
    private readonly Func<string[]> getPorts;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private ILcdConnection? connection;
    private CancellationTokenSource? lifetime;
    private bool sending, disposed;
    private const int RoleLifetime = 1200;
    internal Func<LcdRole?>? CaptureChatRole { get; set; }
    [ObservableProperty] private string? port;
    [ObservableProperty] private string[] availablePorts = [];
    [ObservableProperty] private string portsHint = "";
    [ObservableProperty] private string status = "未连接 · I²C LCD1602";
    [ObservableProperty] private string displayFace = "(?)";
    [ObservableProperty] private string displayLabel = "No valid cue";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanDisconnect))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(RefreshPortsCommand))]
    private bool connecting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanDisconnect))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(RefreshPortsCommand))]
    private bool isConnected;
    public bool CanConnect => !disposed && !Connecting && !IsConnected;
    public bool CanDisconnect => Connecting || IsConnected;

    internal LcdViewModel(Func<ChatExpression?> capture,
        Func<string, CancellationToken, Task<ILcdConnection>>? connect = null, Func<string[]>? getPorts = null)
    {
        this.capture = capture; this.connect = connect ?? LcdConnection.OpenAsync;
        this.getPorts = getPorts ?? SerialPortCatalog.List;
        timer.Tick += Tick;
        RefreshPorts();
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void RefreshPorts()
    {
        if (!CanConnect) return;
        var selected = Port;
        try
        {
            var ports = SerialPortCatalog.Normalize(getPorts());
            AvailablePorts = ports;
            Port = ports.FirstOrDefault(value => string.Equals(value, selected, StringComparison.OrdinalIgnoreCase));
            PortsHint = ports.Length == 0 ? "未发现串口，请连接 Uno 后刷新。" : "请选择连接 LCD 的 Uno 串口。";
        }
        catch (Exception error) when (IsConnectionError(error))
        {
            AvailablePorts = []; Port = null;
            PortsHint = "无法读取串口列表，请刷新重试。";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (!CanConnect) return;
        if (string.IsNullOrWhiteSpace(Port)) { Status = "请先选择 Uno 串口。"; return; }
        Connecting = true;
        lifetime = new();
        var token = lifetime.Token;
        Status = "正在连接 Uno，等待屏幕初始化…";
        ILcdConnection? opened = null;
        try
        {
            opened = await connect(Port.Trim(), token);
            token.ThrowIfCancellationRequested();
            if (opened.SupportsRoles)
            {
                if (!await opened.SendRoleAsync(LcdRole.Idle, RoleLifetime, token))
                    throw new TimeoutException("角色状态在串口发送前已过期。");
                token.ThrowIfCancellationRequested();
                ShowRole(LcdRole.Idle);
            }
            else
            {
                await opened.SendAsync("unknown", 1, token);
                Status = "已连接 · 旧版固件";
            }
            token.ThrowIfCancellationRequested();
            connection = opened; opened = null;
            IsConnected = true;
            timer.Start();
        }
        catch (OperationCanceledException) { if (!disposed) Status = "已断开"; }
        catch (Exception error) when (IsConnectionError(error)) { Status = "LCD 连接失败：" + error.Message; }
        finally
        {
            opened?.Dispose();
            if (!IsConnected) { lifetime?.Dispose(); lifetime = null; }
            Connecting = false;
        }
    }

    private async void Tick(object? sender, EventArgs args) => await SendCurrentAsync();

    internal async Task SendCurrentAsync()
    {
        if (sending || !IsConnected || connection is not { } current || lifetime is null) return;
        var token = lifetime.Token;
        sending = true;
        try
        {
            var cue = capture();
            // Leave 50ms for transport. Do not extend a sample's original 500ms validity.
            var ttl = cue is not null && double.IsFinite(cue.AgeMs) && cue.AgeMs >= 0
                ? (int)Math.Clamp(450 - cue.AgeMs, 0, 450) : 0;
            var valid = ttl > 0 && cue is { Score: >= 0.6 and <= 1 }
                && Enum.TryParse<FacialExpression>(cue.Label, true, out var value) && Enum.IsDefined(value) && value != FacialExpression.Unknown
                && value.ToString().ToLowerInvariant() == cue.Label;
            if (current.SupportsRoles)
            {
                var chatRole = CaptureChatRole?.Invoke();
                var role = chatRole ?? (valid ? LcdRole.Attentive : LcdRole.Idle);
                if (chatRole is null && valid && cue!.Label == "happy") role = LcdRole.Happy;
                if (await current.SendRoleAsync(role, chatRole is null && valid ? ttl : RoleLifetime, token))
                {
                    token.ThrowIfCancellationRequested();
                    ShowRole(role);
                }
                return;
            }
            var label = valid ? cue!.Label : "unknown";
            await current.SendAsync(label, valid ? ttl : 1, token);
            token.ThrowIfCancellationRequested();
            var expression = Enum.Parse<FacialExpression>(label, true);
            DisplayFace = new ExpressionEstimate(expression, 0, 0, "", null, []).Emoticon;
            DisplayLabel = valid ? label : "No valid cue";
            Status = valid ? "已发送 · Uno 已确认" : "已连接 · 等待有效实时表情";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsConnectionError(error))
        {
            if (ReferenceEquals(connection, current))
            { Disconnect(); Status = "LCD 已断开：" + error.Message; }
        }
        finally { sending = false; }
    }

    private void ShowRole(LcdRole role)
    {
        (DisplayFace, DisplayLabel) = role switch
        {
            LcdRole.Attentive => ("(O_O)", "注意"),
            LcdRole.Thinking => ("(o_o)", "思考"),
            LcdRole.Responding => ("(o.O)", "回应"),
            LcdRole.Happy => ("(^_^)", "开心"),
            _ => ("(•_•)", "待机")
        };
        Status = "已连接 · 角色动画";
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect()
    {
        var animated = connection?.SupportsRoles == true;
        timer.Stop(); lifetime?.Cancel(); connection?.Dispose(); connection = null;
        if (!Connecting) { lifetime?.Dispose(); lifetime = null; }
        IsConnected = false; DisplayFace = "(?)"; DisplayLabel = "No valid cue";
        Status = animated ? "已断开 · 屏幕会恢复待机" : "已断开";
    }
    private static bool IsConnectionError(Exception error) => error is IOException or InvalidOperationException
        or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Disconnect(); timer.Tick -= Tick;
    }
}
