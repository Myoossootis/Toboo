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
    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(980, 700)]
    public async Task MissingCameraModelOffersQuickSetupOutsideTheWizard(int width, int height)
    {
        var folder = Path.Combine(Path.GetTempPath(), "toboo-quick-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var servo = new ServoViewModel((_, _) => throw new InvalidOperationException("No actuator access."),
            Path.Combine(folder, "servo.json"), () => []);
        var aim = new FaceAimViewModel(Path.Combine(folder, "aim.json")) { GeometryMode = 0 };
        using var model = new MainViewModel(servo: servo, faceAim: aim);
        var window = new MainWindow(model) { Width = width, Height = height };
        try
        {
            window.Show(); window.Shell.PageIndex = 1;
            var devices = window.FindControl<DevicesView>("DevicesView")!;
            var button = devices.FindControl<Button>("QuickCameraSetupButton")!;
            Assert.False(window.Shell.CalibrationExpanded);
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.IsEnabled);
            var scroll = devices.FindControl<ScrollViewer>("DeviceScroll")!;
            await Task.Delay(50);
            scroll.Offset = new(0, 300);
            await Task.Delay(50);
            var output = Path.Combine(Workspace(), "artifacts", "workflow-preview"); Directory.CreateDirectory(output);
            using var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            bitmap.Save(Path.Combine(output, $"quick-camera-{width}.png"));
            button.Command!.Execute(null);
            Assert.True(aim.LastSaveSucceeded);
            Assert.False(button.IsEffectivelyVisible);
            Assert.False(window.Shell.CanFollow);
        }
        finally { window.Close(); Directory.Delete(folder, true); }
    }

    [AvaloniaTheory]
    [InlineData(false, 0, 640, 480)]
    [InlineData(true, 0, 640, 480)]
    [InlineData(true, 1, 1280, 720)]
    [InlineData(true, 2, 1920, 1080)]
    public void QuickCameraSetupPersistsAnEstimateAndPreservesDirectionConfirmation(bool directionsConfirmed, int mode, int width, int height)
    {
        var folder = Path.Combine(Path.GetTempPath(), "toboo-quick-camera-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "aim.json");
            var calls = 0;
            var servo = new ServoViewModel((_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Camera setup must not access actuators.");
            }, Path.Combine(folder, "servo.json"), () => ["COM6"]);
            var aim = new FaceAimViewModel(file)
            {
                GeometryMode = 0, PanDirectionIndex = 1, TiltDirectionIndex = 1,
                DirectionsConfirmed = directionsConfirmed
            };
            using var model = new MainViewModel(servo: servo, faceAim: aim) { CameraModeIndex = mode };
            using var shell = new CompanionShellViewModel(model);
            Assert.True(shell.NeedsCameraModel);
            Assert.True(shell.CanQuickConfigureCamera);
            model.CalibrationActive = true;
            Assert.False(shell.QuickConfigureCameraCommand.CanExecute(null));
            model.CalibrationActive = false;
            shell.QuickConfigureCameraCommand.Execute(null);
            Assert.Equal(0, calls);
            Assert.False(servo.Ready);
            Assert.False(servo.TiltCalibrationSaved);
            Assert.False(File.Exists(Path.Combine(folder, "servo.json")));
            Assert.True(aim.LastSaveSucceeded);
            Assert.False(shell.NeedsCameraModel);
            Assert.False(shell.CanFollow);
            Assert.Contains("未经实测标定", shell.CameraSetupNotice);
            var restored = new FaceAimViewModel(file);
            Assert.Equal(1, restored.GeometryMode);
            Assert.Equal(60, restored.HorizontalFov, 6);
            Assert.Equal(45, restored.VerticalFov, 6);
            Assert.Equal(width, restored.CalibrationWidth);
            Assert.Equal(height, restored.CalibrationHeight);
            Assert.Equal(1, restored.PanDirectionIndex);
            Assert.Equal(1, restored.TiltDirectionIndex);
            Assert.Equal(directionsConfirmed, restored.DirectionsConfirmed);
            Assert.True(restored.Snapshot().Camera!.Approximate);

            aim.GeometryMode = 2; aim.IntrinsicsCalibrated = true; aim.Fx = 700;
            shell.QuickConfigureCameraCommand.Execute(null);
            Assert.Equal(2, aim.GeometryMode);
            Assert.Equal(700, aim.Fx);
            Assert.Equal(0, calls);
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ProductFollowStartsFromDefaultModeAndKeepsReadinessGates()
    {
        var folder = Path.Combine(Path.GetTempPath(), "toboo-direct-follow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "servo.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                Pan = 88, PanCenter = 88, PanFloor = 30, PanCeiling = 175,
                Tilt = 100, TiltFloor = 50, TiltCeiling = 150,
                TiltProfile = ServoViewModel.TiltProfileId, TiltConfirmed = true
            }));
            var hasReference = false;
            var calls = new List<string[]>();
            var servo = new ServoViewModel((arguments, _) =>
            {
                calls.Add(arguments.ToArray());
                return Task.FromResult(new ServoTool.Result(0, JsonSerializer.Serialize(new
                {
                    firmware = "0.5.3", final_state = "DISARMED",
                    axes = new { S1 = new { last_requested = hasReference ? 88 : -1 }, S2 = new { last_requested = hasReference ? 100 : -1 } }
                }), ""));
            }, file, () => ["COM6"]);
            var aim = new FaceAimViewModel(Path.Combine(folder, "aim.json")) { GeometryMode = 1, DirectionsConfirmed = true };
            using var model = new MainViewModel(servo: servo, faceAim: aim) { SelectedCamera = new(0, "Test camera") };
            var starts = 0;
            using var shell = new CompanionShellViewModel(model, requestFollow: () =>
            {
                Assert.Equal(3, model.SourceIndex);
                Assert.True(model.IsFace);
                Assert.True(model.CanStartFollow);
                starts++;
            });
            Assert.Equal(0, model.SourceIndex); // Real product startup, before any preview has run.
            Assert.False(shell.CanFollow);
            await servo.InspectCommand.ExecuteAsync(null);
            Assert.False(shell.CanFollow);
            servo.Ready = true;
            Assert.False(shell.CanFollow);
            Assert.Contains("回到中心", shell.PreparationHint);
            shell.StartFollowCommand.Execute(null);
            Assert.Equal(0, starts);
            hasReference = true;
            await servo.InspectCommand.ExecuteAsync(null);
            Assert.True(shell.CanFollow);
            Assert.True(shell.StartFollowCommand.CanExecute(null));
            Assert.False(model.CanStartFollow); // Internal experiment mode is still synthetic.
            shell.StartFollowCommand.Execute(null);
            Assert.Equal(1, starts);
            Assert.All(calls, call => Assert.Contains("inspect", call));
            Assert.All(calls, call => Assert.DoesNotContain("--execute", call));

            model.CalibrationActive = true;
            Assert.False(shell.CanFollow);
            Assert.Contains("视觉标定", shell.PreparationHint);
            model.CalibrationActive = false;
            aim.DirectionsConfirmed = false;
            Assert.False(shell.CanFollow);
            aim.DirectionsConfirmed = true;
            model.SelectedCamera = null;
            Assert.False(shell.CanFollow);
            model.SelectedCamera = new(0, "Test camera");
            aim.GeometryMode = 0;
            Assert.False(shell.CanFollow);
            aim.GeometryMode = 1;
            servo.PanMotionBlocked = true;
            Assert.False(shell.CanFollow);
            servo.PanMotionBlocked = false;
            model.SourceIndex = 2;
            model.IsRunning = true; // Recorded-video fixture must not become live actuator input.
            Assert.False(shell.CanFollow);
            Assert.Contains("停止当前实验", shell.PreparationHint);
            model.IsRunning = false;
            Assert.True(shell.CanFollow);
            shell.StartFollowCommand.Execute(null);
            Assert.Equal(2, starts);
        }
        finally { Directory.Delete(folder, true); }
    }

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
            var devices = window.FindControl<DevicesView>("DevicesView")!;
            devices.FindControl<Expander>("RobotSetupExpander")!.IsExpanded = true;
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
            Assert.Same(window.Shell.StartFollowCommand, devices.FindControl<Button>("DeviceFollowButton")!.Command);
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
