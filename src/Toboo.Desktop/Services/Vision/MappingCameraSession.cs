using System.Diagnostics;
using OpenCvSharp;
using Toboo.Core;
using Toboo.Vision;

namespace Toboo.Desktop.Services;

internal sealed record MappingFrame(TimeSpan At, byte[] Jpeg, int Width, int Height, MapPose? Pose,
    bool ChessboardRequested, Point2f[]? ChessboardCorners, bool TagVisible);

internal static class MappingCameraSession
{
    public static async Task RunAsync(int device, CameraSettings settings, long origin,
        Func<MappingIntrinsics?> calibration, double tagMeters, Func<bool> takeChessboard,
        Func<MappingFrame, Task> present, CancellationToken cancellation)
    {
        settings.Validate();
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var buffer = new LatestFrameBuffer();
        var producer = Task.Run(() =>
        {
            using var camera = new VideoCapture(device, settings.Backend);
            if (!camera.IsOpened()) throw new IOException("无法打开摄像头。请检查连接、权限和其他占用相机的程序。");
            camera.Set(VideoCaptureProperties.FrameWidth, settings.Width);
            camera.Set(VideoCaptureProperties.FrameHeight, settings.Height);
            camera.Set(VideoCaptureProperties.Fps, settings.Fps);
            long number = 0;
            while (!closing.IsCancellationRequested)
            {
                Mat? image = new();
                try
                {
                    if (!camera.Read(image) || image.Empty()) throw new IOException("摄像头没有返回图像。");
                    buffer.Publish(new(number++, Stopwatch.GetElapsedTime(origin), image));
                    image = null;
                }
                finally { image?.Dispose(); }
            }
        }, CancellationToken.None);
        var lastFrame = Stopwatch.GetElapsedTime(origin);
        try
        {
            using var mapper = new MarkerMappingVision();
            using var gray = new Mat();
            while (!closing.IsCancellationRequested)
            {
                if (producer.IsFaulted) await producer;
                using var frame = buffer.Take();
                if (frame is null)
                {
                    if (Stopwatch.GetElapsedTime(origin) - lastFrame > TimeSpan.FromSeconds(5)) throw new IOException("摄像头画面超时。");
                    await Task.Delay(10, closing.Token);
                    continue;
                }
                lastFrame = frame.CapturedAt;
                if (Stopwatch.GetElapsedTime(origin) - frame.CapturedAt > ObstacleMap.PairWindow) continue;
                var image = frame.Image;
                switch (image.Channels())
                {
                    case 1: image.CopyTo(gray); break;
                    case 3: Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY); break;
                    case 4: Cv2.CvtColor(image, gray, ColorConversionCodes.BGRA2GRAY); break;
                    default: throw new IOException("相机返回了无法处理的图像格式。");
                }
                var take = takeChessboard();
                var chessboard = take ? MarkerMappingVision.FindChessboard(gray) : null;
                var pose = mapper.Locate(gray, calibration(), tagMeters, out var corners);
                if (corners is not null)
                    Cv2.Polylines(image, new[] { corners.Select(p => new Point((int)p.X, (int)p.Y)) }, true,
                        pose.HasValue ? new Scalar(99, 217, 107) : new Scalar(0, 181, 249), 2, LineTypes.AntiAlias);
                if (chessboard is not null)
                    Cv2.DrawChessboardCorners(image, new Size(9, 6), chessboard, true);
                Cv2.ImEncode(".jpg", image, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 78));
                if (jpeg.Length == 0) throw new IOException("无法生成相机预览帧。");
                await present(new(frame.CapturedAt, jpeg, image.Width, image.Height, pose, take, chessboard, corners is not null));
                await Task.Delay(35, closing.Token);
            }
        }
        finally
        {
            closing.Cancel();
            await producer;
        }
    }
}
