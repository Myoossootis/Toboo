using OpenCvSharp;

namespace PanTiltTracker.Vision;

public sealed record CapturedFrame(long Number, TimeSpan CapturedAt, Mat Image) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

// Transfers ownership of a Mat. Replacing a pending frame disposes the dropped image.
public sealed class LatestFrameBuffer : IDisposable
{
    private readonly object gate = new();
    private CapturedFrame? pending;
    private bool disposed;

    public void Publish(CapturedFrame frame)
    {
        lock (gate)
        {
            if (disposed)
            {
                frame.Dispose();
                return;
            }
            pending?.Dispose();
            pending = frame;
        }
    }

    public CapturedFrame? Take()
    {
        lock (gate)
        {
            var frame = pending;
            pending = null;
            return frame;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending?.Dispose();
            pending = null;
        }
    }
}
