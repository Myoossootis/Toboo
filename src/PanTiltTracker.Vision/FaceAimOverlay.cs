using OpenCvSharp;
using PanTiltTracker.Core;

namespace PanTiltTracker.Vision;

/// <summary>Guides use the same projection as the controller, including a non-centred optical axis.</summary>
public static class FaceAimOverlay
{
    public static void Draw(Mat image, FaceAimSettings settings, FaceAimResult? aim)
    {
        if (settings.Camera is not { } camera) return;
        try { camera.Project(0, 0, image.Width, image.Height); }
        catch (ArgumentException) { return; }
        var center = new Point((int)Math.Round(camera.Cx * image.Width / camera.Width),
            (int)Math.Round(camera.Cy * image.Height / camera.Height));
        Contour(settings.DeadZoneExitDegrees, new Scalar(40, 170, 240));
        Contour(settings.DeadZoneEnterDegrees, new Scalar(90, 205, 90));
        Cv2.DrawMarker(image, center, new Scalar(255, 255, 255), MarkerTypes.Cross, 24, 2);
        if (aim?.FilteredX is { } x && aim.FilteredY is { } y)
            Cv2.Line(image, center, new((int)Math.Round(x), (int)Math.Round(y)), new Scalar(0, 220, 255), 1, LineTypes.AntiAlias);
        Cv2.PutText(image, camera.Approximate ? "Approximate camera / angle guides" : "Pinhole intrinsics / angle guides",
            new(12, image.Height - 14), HersheyFonts.HersheySimplex, 0.45, new Scalar(235, 235, 235), 1, LineTypes.AntiAlias);

        void Contour(double degrees, Scalar color)
        {
            if (degrees <= 0 || degrees >= 85) return;
            var tangent = Math.Tan(degrees * Math.PI / 180);
            var points = new List<Point>();
            // Vertical sight angle depends on horizontal sight angle. Draw the true curved boundary.
            for (var side = 0; side < 4; side++)
            for (var i = 0; i <= 16; i++)
            {
                var t = -1.0 + i / 8.0;
                var nx = tangent * (side switch { 0 => t, 1 => 1, 2 => -t, _ => -1 });
                var ny = tangent * Math.Sqrt(1 + nx * nx) * (side switch { 0 => -1, 1 => t, 2 => 1, _ => -t });
                var px = (camera.Cx + camera.Fx * nx) * image.Width / camera.Width;
                var py = (camera.Cy + camera.Fy * ny) * image.Height / camera.Height;
                points.Add(new((int)Math.Round(Math.Clamp(px, 0, image.Width - 1)),
                    (int)Math.Round(Math.Clamp(py, 0, image.Height - 1))));
            }
            Cv2.Polylines(image, [points.ToArray()], true, color, 1, LineTypes.AntiAlias);
        }
    }
}
