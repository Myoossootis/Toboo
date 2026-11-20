using Toboo.Desktop.Services;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class FaceServoBoundaryTests
{
    [Fact]
    public void InvalidWorkingWindowsAreRejectedBeforeTransportStartup()
    {
        int[][] invalid = [[], [30, 175, 50], [30, 175, 50, 150, 0],
            [29, 175, 50, 150], [30, 176, 50, 150], [100, 100, 50, 150], [175, 30, 50, 150],
            [30, 175, -1, 150], [30, 175, 50, 181], [30, 175, 90, 90], [30, 175, 150, 50]];
        foreach (var bounds in invalid)
            Assert.Throws<ArgumentException>(() => FaceServoSession.SnapshotBounds(bounds, "both"));
        Assert.Throws<ArgumentNullException>(() => FaceServoSession.SnapshotBounds(null!, "both"));
        Assert.Throws<ArgumentException>(() => FaceServoSession.SnapshotBounds([30, 175, 50, 150], "S3"));
        Assert.Throws<ArgumentException>(() => FaceServoSession.CalculateTrialBounds([29, 175, 50, 150], new(103, 100), 5));
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    [InlineData("both")]
    public void SessionWindowIsACopyAndCannotBeExpandedByTheCaller(string axes)
    {
        int[] measured = [40, 160, 50, 150];
        var frozen = FaceServoSession.SnapshotBounds(measured, axes);
        measured[0] = 30; measured[1] = 175;
        Assert.Equal([40, 160, 50, 150], frozen);
    }

    [Fact]
    public async Task AlreadyCancelledStartDoesNotLaunchTheTransport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FaceServoSession.OpenAsync("COM6", [30, 175, 50, 150], "both", cancellation.Token));
    }
}
