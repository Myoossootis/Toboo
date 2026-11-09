using System.Text.Json;
using Toboo.Core;
using Toboo.Desktop.ViewModels;
using Toboo.Runtime;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class S2CalibrationTests
{
    [Fact]
    public async Task Verified50To150BoundsAreSharedByHomePatternAndFollowWithoutOld45Reference()
    {
        var file = TemporaryFile();
        var actions = new List<string[]>();
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new { Pan=103,Tilt=100,PanFloor=30,PanCeiling=175,
                TiltFloor=50,TiltCeiling=150,TiltProfile=ServoViewModel.TiltProfileId,TiltConfirmed=true }));
            var model = new ServoViewModel((arguments, _) =>
            { actions.Add(arguments.ToArray()); return Task.FromResult(Report(103,100)); },file);
            model.Ready=true;
            await model.InspectCommand.ExecuteAsync(null);
            Assert.Equal(100,model.CenterTilt);
            Assert.False(model.ShowTiltReference); Assert.False(model.CanInitializeTilt);
            await model.InitializeTiltCommand.ExecuteAsync(null);
            Assert.DoesNotContain(actions,a=>a.Contains("reference"));
            await model.HomeCommand.ExecuteAsync(null);
            model.SweepReady=true;
            await model.PatternCommand.ExecuteAsync(null);
            foreach (var action in new[]{"home","pattern"})
            {
                var args=Assert.Single(actions,a=>a.Contains(action));
                Assert.Equal("50",args[Array.IndexOf(args,"--bottom")+1]);
                Assert.Equal("150",args[Array.IndexOf(args,"--top")+1]);
                Assert.Equal("30",args[Array.IndexOf(args,"--left")+1]);
                Assert.Equal("175",args[Array.IndexOf(args,"--right")+1]);
            }
            Assert.Equal([30,175,50,150],model.FollowBounds);
            Assert.True(model.ReserveFollow());
            await model.ReleaseFollowAsync(normalStop:true);
            Assert.True(model.CanFollow);
            model.BeginTiltCalibrationCommand.Execute(null);
            Assert.Equal(50,model.TiltFloor); Assert.Equal(150,model.TiltCeiling);
            Assert.False(model.ShowTiltReference);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task LegacyS2IsDiscardedWhileValidS1BoundsArePreserved()
    {
        var file = TemporaryFile();
        try
        {
            File.WriteAllText(file, """{"Pan":95,"Tilt":50,"PanFloor":40,"PanCeiling":150,"TiltFloor":35,"TiltCeiling":65}""");
            var model = new ServoViewModel((_, _) => Task.FromResult(Report(95, 90)), file);
            Assert.Equal(40, model.PanMin); Assert.Equal(150, model.PanMax);
            Assert.Equal(45, model.TiltFloor); Assert.Equal(90, model.TiltCeiling);
            Assert.True(model.TiltCalibrating); Assert.False(model.TiltCalibrationSaved);
            model.Ready = true;
            await model.InspectCommand.ExecuteAsync(null);
            Assert.True(model.CanTiltUp); // Calibration may test 91, beyond the provisional 90 boundary.
            Assert.False(model.CanCalibratedMotion); Assert.False(model.CanFollow);
            model.SweepReady = true;
            Assert.False(model.CanPattern);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task CalibrationStepsAreSingleS2CommandsAndSavedLimitsRestoreIndependently()
    {
        var file = TemporaryFile();
        var tilt = 90;
        var commands = new List<string[]>();
        try
        {
            var model = new ServoViewModel((arguments, _) =>
            {
                var args = arguments.ToArray(); commands.Add(args);
                var action = args[Array.IndexOf(args, "--action") + 1];
                if (action != "nudge") return Task.FromResult(Report(103, tilt));
                Assert.Equal("S2", args[Array.IndexOf(args, "--axis") + 1]);
                Assert.Equal("0", args[Array.IndexOf(args, "--tilt-floor") + 1]);
                Assert.Equal("180", args[Array.IndexOf(args, "--tilt-ceiling") + 1]);
                var from = tilt;
                tilt += int.Parse(args[Array.IndexOf(args, "--delta") + 1]);
                return Task.FromResult(Report(103, tilt, new { axis = "S2", from, to = tilt, uart_frames_sent = 1 }));
            }, file);
            model.Ready = true;
            await model.InspectCommand.ExecuteAsync(null);
            await model.TiltUpCommand.ExecuteAsync(null);
            Assert.Equal(91, tilt);
            Assert.Contains("91°", model.LastRequests);
            await model.TiltDownCommand.ExecuteAsync(null);
            Assert.Equal(90, tilt);
            model.CaptureTiltCeilingCommand.Execute(null);
            tilt = 45;
            await model.InspectCommand.ExecuteAsync(null);
            model.CaptureTiltFloorCommand.Execute(null);
            model.SaveCalibrationCommand.Execute(null);
            Assert.True(model.TiltCalibrationSaved); Assert.False(model.TiltCalibrating);
            Assert.Equal(68, model.CenterTilt);
            var restored = new ServoViewModel((_, _) => Task.FromResult(Report(103, 90)), file);
            Assert.True(restored.TiltCalibrationSaved); Assert.False(restored.TiltCalibrating);
            Assert.Equal(45, restored.TiltFloor); Assert.Equal(90, restored.TiltCeiling);
            restored.Ready = true;
            await restored.InspectCommand.ExecuteAsync(null);
            Assert.False(restored.CanTiltUp); Assert.True(restored.CanFollow);
            restored.TiltCeiling = 95;
            Assert.False(restored.CanFollow); // Edited boundaries must be confirmed by saving again.
            restored.BeginTiltCalibrationCommand.Execute(null);
            var pending = new ServoViewModel((_, _) => Task.FromResult(Report(103, 90)), file);
            Assert.True(pending.TiltCalibrating); Assert.False(pending.TiltCalibrationSaved);
            Assert.Equal(30, pending.PanMin); Assert.Equal(175, pending.PanMax);
            Assert.Equal(2, commands.Count(a => a.Contains("nudge")));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task S2ReferenceIsExplicitAndCannotRunBeforeSupplyConfirmation()
    {
        var file = TemporaryFile();
        var commands = new List<string[]>();
        try
        {
            var model = new ServoViewModel((args, _) =>
            { commands.Add(args.ToArray()); return Task.FromResult(Report(-1, 45)); }, file);
            await model.InitializeTiltCommand.ExecuteAsync(null);
            Assert.Empty(commands);
            await model.InspectCommand.ExecuteAsync(null);
            Assert.False(model.CanInitializeTilt);
            model.Ready = true;
            await model.InitializeTiltCommand.ExecuteAsync(null);
            var reference = Assert.Single(commands, a => a.Contains("reference"));
            Assert.Contains("--execute", reference);
            Assert.Equal("S2", reference[Array.IndexOf(reference, "--axis") + 1]);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void LegacyFaceProfileKeepsCameraAndPanButRequiresNewTiltDirectionConfirmation()
    {
        var file = TemporaryFile();
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new FaceAimSettings {
                Camera = CameraAngleModel.FromFieldOfView(640, 480, 60, 45),
                DirectionsConfirmed = true, PanDirection = -1, TiltDirection = -1, TiltMinDegrees = 35, TiltMaxDegrees = 65 }));
            var model = new FaceAimViewModel(file);
            var settings = model.Snapshot();
            Assert.Equal(-1, settings.PanDirection);
            Assert.Equal(640, settings.Camera!.Width);
            Assert.False(settings.DirectionsConfirmed);
            Assert.Equal(45, settings.TiltMinDegrees); Assert.Equal(90, settings.TiltMaxDegrees);
            model.DirectionsConfirmed = true;
            model.SaveCommand.Execute(null);
            Assert.True(new FaceAimViewModel(file).DirectionsConfirmed);
        }
        finally { File.Delete(file); }
    }

    private static string TemporaryFile() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    private static ServoTool.Result Report(int pan, int tilt, object? nudge = null) => new(0,
        JsonSerializer.Serialize(new { firmware = "0.5.3", final_state = "DISARMED",
            axes = new { S1 = new { last_requested = pan }, S2 = new { last_requested = tilt } }, nudge }), "");
}
