using OpenCvSharp;
using Toboo.Vision;
using Xunit;

namespace Toboo.Tests;

public sealed class LatestFrameBufferTests
{
    [Fact]
    public void NewestFrameReplacesAndDisposesTheOldPendingFrame()
    {
        using var buffer = new LatestFrameBuffer();
        var first = new Mat(8, 8, MatType.CV_8UC1);
        var second = new Mat(8, 8, MatType.CV_8UC1);
        buffer.Publish(new(1, TimeSpan.Zero, first));
        buffer.Publish(new(2, TimeSpan.Zero, second));
        Assert.True(first.IsDisposed);
        using var taken = buffer.Take();
        Assert.Equal(2, taken!.Number);
        Assert.False(second.IsDisposed);
        Assert.Null(buffer.Take());
    }

    [Fact]
    public void OwnershipOfTakenFrameBelongsToConsumer()
    {
        var buffer = new LatestFrameBuffer();
        var image = new Mat(8, 8, MatType.CV_8UC1);
        buffer.Publish(new(1, TimeSpan.Zero, image));
        using var taken = buffer.Take();
        buffer.Dispose();
        Assert.False(image.IsDisposed);
        taken!.Dispose();
        Assert.True(image.IsDisposed);
    }
}
