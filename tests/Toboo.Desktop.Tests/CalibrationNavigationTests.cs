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

public sealed class CalibrationNavigationTests
{
    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(980, 700)]
    public async Task SidebarOpensIndependentCalibrationPageWithItsOwnCameraSelector(int width, int height)
    {
        using var tracking = new MainViewModel();
        tracking.Cameras.Add(new(0, "Test USB camera"));
        tracking.SelectedCamera = tracking.Cameras[0];
        var window = new MainWindow(tracking) { Width = width, Height = height };
        try
        {
            window.Show();
            Assert.NotNull(window.FindControl<Button>("CalibrationNav"));
            await window.Shell.NavigateCommand.ExecuteAsync("3");
            Assert.True(window.Shell.IsCalibrationPage);
            Assert.Equal("视觉标定", window.Shell.PageTitle);
            var view = window.FindControl<SpatialCalibrationView>("SpatialView")!;
            Assert.True(view.IsEffectivelyVisible);
            Assert.False(window.FindControl<DevicesView>("DevicesView")!.IsEffectivelyVisible);
            Assert.Equal(tracking.SelectedCamera, view.FindControl<ComboBox>("CameraSelector")!.SelectedItem);
            Assert.True(view.FindControl<Button>("StartButton")!.IsEnabled);
            Assert.False(tracking.IsRunning);
            view.DisplayDemo();
            await Task.Delay(60);
            using var screenshot = window.CaptureRenderedFrame();
            Assert.NotNull(screenshot);
            var path = Path.Combine(Workspace(), "artifacts", "calibration-navigation");
            Directory.CreateDirectory(path);
            screenshot.Save(Path.Combine(path, $"sidebar-{width}.png"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LeavingCalibrationWaitsForCameraReleaseAndBlocksOtherCapture()
    {
        using var tracking = new MainViewModel();
        using var shell = new CompanionShellViewModel(tracking);
        shell.PageIndex = 3;
        tracking.CalibrationActive = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.StopCalibrationAsync = async () => { await release.Task; tracking.CalibrationActive = false; };
        var navigation = shell.NavigateCommand.ExecuteAsync("1");
        Assert.Equal(3, shell.PageIndex);
        Assert.False(tracking.CanStart);
        Assert.False(tracking.CanStartFollow);
        release.SetResult();
        await navigation;
        Assert.Equal(1, shell.PageIndex);
        Assert.False(tracking.CalibrationActive);
        Assert.True(tracking.CanStart);
    }

    [AvaloniaFact]
    public async Task EnteringCalibrationEndsTheExistingPreviewBeforeChangingPages()
    {
        using var tracking = new MainViewModel();
        tracking.Cameras.Add(new(0, "Test camera"));
        tracking.SelectedCamera = tracking.Cameras[0];
        using var shell = new CompanionShellViewModel(tracking);
        var running = tracking.StartAsync(); // Default synthetic scene.
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (tracking.ProcessedFrames < 2) await Task.Delay(20, deadline.Token);
            Assert.True(tracking.IsRunning);
            await shell.NavigateCommand.ExecuteAsync("3");
            await running;
            Assert.False(tracking.IsRunning);
            Assert.Equal(3, shell.PageIndex);
        }
        finally { await tracking.StopAsync(); await running; }
    }

    [AvaloniaFact]
    public async Task CommandUsesExistingFollowReadinessAndNeverConfirmsPowerOrInitializesServos()
    {
        var folder = Path.Combine(Path.GetTempPath(), "toboo-command-" + Guid.NewGuid().ToString("N"));
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
            var calls = new List<string[]>();
            var servo = new ServoViewModel((arguments, _) =>
            {
                calls.Add(arguments.ToArray());
                return Task.FromResult(new ServoTool.Result(0, JsonSerializer.Serialize(new
                {
                    firmware = "0.5.3", final_state = "DISARMED",
                    axes = new { S1 = new { last_requested = 88 }, S2 = new { last_requested = 100 } }
                }), ""));
            }, file, () => ["COM6"]);
            var aim = new FaceAimViewModel(Path.Combine(folder, "aim.json"))
                { GeometryMode = 1, DirectionsConfirmed = true };
            using var tracking = new MainViewModel(servo: servo, faceAim: aim) { SelectedCamera = new(0, "Test camera") };
            var starts = 0;
            using var shell = new CompanionShellViewModel(tracking, requestFollow: () => starts++);
            Assert.Null(await shell.HandleVoiceCommandAsync("不要打开摄像机跟随", default));
            Assert.Null(await shell.HandleVoiceCommandAsync("你能解释打开摄像机跟随吗", default));
            Assert.Empty(calls);
            await shell.HandleVoiceCommandAsync("打开 摄像机 跟随。", default);
            Assert.Equal(0, starts);
            Assert.False(servo.Ready);
            Assert.All(calls, call => Assert.Contains("inspect", call));
            Assert.All(calls, call => Assert.DoesNotContain("--execute", call));
            servo.Ready = true;
            Assert.True(servo.CanFollow);
            shell.PageIndex = 3;
            tracking.CalibrationActive = true;
            Assert.False(tracking.CanStartFollow);
            shell.StopCalibrationAsync = () =>
            {
                Assert.Equal(0, starts);
                tracking.CalibrationActive = false;
                return Task.CompletedTask;
            };
            await shell.HandleVoiceCommandAsync("打开摄像机跟随", default);
            Assert.Equal(1, starts);
            Assert.Equal(1, shell.PageIndex);
            aim.DirectionsConfirmed = false;
            await shell.HandleVoiceCommandAsync("打开摄像机跟随", default);
            Assert.Equal(1, starts);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Toboo.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
