using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Toboo.Core;
using Toboo.Desktop.Services;
using Toboo.Desktop.ViewModels;
using Toboo.Desktop.Views;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class LcdTests
{
    private sealed class FakeConnection : ILcdConnection
    {
        public List<(string Label, int Ttl)> Sent { get; } = [];
        public bool Disposed { get; private set; }
        public bool Fail { get; set; }
        public TaskCompletionSource? Pending { get; set; }
        public async Task SendAsync(string label, int lifetimeMilliseconds, CancellationToken cancellation)
        {
            if (Fail) throw new IOException("USB removed");
            Sent.Add((label, lifetimeMilliseconds));
            if (Pending is not null) await Pending.Task.WaitAsync(cancellation);
        }
        public void Dispose() => Disposed = true;
    }

    [AvaloniaFact]
    public async Task LiveCueHasRemainingLifetimeAndClearsOnUnknownExpiryAndDisconnect()
    {
        ChatExpression? cue = new("happy", 0.9, 150, 0);
        var transport = new FakeConnection();
        using var model = new LcdViewModel(() => cue, (_, _) => Task.FromResult<ILcdConnection>(transport)) { Port = "COM-test" };
        await model.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(("unknown", 1), transport.Sent[0]);
        await model.SendCurrentAsync();
        Assert.Equal(("happy", 300), transport.Sent[^1]);
        Assert.Equal("(^_^)", model.DisplayFace);
        cue = cue with { AgeMs = 499 };
        await model.SendCurrentAsync();
        Assert.Equal(("unknown", 1), transport.Sent[^1]);
        Assert.Equal("(?)", model.DisplayFace);
        cue = null;
        await model.SendCurrentAsync();
        Assert.Equal(("unknown", 1), transport.Sent[^1]);
        transport.Fail = true;
        await model.SendCurrentAsync();
        Assert.False(model.IsConnected); Assert.True(transport.Disposed);
        Assert.Contains("USB removed", model.Status);
    }

    [AvaloniaFact]
    public async Task PendingAckNeverQueuesAnotherObservationAndCloseCancelsIt()
    {
        var transport = new FakeConnection();
        using var model = new LcdViewModel(() => new("sad", 0.9, 0, 0), (_, _) => Task.FromResult<ILcdConnection>(transport)) { Port = "COM-test" };
        await model.ConnectCommand.ExecuteAsync(null);
        transport.Pending = new();
        var sending = model.SendCurrentAsync();
        await model.SendCurrentAsync();
        Assert.Equal(2, transport.Sent.Count);
        model.Dispose(); await sending;
        Assert.False(model.IsConnected); Assert.True(transport.Disposed);
        Assert.Equal("(?)", model.DisplayFace);
    }

    [AvaloniaFact]
    public async Task CancelledConnectDisposesLateTransportAndDoesNotReconnect()
    {
        var pending = new TaskCompletionSource<ILcdConnection>();
        var transport = new FakeConnection();
        using var model = new LcdViewModel(() => null, (_, _) => pending.Task) { Port = "COM-test" };
        var connecting = model.ConnectCommand.ExecuteAsync(null);
        Assert.True(model.Connecting);
        model.DisconnectCommand.Execute(null);
        pending.SetResult(transport);
        await connecting;
        Assert.True(transport.Disposed); Assert.False(model.IsConnected); Assert.False(model.Connecting);
        Assert.Empty(transport.Sent);
    }

    [AvaloniaFact]
    public void HardwareCueRejectsRecordedImageDisabledAndStoppedVision()
    {
        using var model = new MainViewModel { SourceIndex = 3, TargetIndex = 1, IsRunning = true };
        var estimate = new ExpressionEstimate(FacialExpression.Happy, 0.9, 0.8, "stable", TimeSpan.Zero, []);
        model.Present(new(new byte[640 * 480 * 4], 640, 480, new(0, TimeSpan.Zero, 640, 480, 320, 240),
            new(103, 100, TrackingState.Centered, 0, 0), TimeSpan.Zero, 1, Expression: new(estimate)));
        Assert.Equal("happy", model.CaptureExpressionCue()!.Label);
        model.SourceIndex = 2; Assert.Null(model.CaptureExpressionCue());
        model.SourceIndex = 1; Assert.Null(model.CaptureExpressionCue());
        model.SourceIndex = 3; model.ExpressionEnabled = false; Assert.Null(model.CaptureExpressionCue());
        model.IsRunning = false; Assert.Null(model.CaptureExpressionCue());
        Assert.False(model.Servo.Ready); Assert.False(model.IsFollowing);
    }

    [AvaloniaFact]
    public async Task PortListRefreshPreservesSelectionClearsRemovedPortsAndLocksDuringConnection()
    {
        string[] ports = ["COM10", "COM7", "COM6", "COM7"];
        var scans = 0;
        var transport = new FakeConnection();
        var pending = new TaskCompletionSource<ILcdConnection>();
        using var model = new LcdViewModel(() => null, (_, _) => pending.Task, () => { scans++; return ports; });
        Assert.Equal(["COM6", "COM7", "COM10"], model.AvailablePorts);
        Assert.Null(model.Port); // Never guess which board is the LCD.
        model.Port = "COM7";
        model.RefreshPortsCommand.Execute(null);
        Assert.Equal("COM7", model.Port);
        ports = ["COM6"];
        model.RefreshPortsCommand.Execute(null);
        Assert.Null(model.Port);
        await model.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("选择", model.Status);
        model.Port = "COM6";
        var connecting = model.ConnectCommand.ExecuteAsync(null);
        Assert.False(model.RefreshPortsCommand.CanExecute(null));
        var before = scans;
        model.RefreshPortsCommand.Execute(null);
        Assert.Equal(before, scans);
        pending.SetResult(transport); await connecting;
        Assert.False(model.CanConnect); Assert.False(model.RefreshPortsCommand.CanExecute(null));
        model.DisconnectCommand.Execute(null);
        Assert.True(model.RefreshPortsCommand.CanExecute(null));
        ports = [];
        model.RefreshPortsCommand.Execute(null);
        Assert.Null(model.Port); Assert.Empty(model.AvailablePorts); Assert.Contains("未发现串口", model.PortsHint);
    }

    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(980, 700)]
    public async Task DevicesScreenRendersLcdControlsWithoutOpeningHardware(int width, int height)
    {
        using var model = new MainViewModel();
        var window = new MainWindow(model) { Width = width, Height = height };
        try
        {
            window.Show(); window.Shell.NavigateCommand.Execute("1");
            await Task.Delay(50);
            var devices = window.FindControl<DevicesView>("DevicesView")!;
            Assert.True(devices.FindControl<Button>("ConnectLcdButton")!.IsEffectivelyEnabled);
            var ports = devices.FindControl<ComboBox>("LcdPortSelector")!;
            Assert.Null(ports.SelectedItem); Assert.Null(model.Lcd.Port); Assert.False(model.Lcd.IsConnected);
            Assert.True(devices.FindControl<Button>("RefreshLcdPortsButton")!.IsEffectivelyEnabled);
            Assert.False(model.IsRunning); Assert.False(model.Servo.Ready);
            devices.FindControl<ScrollViewer>("DeviceScroll")!.ScrollToEnd();
            await Task.Delay(50);
            using var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            var path = Path.Combine(Workspace(), "artifacts", "lcd-preview"); Directory.CreateDirectory(path);
            bitmap.Save(Path.Combine(path, $"devices-{width}.png"));
        }
        finally { window.Close(); }
    }

    private static string Workspace()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Toboo.sln"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
}
