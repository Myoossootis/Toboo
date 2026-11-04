using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PanTiltTracker.Desktop.ViewModels;
using Xunit;

namespace PanTiltTracker.Desktop.Tests;

public sealed class WorkbenchPreviewTests
{
    [AvaloniaTheory]
    [InlineData(1280, 860)]
    [InlineData(980, 700)]
    public async Task WorkbenchKeepsTheImageLargeAndBothSettingsGroupsAccessible(int width, int height)
    {
        var workspace = Workspace();
        var output = Path.Combine(workspace, "artifacts", "ui-refactor-preview");
        Directory.CreateDirectory(output);
        using var model = new MainViewModel(output);
        var window = new MainWindow(model) { Width = width, Height = height };
        Task? running = null;
        try
        {
            window.Show();
            await Task.Delay(80);
            Save("idle");
            var recorded = Environment.GetEnvironmentVariable("UAVTRACK_PREVIEW_VIDEO");
            if (!string.IsNullOrEmpty(recorded))
            {
                Assert.True(File.Exists(recorded), "Requested preview recording does not exist.");
                model.SourceIndex = 2;
                model.InputPath = recorded;
                var droneModel = Environment.GetEnvironmentVariable("UAVTRACK_PREVIEW_DRONE_MODEL");
                if (!string.IsNullOrEmpty(droneModel))
                {
                    model.TargetIndex = 2;
                    model.DroneModelPath = droneModel;
                }
                else
                {
                    model.AlgorithmIndex = 1;
                    await model.LoadFirstFrameAsync();
                    // This optional local preview uses the project's documented drone sample.
                    model.SetInitialRegion(new(770, 435, 590, 150));
                }
            }
            running = model.StartAsync();
            var until = DateTime.UtcNow.AddSeconds(20);
            while (model.ProcessedFrames < 12 && model.IsRunning && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.False(model.HasError, model.ErrorMessage);
            Assert.True(model.ProcessedFrames >= 12);
            await Task.Delay(80);
            var preview = window.FindControl<Border>("PreviewPane")!;
            Assert.True(preview.Bounds.Width > 560, $"Image width is only {preview.Bounds.Width}.");
            // Reserve at least half a VGA frame's height at the supported 700px window
            // height, including the extra toolbar controls visible during simulation.
            Assert.True(preview.Bounds.Height >= 240, $"Image height is only {preview.Bounds.Height}.");
            Save(model.IsDrone ? "drone-detection" : string.IsNullOrEmpty(recorded) ? "simulation" : "recorded-video");

            var analysis = window.FindControl<Expander>("AnalysisExpander")!;
            Assert.True(analysis.IsEffectivelyVisible);
            analysis.IsExpanded = true;
            await Task.Delay(80);
            var chart = window.FindControl<Controls.ErrorChart>("HistoryChart")!;
            Assert.True(chart.Bounds.Height > 40, $"Chart height is only {chart.Bounds.Height}.");
            Assert.True(preview.Bounds.Height > 120, $"Expanded analysis leaves only {preview.Bounds.Height} for the image.");
            Save("analysis");
            await model.StopAsync();
            await running;

            model.SourceIndex = 3;
            model.TargetIndex = 1;
            await Task.Delay(80);
            Assert.False(analysis.IsExpanded);
            Assert.True(window.FindControl<ComboBox>("TargetSelector")!.IsEffectivelyVisible);
            Save("camera-idle");
            window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 1;
            await Task.Delay(80);
            Assert.True(window.FindControl<Slider>("PanGainSlider")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("StartButton")!.IsEffectivelyVisible);
            Save("controls");

            void Save(string state)
            {
                using var image = window.CaptureRenderedFrame();
                Assert.NotNull(image);
                image.Save(Path.Combine(output, $"workbench-{width}-{state}.png"));
            }
        }
        finally
        {
            await model.StopAsync();
            if (running is not null) await running;
            window.Close();
        }
    }

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PanTiltTracker.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Workspace not found.");
    }
}
