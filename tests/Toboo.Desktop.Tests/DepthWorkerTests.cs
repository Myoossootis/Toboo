using OpenCvSharp;
using Toboo.Desktop.Services;
using Toboo.Vision;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class DepthWorkerTests
{
    [DepthModelFact]
    public async Task WorkerConsumesLatestFrameAndDisposesNativeInputOnCancellation()
    {
        using var buffer = new LatestFrameBuffer();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var old = new Mat(120, 160, MatType.CV_8UC3, Scalar.White);
        var current = new Mat(120, 160, MatType.CV_8UC3, new Scalar(60, 100, 130));
        buffer.Publish(new(1, TimeSpan.Zero, old));
        buffer.Publish(new(2, TimeSpan.FromMilliseconds(20), current));
        Assert.True(old.IsDisposed);
        var count = 0;
        string? error = null;
        await Task.Run(() => DepthPreviewWorker.RunAsync(buffer, (frame, at) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(20), at);
            Assert.True(frame.Points.Length > 8000);
            count++;
            cancel.Cancel();
            return Task.CompletedTask;
        }, message => error = message, cancel.Token));
        Assert.Null(error);
        Assert.Equal(1, count);
        Assert.True(current.IsDisposed);
        Assert.Null(buffer.Take());
    }

    [Fact]
    public async Task CancelledWorkerDoesNotLoadModelOrPublishResults()
    {
        using var buffer = new LatestFrameBuffer();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await DepthPreviewWorker.RunAsync(buffer, (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), cancel.Token);
    }
}

public sealed class DepthModelFactAttribute : FactAttribute
{
    public DepthModelFactAttribute()
    {
        if (!File.Exists(RelativeDepthPreview.DefaultModelPath))
            Skip = "Prepare the pinned MiDaS model to verify native inference and worker ownership.";
    }
}
