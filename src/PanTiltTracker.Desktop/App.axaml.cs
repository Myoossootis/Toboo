using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using PanTiltTracker.Desktop.Services;

namespace PanTiltTracker.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (desktop.Args?.Contains("--smoke-test-ui", StringComparer.Ordinal) == true)
                window.Opened += async (_, _) =>
                {
                    var success = false;
                    try
                    {
                        var cameraArgument = Array.IndexOf(desktop.Args!, "--camera-index");
                        var useCamera = cameraArgument >= 0;
                        if (useCamera)
                        {
                            if (cameraArgument + 1 >= desktop.Args!.Length ||
                                !int.TryParse(desktop.Args[cameraArgument + 1], out var index) || index < 0)
                                throw new ArgumentException("--camera-index requires a non-negative device index.");
                            window.ViewModel.SourceIndex = 3;
                            window.ViewModel.CameraIndex = index;
                        }
                        var droneArgument = Array.IndexOf(desktop.Args!, "--drone-model");
                        if (droneArgument >= 0)
                        {
                            if (!useCamera || droneArgument + 1 >= desktop.Args!.Length)
                                throw new ArgumentException("--drone-model requires --camera-index and a verified ONNX path.");
                            window.ViewModel.TargetIndex = 2;
                            window.ViewModel.DroneModelPath = desktop.Args[droneArgument + 1];
                            window.ViewModel.DroneTrackingEnabled = desktop.Args.Contains("--drone-tracking", StringComparer.Ordinal);
                        }
                        var run = window.ViewModel.StartAsync();
                        var until = DateTime.UtcNow.AddSeconds(15);
                        while (window.ViewModel.ProcessedFrames < 12 && window.ViewModel.IsRunning && DateTime.UtcNow < until)
                            await Task.Delay(25);
                        await window.ViewModel.StopAsync();
                        await run;
                        success = window.ViewModel.ProcessedFrames >= 12 && !window.ViewModel.HasError;
                        Diagnostics.Write("ui-smoke-test.json", new
                        {
                            success, synthetic = !useCamera, physicalCameraTested = useCamera,
                            frames = window.ViewModel.ProcessedFrames, window.ViewModel.ErrorMessage,
                            target = window.ViewModel.IsDrone ? "drone" : "aruco",
                            droneTracking = window.ViewModel.IsDroneStream && window.ViewModel.DroneTrackingEnabled,
                            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                            hardwareOutput = "disabled"
                        });
                    }
                    catch (Exception error) { Diagnostics.Write("ui-smoke-test.json", new { success = false, error = error.ToString() }); }
                    finally { desktop.Shutdown(success ? 0 : 1); }
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
