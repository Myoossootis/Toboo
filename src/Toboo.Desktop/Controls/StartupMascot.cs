using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Toboo.Desktop.Controls;

public sealed class StartupMascot : Control
{
    private static readonly Uri MascotSource = new("avares://toboo/Assets/toboo-companion.png");
    private static readonly Rect Crop = new(205, 180, 860, 860);
    private static readonly Rect LeftVisorPatch = new(494, 436, 268, 205);
    private static readonly Rect RightVisorPatch = new(748, 398, 210, 194);
    private static readonly IBrush LeftVisor = FeatheredPatch(Color.Parse("#1C2524"));
    private static readonly IBrush RightVisor = FeatheredPatch(Color.Parse("#293333"));
    private static readonly Pen SoftGlow = new(Brush.Parse("#2150FFE2"), 41, lineCap: PenLineCap.Round);
    private static readonly Pen InnerGlow = new(Brush.Parse("#4050FFE2"), 29, lineCap: PenLineCap.Round);
    private static readonly Pen EyeStroke = new(Brush.Parse("#69F9E3"), 19, lineCap: PenLineCap.Round);

    private readonly record struct Keyframe(double Milliseconds, double Width, double Opening, double Brightness, double Original);
    private static readonly Keyframe[] Keyframes =
    [
        new(0, 0.06, 0, 0.46, 0),
        new(420, 0.18, 0.05, 0.78, 0),
        new(1050, 0.82, 0.72, 0.98, 0),
        new(1650, 0.88, 0.75, 1, 0),
        new(1770, 0.72, 0.02, 0.89, 0),
        new(1940, 0.91, 0.78, 1, 0),
        new(2920, 0.94, 0.83, 1, 0),
        new(3040, 0.76, 0.02, 0.89, 0),
        new(3240, 1, 1, 1, 0),
        new(4280, 1, 1, 1, 0.65),
        new(5000, 1, 1, 1, 1)
    ];
    internal static double AnimationEnd => Keyframes[^1].Milliseconds;
    private Bitmap? mascot;
    private double time;

    internal void ShowFrame(double milliseconds)
    {
        time = Math.Clamp(milliseconds, 0, AnimationEnd);
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        using var source = AssetLoader.Open(MascotSource);
        mascot = new Bitmap(source);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        mascot?.Dispose();
        mascot = null;
        base.OnDetachedFromVisualTree(args);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (mascot is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var scale = Math.Min(Bounds.Width / Crop.Width, Bounds.Height / Crop.Height);
        var offset = new Point((Bounds.Width - Crop.Width * scale) / 2, (Bounds.Height - Crop.Height * scale) / 2);
        using var clip = context.PushClip(new Rect(Bounds.Size));
        using var artwork = context.PushTransform(Matrix.CreateTranslation(-Crop.X, -Crop.Y)
            * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y));
        context.DrawImage(mascot, new Rect(new Size(mascot.PixelSize.Width, mascot.PixelSize.Height)));
        var pose = FrameAt(time);
        if (pose.Original >= 1) return;
        using var blend = context.PushOpacity(1 - pose.Original);
        context.DrawEllipse(LeftVisor, null, LeftVisorPatch);
        context.DrawEllipse(RightVisor, null, RightVisorPatch);
        using var brightness = context.PushOpacity(pose.Brightness);
        DrawEye(context, new Point(630, 556), 60, 48, pose);
        DrawEye(context, new Point(856, 514), 49, 41, pose);
    }

    private static Keyframe FrameAt(double milliseconds)
    {
        for (var index = 1; index < Keyframes.Length; index++)
        {
            var to = Keyframes[index];
            if (milliseconds > to.Milliseconds) continue;
            var from = Keyframes[index - 1];
            var fraction = (milliseconds - from.Milliseconds) / (to.Milliseconds - from.Milliseconds);
            var ease = fraction * fraction * (3 - 2 * fraction);
            double Blend(double start, double end) => start + (end - start) * ease;
            return new(milliseconds, Blend(from.Width, to.Width), Blend(from.Opening, to.Opening),
                Blend(from.Brightness, to.Brightness), Blend(from.Original, to.Original));
        }
        return Keyframes[^1];
    }

    private static void DrawEye(DrawingContext context, Point center, double width, double height, Keyframe pose)
    {
        var halfWidth = width * pose.Width;
        var slope = halfWidth * 0.16;
        var curve = new StreamGeometry();
        using (var path = curve.Open())
        {
            path.BeginFigure(new Point(center.X - halfWidth, center.Y + slope), false);
            path.QuadraticBezierTo(new Point(center.X, center.Y - 2 * height * pose.Opening),
                new Point(center.X + halfWidth, center.Y - slope));
            path.EndFigure(false);
        }
        context.DrawGeometry(null, SoftGlow, curve);
        context.DrawGeometry(null, InnerGlow, curve);
        context.DrawGeometry(null, EyeStroke, curve);
    }

    private static RadialGradientBrush FeatheredPatch(Color color) => new()
    {
        RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
        GradientStops = [new(color, 0), new(color, 0.74), new(Color.FromArgb(0, color.R, color.G, color.B), 1)]
    };
}
