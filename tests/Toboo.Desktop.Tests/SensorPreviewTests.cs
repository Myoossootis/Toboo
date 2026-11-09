using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Toboo.Core;
using Toboo.Desktop.ViewModels;
using Toboo.Runtime;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class SensorPreviewTests
{
    [AvaloniaTheory]
    [InlineData(720, 720)]
    [InlineData(480, 640)]
    public async Task SensorPanelShowsProtectionAndInvalidDataAtBothWidths(int width, int height)
    {
        using var model = new SensorViewModel { Source = "合成回放 · 无硬件输出" };
        var window = new SensorWindow(model) { Width = width, Height = height };
        try
        {
            window.Show();
            var states = new HashSet<CompanionState>();
            foreach (var item in SensorExperiment.Demo())
            {
                model.PreviewEvent(item);
                states.Add(model.Latest!.State);
                if (item.AtMilliseconds == 4050 && item.Line is null)
                {
                    Assert.Equal("距离保护", model.State);
                    await Task.Delay(60);
                    var label = window.FindControl<TextBlock>("FusionStateLabel")!;
                    Assert.True(label.IsEffectivelyVisible);
                    Assert.True(label.Bounds.Width < width);
                    using var screenshot = window.CaptureRenderedFrame();
                    Assert.NotNull(screenshot);
                    var directory = Path.Combine(Workspace(), "artifacts", "sensor-preview");
                    Directory.CreateDirectory(directory);
                    screenshot.Save(Path.Combine(directory, $"distance-{width}.png"));
                }
                if (item.AtMilliseconds == 11400 && item.Line is null)
                {
                    Assert.Equal(CompanionState.WaitingForSensors, model.Latest.State);
                    Assert.Contains("过期", model.Acceleration);
                    Assert.False(model.Latest.MotionCandidate);
                }
            }
            Assert.Contains(CompanionState.Companion, states);
            Assert.Contains(CompanionState.PostureProtection, states);
            Assert.Contains(CompanionState.DistanceProtection, states);
            Assert.Contains(CompanionState.WaitingForSensors, states);
            Assert.Contains(CompanionState.AwaitingResume, states);
            Assert.Contains(CompanionState.Resting, states);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task StoppingDemoClosesItsLogBeforeAllowingRestart()
    {
        using var model = new SensorViewModel();
        var run = model.DemoCommand.ExecuteAsync(null);
        await Task.Delay(75);
        var path = model.LogPath;
        Assert.True(model.Busy);
        model.StopCommand.Execute(null);
        Assert.False(model.DemoCommand.CanExecute(null));
        await run;
        Assert.False(model.Busy);
        Assert.False(model.Latest!.MotionCandidate);
        var events = SensorExperiment.Read(path).ToArray();
        Assert.True(events.Last().Disconnected);
        Assert.Contains("synthetic", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "run.json")));
        var receiver = new SensorTelemetry(); var fusion = new CompanionFusionPreview();
        foreach (var item in events) SensorExperiment.Apply(item, receiver, fusion);
        Assert.False(receiver.Connected);
        Assert.True(model.DemoCommand.CanExecute(null));
    }

    [Fact]
    public void RecordedReplayRejectsNonmonotonicTime()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, ["{\"AtMilliseconds\":100}", "{\"AtMilliseconds\":99}"]);
            Assert.Throws<InvalidDataException>(() => SensorExperiment.Read(path).ToArray());
        }
        finally { File.Delete(path); }
    }

    private static string Workspace()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Toboo.sln"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
}
