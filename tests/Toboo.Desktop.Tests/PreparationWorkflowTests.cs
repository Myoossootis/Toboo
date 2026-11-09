using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Toboo.Desktop.Services;
using Toboo.Desktop.ViewModels;
using Toboo.Desktop.Views;
using Toboo.Runtime;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class PreparationWorkflowTests
{
    [Fact]
    public async Task FollowWaitsForPreviewOwnershipAndRechecksReadinessAfterWaiting()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = true;
        var starts = 0;
        var transition = PreviewFollowTransition.RunAsync(() => released.Task, () => ready,
            () => { starts++; return Task.CompletedTask; });
        Assert.Equal(0, starts);
        ready = false; // E.g. the board was disconnected or calibration changed while stopping.
        released.SetResult(); await transition;
        Assert.Equal(0, starts);
        await PreviewFollowTransition.RunAsync(() => Task.CompletedTask, () => true,
            () => { starts++; return Task.CompletedTask; });
        Assert.Equal(1, starts);
        await Assert.ThrowsAsync<IOException>(() => PreviewFollowTransition.RunAsync(
            () => Task.FromException(new IOException("camera still owns the device")), () => true,
            () => { starts++; return Task.CompletedTask; }));
        Assert.Equal(1, starts);
    }

    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(980, 700)]
    public async Task CalibrationStaysInsideProductAndPreparedPreviewCanOfferFollow(int width, int height)
    {
        var folder = Path.Combine(Path.GetTempPath(), "toboo-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var calibration = Path.Combine(folder, "servo.json");
        File.WriteAllText(calibration, JsonSerializer.Serialize(new { Pan = 103, Tilt = 100, PanFloor = 30, PanCeiling = 175,
            TiltFloor = 50, TiltCeiling = 150, TiltProfile = ServoViewModel.TiltProfileId, TiltConfirmed = true }));
        var calls = 0;
        var servo = new ServoViewModel((args, _) =>
        {
            calls++; Assert.Contains("inspect", args);
            return Task.FromResult(new ServoTool.Result(0, JsonSerializer.Serialize(new { firmware = "0.5.3", final_state = "DISARMED",
                axes = new { S1 = new { last_requested = 103 }, S2 = new { last_requested = 100 } } }), ""));
        }, calibration, () => ["COM7", "COM6"]);
        var aim = new FaceAimViewModel(Path.Combine(folder, "aim.json")) { GeometryMode = 1, DirectionsConfirmed = true };
        using var model = new MainViewModel(servo: servo, faceAim: aim) { SourceIndex = 3, TargetIndex = 1, SelectedCamera = new(0, "Test camera") };
        model.Cameras.Add(model.SelectedCamera!);
        var window = new MainWindow(model) { Width = width, Height = height };
        try
        {
            window.Show(); window.Shell.PageIndex = 1;
            var advanced = 0; window.Shell.AdvancedRequested += _ => advanced++;
            window.Shell.OpenCalibrationCommand.Execute(null);
            Assert.True(window.Shell.CalibrationExpanded); Assert.Equal(0, advanced);
            Assert.False(window.Shell.CanFollow);
            await servo.InspectCommand.ExecuteAsync(null); servo.Ready = true;
            model.IsRunning = true; // A preview-state fixture; no camera/serial output starts.
            Assert.False(model.CanStart); Assert.True(model.CanStartFollow); Assert.True(window.Shell.CanFollow);
            Assert.Contains("预览", window.Shell.FollowActionLabel);
            aim.DirectionsConfirmed = false; Assert.False(window.Shell.CanFollow);
            aim.DirectionsConfirmed = true;
            window.Shell.AdvanceSetupCommand.Execute(null);
            Assert.Equal(1, window.Shell.SetupStep);
            window.Shell.AdvanceSetupCommand.Execute(null);
            Assert.Equal(2, window.Shell.SetupStep);
            Assert.Equal(1, calls); // Rendering and calibration expansion never send motion commands.
            var devices = window.FindControl<DevicesView>("DevicesView")!;
            var scroll = devices.FindControl<ScrollViewer>("DeviceScroll")!;
            await Task.Delay(50);
            scroll.Offset = new(0, 320);
            await Task.Delay(50);
            using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
            var root = Workspace(); var output = Path.Combine(root, "artifacts", "workflow-preview"); Directory.CreateDirectory(output);
            bitmap.Save(Path.Combine(output, $"calibration-{width}.png"));
            Assert.NotNull(devices.FindControl<ComboBox>("DeviceServoPortSelector"));
            Assert.Empty(window.OwnedWindows);
            window.Shell.AdvanceSetupCommand.Execute(null);
            Assert.True(aim.LastSaveSucceeded); Assert.False(window.Shell.CalibrationExpanded);
            Assert.True(File.Exists(Path.Combine(folder, "aim.json")));
            scroll.Offset = default;
            await Task.Delay(50);
            using var daily = window.CaptureRenderedFrame(); Assert.NotNull(daily);
            daily.Save(Path.Combine(output, $"daily-{width}.png"));
        }
        finally { model.IsRunning = false; window.Close(); Directory.Delete(folder, true); }
    }

    private static string Workspace()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Toboo.sln"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
}
