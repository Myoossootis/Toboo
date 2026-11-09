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
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private ILcdConnection? connection;
    private CancellationTokenSource? lifetime;
    private bool sending, disposed;
    [ObservableProperty] private string port = "";
    [ObservableProperty] private string status = "未连接 · I²C LCD1602";
    [ObservableProperty] private string displayFace = "(?)";
    [ObservableProperty] private string displayLabel = "No valid cue";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanDisconnect))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand))]
    private bool connecting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanDisconnect))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand))]
    private bool isConnected;
    public bool CanConnect => !disposed && !Connecting && !IsConnected;
    public bool CanDisconnect => Connecting || IsConnected;

    internal LcdViewModel(Func<ChatExpression?> capture,
        Func<string, CancellationToken, Task<ILcdConnection>>? connect = null)
    {
        this.capture = capture; this.connect = connect ?? LcdConnection.OpenAsync;
        timer.Tick += Tick;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (!CanConnect) return;
        if (string.IsNullOrWhiteSpace(Port)) { Status = "请填写已确认的 Uno COM 端口。"; return; }
        Connecting = true;
        lifetime = new();
        var token = lifetime.Token;
        Status = "正在连接 Uno，等待屏幕初始化…";
        ILcdConnection? opened = null;
        try
        {
            opened = await connect(Port.Trim(), token);
            token.ThrowIfCancellationRequested();
            // Always erase a previous session before starting live observations.
            await opened.SendAsync("unknown", 1, token);
            token.ThrowIfCancellationRequested();
            connection = opened; opened = null;
            IsConnected = true; Status = "已连接 · 等待有效实时表情";
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
                && Enum.TryParse<FacialExpression>(cue.Label, true, out var value) && value != FacialExpression.Unknown
                && value.ToString().ToLowerInvariant() == cue.Label;
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
            Disconnect(); Status = "LCD 已断开：" + error.Message;
        }
        finally { sending = false; }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect()
    {
        timer.Stop(); lifetime?.Cancel(); connection?.Dispose(); connection = null;
        if (!Connecting) { lifetime?.Dispose(); lifetime = null; }
        IsConnected = false; DisplayFace = "(?)"; DisplayLabel = "No valid cue";
        Status = "已断开 · 屏幕旧表情将在 500ms 内过期";
    }
    private static bool IsConnectionError(Exception error) => error is IOException or InvalidOperationException
        or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Disconnect(); timer.Tick -= Tick;
    }
}
