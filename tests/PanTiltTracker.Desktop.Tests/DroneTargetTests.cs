using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using OpenCvSharp;
using System.Text.Json;
using PanTiltTracker.Desktop.Services;
using PanTiltTracker.Desktop.ViewModels;
using PanTiltTracker.Vision;
using Xunit;

namespace PanTiltTracker.Desktop.Tests;

public sealed class DroneTargetTests
{
    [DroneVideoFact]
    public async Task RealVideoHybridSessionLogsVisualTrackingWithoutInventingClassificationScores()
    {
        var output = Path.Combine(Path.GetTempPath(), "UAVTrack-drone-video", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var frames = new List<SessionFrame>();
        var options = new SessionOptions(InputSource.Video, Environment.GetEnvironmentVariable("UAVTRACK_DRONE_TEST_VIDEO")!,
            0,7,new(),output,DroneModelPath:Environment.GetEnvironmentVariable("UAVTRACK_DRONE_MODEL")!,TrackDrones:true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>TrackingSession.RunAsync(options,frame=>
        {
            frames.Add(frame);
            if(frames.Count>=8) cancellation.Cancel();
            return Task.CompletedTask;
        },()=>false,cancellation.Token));
        Assert.Equal(8,frames.Count);
        Assert.Contains(frames,f=>f.VisionStatus=="drone_acquired" && f.TargetScore is not null);
        Assert.Contains(frames,f=>f.VisionStatus=="drone_tracked" && f.Observation.HasTarget && f.TargetScore is null);
        var csv=File.ReadAllLines(Path.Combine(output,"telemetry.csv"));
        var header=csv[0].Split(',');
        var rows=csv.Skip(1).Select(row=>header.Zip(row.Split(',')).ToDictionary(p=>p.First,p=>p.Second)).ToArray();
        var visual=rows.First(row=>row["target_source"]=="csrt");
        Assert.Empty(visual["target_score"]); Assert.Empty(visual["target_class"]);
        Assert.Empty(visual["candidate_count"]); Assert.NotEmpty(visual["track_epoch"]);
        using var run=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"run.json")));
        Assert.EndsWith("_csrt",run.RootElement.GetProperty("algorithm").GetString());
        Assert.Equal("disabled",run.RootElement.GetProperty("hardwareOutput").GetString());
    }

    [DroneImageFact]
    public async Task ActualModelImageThenBlankKeepsPreviewLogsScoresAndClearsTheTarget()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UAVTrack-drone-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var positiveOutput = Path.Combine(folder, "positive");
        var options = new SessionOptions(InputSource.Image, Environment.GetEnvironmentVariable("UAVTRACK_DRONE_TEST_IMAGE")!,
            0, 7, new(), positiveOutput, DroneModelPath: Environment.GetEnvironmentVariable("UAVTRACK_DRONE_MODEL")!);
        SessionFrame? presented = null;
        await TrackingSession.RunAsync(options, frame => { presented = frame; return Task.CompletedTask; },
            () => false, CancellationToken.None);
        Assert.NotNull(presented);
        Assert.True(presented.Observation.HasTarget);
        Assert.True(presented.TargetCount > 0);
        Assert.Equal("drone_detected", presented.VisionStatus);
        Assert.Equal(presented.Width * presented.Height * 4, presented.Pixels.Length);
        var lines = File.ReadAllLines(Path.Combine(positiveOutput, "telemetry.csv"));
        var row = lines[0].Split(',').Zip(lines[1].Split(',')).ToDictionary(x => x.First, x => x.Second);
        Assert.Equal("drone", row["target_class"]); Assert.NotEmpty(row["target_score"]);
        Assert.Equal("drone_detected", row["vision_status"]);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(positiveOutput, "run.json")));
        Assert.Equal("yolov5_drone", metadata.RootElement.GetProperty("algorithm").GetString());
        Assert.Equal(JsonValueKind.Null, metadata.RootElement.GetProperty("markerId").ValueKind);
        Assert.Equal(DroneTargetDetector.ModelSha256, metadata.RootElement.GetProperty("experiment")
            .GetProperty("drone").GetProperty("modelSha256").GetString());

        var blankPath = Path.Combine(folder, "blank.png");
        using (var blank = new Mat(480,640,MatType.CV_8UC3,Scalar.Black)) blank.SaveImage(blankPath);
        var blankOutput = Path.Combine(folder, "blank");
        await TrackingSession.RunAsync(options with { InputPath = blankPath, OutputDirectory = blankOutput },
            frame => { presented = frame; return Task.CompletedTask; }, () => false, CancellationToken.None);
        Assert.False(presented.Observation.HasTarget);
        Assert.Equal("no_drone_detected", presented.VisionStatus);
        Assert.Null(presented.TargetScore); Assert.Equal(0, presented.TargetCount);
        Assert.Equal(presented.Width * presented.Height * 4, presented.Pixels.Length);
        lines = File.ReadAllLines(Path.Combine(blankOutput, "telemetry.csv"));
        row = lines[0].Split(',').Zip(lines[1].Split(',')).ToDictionary(x => x.First, x => x.Second);
        Assert.Equal("0", row["detected"]); Assert.Empty(row["u_px"]);
        Assert.Empty(row["target_score"]); Assert.Empty(row["target_class"]);
        Assert.Equal("0", row["candidate_count"]);
    }

    [AvaloniaFact]
    public async Task DroneChoiceWorksForCameraAndFilesButNotSimulationOrCsrt()
    {
        using var model = new MainViewModel { SourceIndex = 3 };
        var window = new MainWindow(model);
        try
        {
            window.Show();
            window.FindControl<ComboBox>("TargetSelector")!.SelectedIndex = 2;
            await Task.Delay(30);
            Assert.True(model.IsDrone); Assert.False(model.IsFace); Assert.False(model.IsAruco);
            Assert.True(window.FindControl<StackPanel>("DroneModelPanel")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<NumericUpDown>("MarkerIdInput")!.IsEffectivelyVisible);
            model.SourceIndex = 0;
            Assert.False(model.IsDrone); Assert.True(model.IsAruco);
            model.SourceIndex = 1; Assert.True(model.IsDrone);
            model.SourceIndex = 2; Assert.True(model.IsDrone);
            Assert.True(model.IsDroneStream);
            Assert.True(window.FindControl<CheckBox>("DroneTrackingToggle")!.IsEffectivelyVisible);
            model.DroneTrackingEnabled = true;
            Assert.Contains("CSRT",model.AlgorithmDescription);
            model.SourceIndex = 1;
            Assert.False(model.IsDroneStream);
            Assert.DoesNotContain("CSRT",model.AlgorithmDescription);
            model.SourceIndex = 2;
            model.AlgorithmIndex = 1;
            Assert.False(model.IsDrone); Assert.True(model.IsCsrt);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MissingModelStopsBeforeCameraAccessWithAnActionableMessage()
    {
        using var model = new MainViewModel { SourceIndex = 3, TargetIndex = 2,
            DroneModelPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx") };
        await model.StartAsync();
        Assert.False(model.IsRunning); Assert.False(model.HasRun);
        Assert.Contains("prepare-drone-model.ps1", model.ErrorMessage);
    }

    [Fact]
    public async Task InvalidTargetCombinationsAreRejectedBeforeModelOrCameraAccess()
    {
        var options = new SessionOptions(InputSource.Camera, "", 0, 7, new(), "unused",
            DetectFaces: true, DroneModelPath: "unused.onnx");
        await Assert.ThrowsAsync<ArgumentException>(() => TrackingSession.RunAsync(options, _ => Task.CompletedTask,
            () => false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => TrackingSession.RunAsync(options with
            { Source = InputSource.Simulation, DetectFaces = false }, _ => Task.CompletedTask,
            () => false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => TrackingSession.RunAsync(options with
            { Source = InputSource.Video, DetectFaces = false, Algorithm = VideoAlgorithm.Csrt }, _ => Task.CompletedTask,
            () => false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => TrackingSession.RunAsync(options with
            { Source = InputSource.Image, DetectFaces = false, TrackDrones = true }, _ => Task.CompletedTask,
            () => false, CancellationToken.None));
    }
}

public sealed class DroneImageFactAttribute : FactAttribute
{
    public DroneImageFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UAVTRACK_DRONE_MODEL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UAVTRACK_DRONE_TEST_IMAGE")))
            Skip = "Optional real-image integration: set UAVTRACK_DRONE_MODEL and UAVTRACK_DRONE_TEST_IMAGE.";
    }
}

public sealed class DroneVideoFactAttribute : FactAttribute
{
    public DroneVideoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UAVTRACK_DRONE_MODEL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UAVTRACK_DRONE_TEST_VIDEO")))
            Skip="Optional real-video integration: set UAVTRACK_DRONE_MODEL and UAVTRACK_DRONE_TEST_VIDEO.";
    }
}
