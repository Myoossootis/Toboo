using System.Text.Json;
using PanTiltTracker.Desktop.ViewModels;
using PanTiltTracker.Shared;
using Xunit;

namespace PanTiltTracker.Desktop.Tests;

public sealed class ServoFollowRestartTests
{
    [Fact]
    public async Task NormalStopReadsFreshStatusAndCanRestartWithoutNewPowerConfirmation()
    {
        var actions = new List<string>();
        var pan = 107;
        var model = Model((arguments, _) =>
        {
            var args = arguments.ToArray();
            Assert.DoesNotContain("--execute", args);
            actions.Add(args[Array.IndexOf(args, "--action") + 1]);
            return Task.FromResult(Report(pan, 53));
        });
        model.Ready = true;
        await model.InspectCommand.ExecuteAsync(null);
        Assert.True(model.CanFollow);
        Assert.True(model.ReserveFollow());
        pan = 112;
        await model.ReleaseFollowAsync(normalStop: true);
        Assert.True(model.Ready);
        Assert.True(model.CanFollow);
        Assert.False(model.Busy);
        Assert.Contains("112°", model.LastRequests);
        Assert.Contains("可直接再次开始", model.Status);
        Assert.True(model.ReserveFollow());
        await model.ReleaseFollowAsync(normalStop: true);
        Assert.True(model.CanFollow);
        Assert.Equal(["inspect", "inspect", "inspect"], actions);
    }

    [Theory]
    [InlineData(-1, -1, "DISARMED")]
    [InlineData(112, 53, "TIMED_OUT")]
    public async Task UnknownReferencesOrTimeoutNeverReenableFollow(int pan, int tilt, string state)
    {
        var report = Report(107, 53);
        var model = Model((_, _) => Task.FromResult(report));
        model.Ready = true;
        await model.InspectCommand.ExecuteAsync(null);
        Assert.True(model.ReserveFollow());
        report = Report(pan, tilt, state);
        await model.ReleaseFollowAsync(normalStop: true);
        Assert.False(model.Ready);
        Assert.False(model.CanFollow);
        Assert.False(model.Busy);
    }

    [Fact]
    public async Task FaultStopClearsConfirmationAndDoesNotPretendToRecover()
    {
        var queries = 0;
        var model = Model((_, _) => { queries++; return Task.FromResult(Report(107, 53)); });
        model.Ready = true;
        await model.InspectCommand.ExecuteAsync(null);
        Assert.True(model.ReserveFollow());
        await model.ReleaseFollowAsync(normalStop: false);
        Assert.Equal(1, queries);
        Assert.False(model.Ready);
        Assert.False(model.CanFollow);
        Assert.False(model.Busy);
    }

    [Fact]
    public async Task FailedReadAfterStopClearsConfirmation()
    {
        var report = Report(107, 53);
        var model = Model((_, _) => Task.FromResult(report));
        model.Ready = true;
        await model.InspectCommand.ExecuteAsync(null);
        Assert.True(model.ReserveFollow());
        report = new(1, "", "USB disconnected");
        await model.ReleaseFollowAsync(normalStop: true);
        Assert.False(model.CanFollow);
        Assert.False(model.Ready);
    }

    private static ServoViewModel Model(Func<IEnumerable<string>, CancellationToken, Task<ServoTool.Result>> run)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { Pan = 103, Tilt = 68, PanFloor = 30, PanCeiling = 175,
            TiltFloor = 45, TiltCeiling = 90, TiltProfile = ServoViewModel.TiltProfileId, TiltConfirmed = true }));
        var model = new ServoViewModel(run, file);
        File.Delete(file);
        return model;
    }

    private static ServoTool.Result Report(int pan, int tilt, string state = "DISARMED") => new(0,
        JsonSerializer.Serialize(new { firmware = "0.5.3", final_state = state,
            axes = new { S1 = new { last_requested = pan }, S2 = new { last_requested = tilt } } }), "");
}
