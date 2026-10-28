using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PanTiltTracker.Desktop.ViewModels;
using PanTiltTracker.Vision;
using Xunit;

namespace PanTiltTracker.Desktop.Tests;

public sealed class DesktopTests
{
    private static string OutputRoot => Path.Combine(Workspace(), "artifacts", "desktop-tests");

    [AvaloniaTheory]
    [InlineData(1280, 860, "wide")]
    [InlineData(980, 700, "compact")]
    public async Task LayoutKeepsPrimaryActionsAndPreviewVisible(int width, int height, string name)
    {
        using var viewModel = new MainViewModel(OutputRoot);
        var window = new MainWindow(viewModel) { Width = width, Height = height };
        try
        {
            window.Show();
            await Task.Delay(100);
            SavePreview(window, $"{name}-idle.png");
            var start = window.FindControl<Button>("StartButton")!;
            var preview = window.FindControl<Border>("PreviewPane")!;
            Assert.True(start.IsEffectivelyVisible);
            Assert.True(start.Bounds.Width > 80);
            Assert.True(preview.Bounds.Width > 300);
            Assert.True(preview.Bounds.Height > 120);
            Assert.True(viewModel.StartCommand.CanExecute(null));
            Assert.False(viewModel.StopCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SimulationCanStartOccludeRecoverStopAndExport()
    {
        using var viewModel = new MainViewModel(OutputRoot);
        var window = new MainWindow(viewModel);
        Task? running = null;
        try
        {
            window.Show();
            running = viewModel.StartAsync();
            await UntilAsync(() => viewModel.ProcessedFrames >= 90);
            Assert.True(viewModel.HasPreview);
            Assert.False(viewModel.CanConfigure);
            Assert.False(viewModel.StartCommand.CanExecute(null));
            Assert.True(viewModel.StopCommand.CanExecute(null));
            Assert.False(viewModel.CanExport);
            SavePreview(window, "wide-running.png");

            viewModel.HideTarget = true;
            await UntilAsync(() => viewModel.StateLabel == "目标丢失 · 保持");
            Assert.Equal("—", viewModel.ErrorX);
            var held = viewModel.PanCommand;
            var count = viewModel.ProcessedFrames;
            await UntilAsync(() => viewModel.ProcessedFrames > count + 2);
            Assert.Equal(held, viewModel.PanCommand);
            SavePreview(window, "wide-occluded.png");

            viewModel.HideTarget = false;
            await UntilAsync(() => viewModel.DetectionLabel.StartsWith("已检测", StringComparison.Ordinal));
            await viewModel.StopAsync();
            await running;
            Assert.False(viewModel.IsRunning);
            Assert.True(viewModel.CanExport);
            Assert.True(viewModel.CanConfigure);
            var exported = Path.Combine(OutputRoot, "exported.csv");
            viewModel.ExportCsvTo(exported);
            Assert.True(File.ReadLines(exported).Count() > 15);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(viewModel.LastRunDirectory, "run.json")));
            Assert.Equal("disabled", metadata.RootElement.GetProperty("hardwareOutput").GetString());
            Assert.True(metadata.RootElement.GetProperty("simulated").GetBoolean());
        }
        finally
        {
            await viewModel.StopAsync();
            if (running is not null) await running;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MissingInputIsReportedWithoutStartingASession()
    {
        using var viewModel = new MainViewModel(OutputRoot) { SourceIndex = 1, InputPath = "" };
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await viewModel.StartAsync();
            Assert.True(viewModel.HasError);
            Assert.False(viewModel.IsRunning);
            Assert.False(viewModel.HasRun);
            SavePreview(window, "file-validation.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ImageSourceUsesTheActualDetectorAndCompletes()
    {
        Directory.CreateDirectory(OutputRoot);
        var imagePath = Path.Combine(OutputRoot, "marker.png");
        using (var marker = ArucoTargetDetector.CreateMarker()) marker.SaveImage(imagePath);
        using var viewModel = new MainViewModel(OutputRoot) { SourceIndex = 1, InputPath = imagePath };
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await viewModel.StartAsync();
            Assert.Equal(1, viewModel.ProcessedFrames);
            Assert.True(viewModel.HasPreview);
            Assert.Equal("100.0%", viewModel.DetectionRate);
            Assert.Equal("处理完成", viewModel.StateLabel);
            Assert.True(viewModel.CanExport);
            viewModel.SourceIndex = 0;
            Assert.False(viewModel.HasPreview);
            Assert.False(viewModel.CanExport);
            Assert.Equal(0, viewModel.ProcessedFrames);
            Assert.Equal("—", viewModel.ErrorX);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingAnActiveWindowStopsTheSession()
    {
        using var viewModel = new MainViewModel(OutputRoot);
        var window = new MainWindow(viewModel);
        window.Show();
        var running = viewModel.StartAsync();
        try
        {
            await UntilAsync(() => viewModel.ProcessedFrames >= 3);
            window.Close();
            await UntilAsync(() => !window.IsVisible && !viewModel.IsRunning);
            await running;
        }
        finally
        {
            await viewModel.StopAsync();
            window.Close();
        }
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!predicate())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Desktop state did not update in time.");
            await Task.Delay(20);
        }
    }

    private static void SavePreview(Window window, string name)
    {
        var directory = Path.Combine(Workspace(), "artifacts", "desktop-preview");
        Directory.CreateDirectory(directory);
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(directory, name));
    }

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PanTiltTracker.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Workspace not found.");
    }
}
