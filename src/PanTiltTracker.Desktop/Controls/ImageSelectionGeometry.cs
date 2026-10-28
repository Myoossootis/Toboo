using Avalonia;

namespace PanTiltTracker.Desktop.Controls;

public static class ImageSelectionGeometry
{
    public static Rect ImageBounds(Size viewport, int imageWidth, int imageHeight)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || imageWidth <= 0 || imageHeight <= 0) return default;
        var scale = Math.Min(viewport.Width / imageWidth, viewport.Height / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        return new((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
    }

    public static OpenCvSharp.Rect? Region(Point start, Point end, Size viewport, int width, int height)
    {
        var image = ImageBounds(viewport, width, height);
        if (image.Width <= 0 || !image.Contains(start)) return null;
        var x1 = Math.Clamp((Math.Min(start.X, end.X) - image.X) / image.Width, 0, 1);
        var x2 = Math.Clamp((Math.Max(start.X, end.X) - image.X) / image.Width, 0, 1);
        var y1 = Math.Clamp((Math.Min(start.Y, end.Y) - image.Y) / image.Height, 0, 1);
        var y2 = Math.Clamp((Math.Max(start.Y, end.Y) - image.Y) / image.Height, 0, 1);
        var x = (int)Math.Floor(x1 * width);
        var y = (int)Math.Floor(y1 * height);
        var right = Math.Min(width, (int)Math.Ceiling(x2 * width));
        var bottom = Math.Min(height, (int)Math.Ceiling(y2 * height));
        return right - x >= 8 && bottom - y >= 8 ? new(x, y, right - x, bottom - y) : null;
    }
}
