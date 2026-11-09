using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using OpenCvSharp;
using PanTiltTracker.Desktop.ViewModels;
using Xunit;

namespace PanTiltTracker.Desktop.Tests;

public sealed class FaceTargetTests
{
    [AvaloniaFact]
    public async Task FaceChoiceWorksForCameraAndDoesNotReplaceSimulationOrVideoTrackers()
    {
        using var model = new MainViewModel { SourceIndex = 3 };
        var window = new AdvancedWindow(model);
        try
        {
            window.Show();
            var selector = window.FindControl<ComboBox>("TargetSelector")!;
            selector.SelectedIndex = 1;
            await Task.Delay(30);
            Assert.Equal(1, model.TargetIndex);
            Assert.True(model.IsFace);
            Assert.False(model.IsAruco);
            Assert.True(selector.IsEffectivelyVisible);
            Assert.False(window.FindControl<NumericUpDown>("MarkerIdInput")!.IsEffectivelyVisible);
            model.SourceIndex = 0;
            Assert.False(model.IsFace);
            Assert.True(model.IsAruco);
            Assert.False(model.CanChooseTarget);
            model.SourceIndex = 2;
            Assert.True(model.IsFace);
            model.AlgorithmIndex = 1;
            Assert.False(model.IsFace);
            Assert.True(model.IsCsrt);
            Assert.False(model.CanChooseTarget);
            model.SourceIndex = 3;
            Assert.True(model.IsFace);
            Assert.Contains("人脸", model.AlgorithmDescription);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FaceImageSessionKeepsPreviewWithoutFacesAndLogsTheCorrectAlgorithm()
    {
        var output = Path.Combine(Path.GetTempPath(), "UAVTrack-face-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "blank.png");
        using (var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(40))) blank.SaveImage(path);
        using var model = new MainViewModel(output) { SourceIndex = 1, InputPath = path, TargetIndex = 1 };
        var window = new AdvancedWindow(model);
        try
        {
            window.Show();
            await model.StartAsync();
            Assert.False(model.HasError, model.ErrorMessage);
            Assert.True(model.HasPreview);
            Assert.Equal(1, model.ProcessedFrames);
            Assert.Equal("未检测到人脸", model.DetectionLabel);
            Assert.Equal("—", model.TargetPosition);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(model.LastRunDirectory, "run.json")));
            Assert.Equal("yunet_face", metadata.RootElement.GetProperty("algorithm").GetString());
            Assert.Equal(JsonValueKind.Null, metadata.RootElement.GetProperty("markerId").ValueKind);
            Assert.Equal(JsonValueKind.Null, metadata.RootElement.GetProperty("dictionary").ValueKind);
            var csv = File.ReadAllLines(Path.Combine(model.LastRunDirectory, "telemetry.csv"));
            Assert.Contains("no_face_detected,0", csv[1]);
            model.TargetIndex = 0;
            Assert.True(model.IsAruco);
            Assert.False(model.HasPreview);
        }
        finally { await model.StopAsync(); window.Close(); }
    }
}
