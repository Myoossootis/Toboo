using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Toboo.Desktop.Services;

namespace Toboo.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new StartupWindow();
            desktop.MainWindow = splash;
            splash.Opened += async (_, _) => await CompleteStartupAsync(desktop, splash, () => CreateMainWindow(desktop));
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal static async Task CompleteStartupAsync(IClassicDesktopStyleApplicationLifetime desktop, StartupWindow splash,
        Func<Window> createMainWindow)
    {
        using var closing = new CancellationTokenSource();
        void CancelWhenClosed(object? sender, EventArgs args) => closing.Cancel();
        splash.Closed += CancelWhenClosed;
        Window? window = null;
        try
        {
            var started = Stopwatch.GetTimestamp();
            splash.Play();
            // Let Avalonia render the splash before building the main window on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, closing.Token);
            closing.Token.ThrowIfCancellationRequested();
            window = createMainWindow();
            var remaining = StartupWindow.IntroDuration - Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, closing.Token);
            closing.Token.ThrowIfCancellationRequested();

            var content = window.Content as Control;
            if (content is not null)
            {
                content.Opacity = 0;
                content.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) }];
            }
            desktop.MainWindow = window;
            window.Show();
            if (content is not null) content.Opacity = 1;
            splash.Close();
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { window?.Close(); }
        catch (Exception error)
        {
            desktop.MainWindow = splash;
            window?.Close();
            try { Diagnostics.Write("startup-error.json", new { success = false, error = error.ToString() }); }
            catch (Exception) { /* The splash can still report failure if diagnostics are unwritable. */ }
            if (desktop.Args?.Contains("--smoke-test-ui", StringComparer.Ordinal) == true) desktop.Shutdown(1);
            else
            {
                desktop.Exit += (_, args) => args.ApplicationExitCode = 1;
                splash.ShowFailure();
            }
        }
        finally { splash.Closed -= CancelWhenClosed; }
    }

    private static MainWindow CreateMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? [];
        var smokeTest = args.Contains("--smoke-test-ui", StringComparer.Ordinal);
        var calibrate = args.Contains("--servo-calibration", StringComparer.Ordinal);
        var window = new MainWindow();
        if (args.Contains("--sensors", StringComparer.Ordinal)) window.Opened += (_, _) => window.ShowSensors();
        if (calibrate || args.Contains("--expression-preview", StringComparer.Ordinal)) window.Shell.PageIndex = 1;
        if (calibrate) window.Shell.CalibrationExpanded = true;
        if (!smokeTest || calibrate)
        {
            window.ViewModel.SourceIndex = 3;
            window.ViewModel.TargetIndex = 1;
        }
        if (smokeTest) window.Opened += async (_, _) => await RunUiSmokeTestAsync(desktop, window, args);
        else
        {
            window.Opened += async (_, _) => await window.ViewModel.RefreshCamerasAsync();
            window.Opened += async (_, _) => await window.Shell.Chat.ConnectAsync();
            window.Opened += async (_, _) => await window.Shell.Voice.InitializeAsync();
        }
        return window;
    }

    private static async Task RunUiSmokeTestAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, string[] args)
    {
        var success = false;
        try
        {
            var cameraArgument = Array.IndexOf(args, "--camera-index");
            var useCamera = cameraArgument >= 0;
            if (useCamera)
            {
                if (cameraArgument + 1 >= args.Length || !int.TryParse(args[cameraArgument + 1], out var index) || index < 0)
                    throw new ArgumentException("--camera-index requires a non-negative device index.");
                window.ViewModel.SourceIndex = 3;
                window.ViewModel.CameraIndex = index;
            }
            var run = window.ViewModel.StartAsync();
            var started = Stopwatch.GetTimestamp();
            while (window.ViewModel.ProcessedFrames < 12 && window.ViewModel.IsRunning && Stopwatch.GetElapsedTime(started).TotalSeconds < 15)
                await Task.Delay(25);
            await window.ViewModel.StopAsync();
            await run;
            success = window.ViewModel.ProcessedFrames >= 12 && !window.ViewModel.HasError;
            Diagnostics.Write("ui-smoke-test.json", new
            {
                success, synthetic = !useCamera, physicalCameraTested = useCamera,
                frames = window.ViewModel.ProcessedFrames, window.ViewModel.ErrorMessage,
                target = window.ViewModel.IsFace ? "face" : "aruco",
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                hardwareOutput = "disabled"
            });
        }
        catch (Exception error) { Diagnostics.Write("ui-smoke-test.json", new { success = false, error = error.ToString() }); }
        finally { desktop.Shutdown(success ? 0 : 1); }
    }
}
