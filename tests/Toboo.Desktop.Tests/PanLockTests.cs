using System.Text.Json;
using Toboo.Desktop.ViewModels;
using Toboo.Runtime;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class PanLockTests
{
    [Fact]
    public async Task LockedReplacementCannotNudgeHomePatternOrFollowEvenWithLegacyConfirmedBounds()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var calls = new List<string[]>();
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new { Pan = 103, PanFloor = 30, PanCeiling = 175,
                Tilt = 100, TiltFloor = 50, TiltCeiling = 150, TiltProfile = ServoViewModel.TiltProfileId,
                TiltConfirmed = true, PanMotionBlocked = true }));
            var model = new ServoViewModel((args, _) => { calls.Add(args.ToArray()); return Task.FromResult(Report(false)); }, file, () => ["COM6"]);
            await model.InspectCommand.ExecuteAsync(null); model.Ready = true; model.SweepReady = true;
            Assert.False(model.CanPanUp); Assert.False(model.CanPanDown);
            Assert.False(model.CanCalibratedMotion); Assert.False(model.CanFollow); Assert.False(model.CanPattern);
            Assert.False(model.ReserveFollow());
            await model.PanUpCommand.ExecuteAsync(null); await model.PanDownCommand.ExecuteAsync(null);
            await model.HomeCommand.ExecuteAsync(null); await model.PatternCommand.ExecuteAsync(null);
            Assert.Single(calls); Assert.Contains("inspect", calls[0]);
            Assert.True(model.TiltCalibrationSaved); Assert.True(model.CanTiltUp);
            model.PanMin = 80; model.PanMax = 100;
            model.SaveCalibrationCommand.Execute(null);
            var restored = new ServoViewModel((_, _) => Task.FromResult(Report(false)), file, () => ["COM6"]);
            Assert.True(restored.PanMotionBlocked); Assert.True(restored.TiltCalibrationSaved);
            Assert.Contains("锁定", restored.CenterLabel);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task AllMotionLockRejectsTiltStepsReferenceAndSavingUnverifiedBounds()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var calls = new List<string[]>();
        try
        {
            var model = new ServoViewModel((args, _) => {
                calls.Add(args.ToArray());
                return Task.FromResult(new ServoTool.Result(0, JsonSerializer.Serialize(new {
                    firmware = "0.5.3", pan_output_locked = true, all_motion_locked = true, final_state = "DISARMED",
                    axes = new { S1 = new { last_requested = 103 }, S2 = new { last_requested = 100 } } }), ""));
            }, file, () => ["COM6"]);
            await model.InspectCommand.ExecuteAsync(null); model.Ready = true;
            Assert.True(model.TiltMotionBlocked); Assert.False(model.CanTiltUp); Assert.False(model.CanTiltDown);
            Assert.False(model.CanInitializeTilt); Assert.False(model.CanCaptureTilt);
            await model.TiltUpCommand.ExecuteAsync(null); await model.TiltDownCommand.ExecuteAsync(null);
            await model.InitializeTiltCommand.ExecuteAsync(null);
            model.SaveCalibrationCommand.Execute(null);
            Assert.Single(calls); Assert.False(model.TiltCalibrationSaved);
            var restored = new ServoViewModel((_, _) => Task.FromResult(Report(false)), file, () => ["COM6"]);
            Assert.True(restored.PanMotionBlocked && restored.TiltMotionBlocked);
            Assert.False(restored.TiltCalibrationSaved);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task FirmwareLockPersistsWithoutSilentlyConfirmingUnverifiedTiltBounds()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var model = new ServoViewModel((_, _) => Task.FromResult(Report(true)), file, () => ["COM6"]);
            await model.InspectCommand.ExecuteAsync(null);
            Assert.True(model.PanMotionBlocked); Assert.False(model.TiltCalibrationSaved);
            var restored = new ServoViewModel((_, _) => Task.FromResult(Report(false)), file, () => ["COM6"]);
            Assert.True(restored.PanMotionBlocked); Assert.False(restored.TiltCalibrationSaved);
        }
        finally { File.Delete(file); }
    }

    private static ServoTool.Result Report(bool locked) => new(0, JsonSerializer.Serialize(new {
        firmware = "0.5.3", pan_output_locked = locked, final_state = "DISARMED",
        axes = new { S1 = new { last_requested = 103 }, S2 = new { last_requested = 100 } } }), "");
}
