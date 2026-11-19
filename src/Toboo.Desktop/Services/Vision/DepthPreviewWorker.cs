using Toboo.Vision;

namespace Toboo.Desktop.Services;

/// <summary>One in-flight inference and one pending frame, with explicit native-frame ownership.</summary>
internal static class DepthPreviewWorker
{
    internal static async Task RunAsync(LatestFrameBuffer buffer,
        Func<DepthPreviewFrame, TimeSpan, Task> present, Action<string> failed, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var model = new RelativeDepthPreview();
            while (!cancellation.IsCancellationRequested)
            {
                using var frame = buffer.Take();
                if (frame is null) { await Task.Delay(30, cancellation); continue; }
                var result = model.Infer(frame.Image);
                cancellation.ThrowIfCancellationRequested();
                await present(result, frame.CapturedAt);
                await Task.Delay(100, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or OpenCvSharp.OpenCVException or ArgumentException)
        { if (!cancellation.IsCancellationRequested) failed(error.Message); }
    }
}
