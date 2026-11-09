using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using OpenCvSharp;
using Toboo.Core;
using Toboo.Desktop.Services;
using Toboo.Desktop.ViewModels;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class ExpressionIntegrationTests
{
    [FaceAimSampleFact]
    public async Task PublicFaceVideoUsesSelectedTargetAndClearsImmediatelyOnBlankFrames()
    {
        var directory = Path.Combine(Workspace(), "artifacts", "expression-validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "public-sample.avi");
        using (var source = Cv2.ImRead(Environment.GetEnvironmentVariable("UAVTRACK_FACE_TEST_IMAGE")!))
        using (var resized = new Mat())
        using (var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(40)))
        using (var writer = new VideoWriter(path, FourCC.MJPG, 10, new(640, 480)))
        {
            Assert.True(writer.IsOpened()); Cv2.Resize(source, resized, new(640, 480));
            for (var i = 0; i < 20; i++) writer.Write(resized);
            for (var i = 0; i < 4; i++) writer.Write(blank);
        }
        var frames = new List<SessionFrame>();
        var options = new SessionOptions(InputSource.Video, path, 0, 7, new(), directory,
            DetectFaces: true, FaceAim: new(), DetectExpressions: true);
        await TrackingSession.RunAsync(options, f => { frames.Add(f); return Task.CompletedTask; }, () => false, CancellationToken.None);
        Assert.Equal(24, frames.Count);
        Assert.All(frames.Take(2), f => Assert.Equal("no_selected_face", f.Expression!.Estimate.Reason));
        var inferred = frames.Where(f => f.Expression!.WasInferred).ToArray();
        Assert.NotEmpty(inferred);
        Assert.All(inferred, f => {
            Assert.NotNull(f.FaceAim!.SelectedBox); Assert.True(f.Observation.HasTarget);
            Assert.Equal(f.FaceAim.LockEpoch, f.Expression!.TargetEpoch);
            Assert.Equal(7, f.Expression.Estimate.Scores.Length);
            Assert.False(f.FaceAim.RequestPermitted); // This experiment never arms hardware.
        });
        Assert.InRange(inferred.Length, 1, 10);
        Assert.All(frames.Skip(20), f => {
            Assert.Equal(FacialExpression.Unknown, f.Expression!.Estimate.Label);
            Assert.Empty(f.Expression.Estimate.Scores);
        });
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "run.json")));
        Assert.Equal("disabled", metadata.RootElement.GetProperty("hardwareOutput").GetString());
        Assert.Equal(24, File.ReadLines(Path.Combine(directory, "expression.jsonl")).Count());
        File.WriteAllText(Path.Combine(directory, "validation.json"), JsonSerializer.Serialize(new {
            source = "public sample converted to recorded video; no expression ground truth",
            frames = frames.Count, inferences = inferred.Length,
            averageInferenceMs = inferred.Average(f => f.Expression!.InferenceMilliseconds),
            hardwareOutput = "disabled", accuracyMeasured = false }));
    }

    [AvaloniaTheory]
    [InlineData(1280, 860)]
    [InlineData(980, 700)]
    public async Task ExpressionTabRendersScoresAndDoesNotLeaveStaleLabels(int width, int height)
    {
        using var model = new MainViewModel { SourceIndex = 3, TargetIndex = 1, IsRunning = true };
        var window = new AdvancedWindow(model) { Width = width, Height = height };
        try
        {
            window.Show();
            var estimate = new ExpressionEstimate(FacialExpression.Happy, 0.9, 0.8, "stable", TimeSpan.Zero,
                [0.02, 0.01, 0.01, 0.9, 0.03, 0.01, 0.02]);
            model.Present(new(new byte[640 * 480 * 4], 640, 480, new(0, TimeSpan.Zero, 640, 480, 320, 240),
                new(103, 100, TrackingState.Centered, 0, 0), TimeSpan.Zero, 1, VisionStatus: "face_locked", TargetCount: 1, Expression: new(estimate, 3)));
            window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4;
            await Task.Delay(80);
            Assert.Equal("(^_^)", model.ExpressionEmoticon);
            Assert.Equal(7, model.ExpressionScores.Count);
            Assert.True(window.FindControl<TextBlock>("ExpressionDetailsLabel")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<CheckBox>("ExpressionToggle")!.IsEffectivelyEnabled);
            using (var screenshot = window.CaptureRenderedFrame())
            {
                Assert.NotNull(screenshot);
                var directory = Path.Combine(Workspace(), "artifacts", "expression-preview");
                Directory.CreateDirectory(directory);
                screenshot.Save(Path.Combine(directory, $"synthetic-state-{width}.png"));
            }
            await Task.Delay(650);
            Assert.Equal("(?)", model.ExpressionEmoticon);
            Assert.Contains("过期", model.ExpressionLabel);
            Assert.Empty(model.ExpressionScores);
        }
        finally { model.IsRunning = false; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ImageModeAndDisabledOptionRemainIndependentOfActuators()
    {
        var directory = Path.Combine(Workspace(), "artifacts", "expression-validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "blank.png");
        using (var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(70))) Cv2.ImWrite(input, blank);
        using var model = new MainViewModel(directory) { SourceIndex = 1, TargetIndex = 1, InputPath = input, ExpressionEnabled = false };
        await model.StartAsync();
        Assert.False(model.HasError, model.ErrorMessage);
        Assert.True(model.HasPreview); Assert.False(model.IsFollowing);
        Assert.False(File.Exists(Path.Combine(model.LastRunDirectory, "expression.jsonl")));
        model.ExpressionEnabled = true;
        await Task.Delay(3); await model.StartAsync();
        Assert.False(model.HasError, model.ErrorMessage);
        Assert.Contains("没有已确认", model.ExpressionDetail);
        Assert.True(File.Exists(Path.Combine(model.LastRunDirectory, "expression.jsonl")));
    }

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Toboo.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
