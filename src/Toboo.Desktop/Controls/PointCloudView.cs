using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Toboo.Vision;

namespace Toboo.Desktop.Controls;

public sealed class PointCloudView : Control
{
    private static readonly IBrush Background = Brush.Parse("#102C32");
    private static readonly Pen Grid = new(Brush.Parse("#24464A"), 1);
    private static readonly Pen Camera = new(Brush.Parse("#27C6B3"), 1.7);
    private static readonly IBrush[] Colors = Enumerable.Range(0, 512).Select(i =>
        (IBrush)new SolidColorBrush(Color.FromRgb((byte)((i >> 6) * 255 / 7),
            (byte)(((i >> 3) & 7) * 255 / 7), (byte)((i & 7) * 255 / 7)))).ToArray();
    private DepthPoint[] points = [];
    private Point? dragging;
    private double yaw = -0.20, pitch = -0.12, zoom = 1;

    public int PointCount => points.Length;

    internal void Show(DepthPoint[] value)
    {
        points = value;
        InvalidateVisual();
    }

    internal void ResetView()
    {
        yaw = -0.20; pitch = -0.12; zoom = 1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        dragging = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging is not { } previous) return;
        var next = e.GetPosition(this);
        yaw += (next.X - previous.X) * 0.008;
        pitch = Math.Clamp(pitch + (next.Y - previous.Y) * 0.008, -1.3, 1.3);
        dragging = next;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragging = null; e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragging = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        zoom = Math.Clamp(zoom * Math.Exp(e.Delta.Y * 0.12), 0.3, 4);
        InvalidateVisual(); e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);
        if (bounds.Width < 10 || bounds.Height < 10) return;
        using var clip = context.PushClip(bounds);
        var scale = Math.Min(bounds.Width, bounds.Height) * 0.40 * zoom;
        // The projection is a visualization camera around the relative cloud, in arbitrary units.
        (Point Screen, double Depth) Project(double x, double y, double z)
        {
            z -= 1.5;
            var side = x * Math.Cos(yaw) + z * Math.Sin(yaw);
            var distance = z * Math.Cos(yaw) - x * Math.Sin(yaw);
            var vertical = y * Math.Cos(pitch) - distance * Math.Sin(pitch);
            var depth = distance * Math.Cos(pitch) + y * Math.Sin(pitch);
            var perspective = 4 / Math.Max(1.0, 4 + depth);
            return (new(bounds.Width / 2 + side * scale * perspective,
                bounds.Height / 2 + vertical * scale * perspective), depth);
        }
        for (var i = -6; i <= 6; i++)
        {
            context.DrawLine(Grid, Project(i * .5, 1, 0).Screen, Project(i * .5, 1, 4).Screen);
            context.DrawLine(Grid, Project(-3, 1, (i + 6) / 3.0).Screen, Project(3, 1, (i + 6) / 3.0).Screen);
        }
        var projected = points.Select(p =>
        {
            var transformed = Project(p.X, p.Y, p.Z);
            var index = (int)(((p.Rgb >> 21) & 7) << 6 | ((p.Rgb >> 13) & 7) << 3 | ((p.Rgb >> 5) & 7));
            return (transformed.Screen, transformed.Depth, Color: index);
        }).OrderByDescending(p => p.Depth);
        foreach (var p in projected)
        {
            if (!bounds.Contains(p.Screen)) continue;
            context.FillRectangle(Colors[p.Color], new Rect(p.Screen.X, p.Screen.Y, 2, 2));
        }
        var origin = Project(0, 0, 0).Screen;
        var corners = new[] { Project(-.20, -.14, .30).Screen, Project(.20, -.14, .30).Screen,
            Project(.20, .14, .30).Screen, Project(-.20, .14, .30).Screen };
        for (var i = 0; i < 4; i++)
        {
            context.DrawLine(Camera, origin, corners[i]);
            context.DrawLine(Camera, corners[i], corners[(i + 1) % 4]);
        }
    }
}
