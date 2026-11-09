using System.Text.Json;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Toboo.Desktop.ViewModels;
using Toboo.Desktop.Controls;
using Toboo.Vision;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class DesktopTests
{
    private static string OutputRoot => Path.Combine(Workspace(), "artifacts", "desktop-tests");

    [AvaloniaTheory]
    [InlineData(1280, 860)]
    [InlineData(980, 700)]
    public async Task VideoCanLoadSelectWithMouseTrackAndInvalidateRegion(int width, int height)
    {
        Directory.CreateDirectory(OutputRoot);
        var path = Path.Combine(OutputRoot, $"synthetic-tracking-{width}.avi");
        using (var writer = new OpenCvSharp.VideoWriter(path, OpenCvSharp.FourCC.MJPG, 30, new(640, 360)))
        {
            Assert.True(writer.IsOpened());
            using var scene = new SyntheticScene();
            for (var index = 0; index < 15; index++)
            {
                using var gray = scene.RenderAt(420 + index, 200);
                using var resized = new OpenCvSharp.Mat();
                using var color = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.Resize(gray, resized, new(640, 360));
                OpenCvSharp.Cv2.CvtColor(resized, color, OpenCvSharp.ColorConversionCodes.GRAY2BGR);
                writer.Write(color);
            }
        }
        using var model = new MainViewModel(OutputRoot) { SourceIndex = 2, AlgorithmIndex = 1, InputPath = path };
        var window = new AdvancedWindow(model) { Width = width, Height = height };
        try
        {
            window.Show();
            await model.StartAsync();
            Assert.True(model.HasError);
            Assert.False(model.HasRun);
            await model.LoadFirstFrameAsync();
            Assert.False(model.HasError);
            Assert.True(model.CanSelectRegion);
            await Task.Delay(80);
            var canvas = window.FindControl<Canvas>("SelectionCanvas")!;
            var bounds = ImageSelectionGeometry.ImageBounds(canvas.Bounds.Size, 640, 360);
            var start = canvas.TranslatePoint(new Point(bounds.X + bounds.Width * 350 / 640,
                bounds.Y + bounds.Height * 95 / 360), window)!.Value;
            var end = canvas.TranslatePoint(new Point(bounds.X + bounds.Width * 490 / 640,
                bounds.Y + bounds.Height * 205 / 360), window)!.Value;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Assert.NotNull(model.InitialRegion);
            Assert.InRange(model.InitialRegion!.Value.X, 349, 351);
            Assert.InRange(model.InitialRegion.Value.Y, 94, 96);
            SavePreview(window, $"video-selected-{width}.png");
            // Keyboard start may occur before an in-progress mouse selection is released.
            window.MouseDown(start, MouseButton.Left);
            var running = model.StartAsync();
            window.MouseUp(end, MouseButton.Left);
            await running;
            Assert.False(model.HasError, model.ErrorMessage);
            Assert.Equal(15, model.ProcessedFrames);
            Assert.Contains("CSRT", model.DetectionLabel);
            SavePreview(window, $"video-tracked-{width}.png");
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(model.LastRunDirectory, "run.json")));
            Assert.Equal("Csrt", metadata.RootElement.GetProperty("algorithm").GetString());
            Assert.Equal(JsonValueKind.Null, metadata.RootElement.GetProperty("markerId").ValueKind);
            Assert.True(File.ReadLines(Path.Combine(model.LastRunDirectory, "telemetry.csv")).First().Contains("box_width", StringComparison.Ordinal));
            await model.LoadFirstFrameAsync();
            model.SetInitialRegion(new(350, 95, 140, 110));
            model.InputPath = path + ".changed";
            Assert.Null(model.InitialRegion);
            Assert.False(model.HasPreview);
        }
        finally { await model.StopAsync(); window.Close(); }
    }

    [Fact]
    public void RegionSelectionAccountsForLetterboxingAndReverseDrag()
    {
        var viewport = new Size(800, 600);
        Assert.Null(ImageSelectionGeometry.Region(new(50, 20), new(200, 100), viewport, 1920, 1080));
        var region = ImageSelectionGeometry.Region(new(600, 400), new(200, 200), viewport, 1920, 1080);
        Assert.Equal(new OpenCvSharp.Rect(480, 300, 960, 480), region);
        Assert.Null(ImageSelectionGeometry.Region(new(200, 200), new(201, 201), viewport, 1920, 1080));
    }

    [AvaloniaTheory]
    [InlineData(1280, 860, "wide")]
    [InlineData(980, 700, "compact")]
    public async Task LayoutKeepsPrimaryActionsAndPreviewVisible(int width, int height, string name)
    {
        using var viewModel = new MainViewModel(OutputRoot);
        var window = new AdvancedWindow(viewModel) { Width = width, Height = height };
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
        var window = new AdvancedWindow(viewModel);
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
        var window = new AdvancedWindow(viewModel);
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
        var window = new AdvancedWindow(viewModel);
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
        var window = new AdvancedWindow(viewModel);
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
            if (File.Exists(Path.Combine(directory.FullName, "Toboo.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Workspace not found.");
    }
}
