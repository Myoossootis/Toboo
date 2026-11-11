using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Toboo.Desktop.Services;

namespace Toboo.Desktop.Controls;

public sealed class LcdFacePreview : Control
{
    public static readonly StyledProperty<LcdRole> RoleProperty =
        AvaloniaProperty.Register<LcdFacePreview, LcdRole>(nameof(Role));

    private static readonly IBrush lit = Brush.Parse("#E0EEFF");
    private static readonly Pen stroke = new(lit, 4, lineCap: PenLineCap.Round);
    private static readonly Geometry smilingEye = Geometry.Parse("M-10,4 Q0,-12 10,4");
    private static readonly Geometry smile = Geometry.Parse("M-8,-2 Q0,7 8,-2");
    private static readonly Keyframe[][] clips = LoadClips();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private long roleStarted;
    private Pose pose;
    private readonly record struct Pose(double X, double Y, double Eye, double Mouth);
    private readonly record struct Keyframe(int Time, Pose Pose);

    public LcdRole Role { get => GetValue(RoleProperty); set => SetValue(RoleProperty, value); }

    public LcdFacePreview() => timer.Tick += (_, _) =>
    {
        if (!IsEffectivelyVisible) return;
        var next = PoseAt(Role, Stopwatch.GetElapsedTime(roleStarted).TotalMilliseconds);
        if (pose == next) return;
        pose = next;
        InvalidateVisual();
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Restart();
        timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RoleProperty) Restart();
    }

    private void Restart()
    {
        roleStarted = Stopwatch.GetTimestamp();
        pose = PoseAt(Role, 0);
        InvalidateVisual();
    }

    private static Keyframe[][] LoadClips()
    {
        using var stream = typeof(LcdFacePreview).Assembly.GetManifestResourceStream("Toboo.LcdMotion")!;
        using var reader = new StreamReader(stream);
        var result = new Keyframe[Enum.GetValues<LcdRole>().Length][];
        var frames = new List<Keyframe>();
        var role = LcdRole.Idle;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("CLIP(", StringComparison.Ordinal))
                role = Enum.Parse<LcdRole>(line[5..^1]);
            else if (line.StartsWith("POSE(", StringComparison.Ordinal))
            {
                var values = line[5..^1].Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                frames.Add(new(values[0], new(values[1], values[2], values[3], values[4])));
            }
            else if (line == "END_CLIP()")
            {
                result[(int)role] = frames.ToArray();
                frames.Clear();
            }
        }
        return result;
    }

    private static Pose PoseAt(LcdRole role, double elapsed)
    {
        var frames = clips[(int)role];
        var time = elapsed % frames[^1].Time;
        for (var i = 1; i < frames.Length; i++)
        {
            var to = frames[i];
            if (time > to.Time) continue;
            var from = frames[i - 1];
            var progress = (time - from.Time) / (to.Time - from.Time);
            var ease = progress * progress * (3 - 2 * progress);
            return new(Blend(from.Pose.X, to.Pose.X), Blend(from.Pose.Y, to.Pose.Y),
                Blend(from.Pose.Eye, to.Pose.Eye), Blend(from.Pose.Mouth, to.Pose.Mouth));

            double Blend(double start, double end) => start + (end - start) * ease;
        }
        return frames[^1].Pose;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = Bounds.Width / 2 + pose.X * 3;
        var y = Bounds.Height / 2 - 3 + pose.Y * 2;
        var eyeGap = Math.Min(40, Bounds.Width / 4);
        DrawEye(center - eyeGap);
        DrawEye(center + eyeGap);

        var mouthY = y + 13;
        if (Role == LcdRole.Happy)
        {
            if (pose.Mouth > 0.7)
                context.DrawEllipse(null, stroke, new Point(center, mouthY), 6, pose.Mouth * 2);
            else
            {
                using var position = context.PushTransform(Matrix.CreateTranslation(center, mouthY));
                context.DrawGeometry(null, stroke, smile);
            }
        }
        else if (pose.Mouth > 0.4)
            context.DrawEllipse(null, stroke, new Point(center, mouthY), 6, pose.Mouth * 2);
        else
            context.DrawLine(stroke, new Point(center - 4, mouthY), new Point(center + 4, mouthY));

        void DrawEye(double x)
        {
            if (Role == LcdRole.Happy)
            {
                using var position = context.PushTransform(Matrix.CreateTranslation(x, y));
                context.DrawGeometry(null, stroke, smilingEye);
                return;
            }
            var height = Math.Max(3, pose.Eye * 2);
            context.DrawRectangle(lit, null, new Rect(x - 10, y - height / 2.0, 20, height), 4, 4);
        }
    }
}
