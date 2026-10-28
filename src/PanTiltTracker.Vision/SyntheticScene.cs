using OpenCvSharp;

namespace PanTiltTracker.Vision;

// Ideal pinhole simulation only: no measured lens, servo dynamics, backlash or USB delay.
public sealed class SyntheticScene : IDisposable
{
    public const int Width = 640;
    public const int Height = 480;
    private readonly Mat marker;

    public SyntheticScene(int targetId = 7)
    {
        using var printable = ArucoTargetDetector.CreateMarker(targetId, 120);
        marker = new Mat();
        Cv2.Resize(printable, marker, new Size(112, 112), 0, 0, InterpolationFlags.Nearest);
    }

    public Mat Render(double seconds, double panCommand, double tiltCommand, bool hidden = false)
    {
        var targetYaw = 12 * Math.Sin(seconds * 0.6) + 6;
        var targetPitch = 6 * Math.Cos(seconds * 0.4);
        const double focalPixels = 450; // A simulation parameter, not a specification of the purchased camera.
        var x = Width / 2.0 + focalPixels * Math.Tan((targetYaw - (panCommand - 90)) * Math.PI / 180);
        var y = Height / 2.0 + focalPixels * Math.Tan((targetPitch - (tiltCommand - 90)) * Math.PI / 180);
        return RenderAt(x, y, hidden);
    }

    public Mat RenderAt(double centerX, double centerY, bool hidden = false)
    {
        var image = new Mat(Height, Width, MatType.CV_8UC1, Scalar.White);
        var left = (int)Math.Round(centerX - marker.Width / 2.0);
        var top = (int)Math.Round(centerY - marker.Height / 2.0);
        if (!hidden && left >= 0 && top >= 0 &&
            left + marker.Width <= Width && top + marker.Height <= Height)
        {
            using var region = new Mat(image, new Rect(left, top, marker.Width, marker.Height));
            marker.CopyTo(region);
        }
        return image;
    }

    public void Dispose() => marker.Dispose();
}
