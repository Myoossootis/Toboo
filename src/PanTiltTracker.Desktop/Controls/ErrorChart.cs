using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PanTiltTracker.Desktop.ViewModels;

namespace PanTiltTracker.Desktop.Controls;

public sealed class ErrorChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ErrorSample>> SamplesProperty =
        AvaloniaProperty.Register<ErrorChart, IReadOnlyList<ErrorSample>>(nameof(Samples), Array.Empty<ErrorSample>());
    private static readonly Pen grid = new(Brush.Parse("#E8EEEE"), 1);
    private static readonly Pen zero = new(Brush.Parse("#CCD9DA"), 1);
    private static readonly Pen xLine = new(Brush.Parse("#167D71"), 1.8);
    private static readonly Pen yLine = new(Brush.Parse("#BC7C37"), 1.8);

    static ErrorChart() => AffectsRender<ErrorChart>(SamplesProperty);

    public IReadOnlyList<ErrorSample> Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var area = new Rect(0, 2, Bounds.Width, Math.Max(0, Bounds.Height - 4));
        if (area.Width <= 0 || area.Height <= 0) return;
        using (context.PushClip(area))
        {
            for (var line = 0; line <= 4; line++)
            {
                var y = area.Y + line * area.Height / 4;
                context.DrawLine(line == 2 ? zero : grid, new(0, y), new(area.Right, y));
            }
            for (var line = 0; line <= 5; line++)
            {
                var x = line * area.Width / 5;
                context.DrawLine(grid, new(x, area.Top), new(x, area.Bottom));
            }
            for (var index = 1; index < Samples.Count; index++)
            {
                Draw(Samples[index - 1].X, Samples[index].X, xLine);
                // Different line patterns keep X/Y distinguishable without color (§5.7).
                if (index % 6 < 3) Draw(Samples[index - 1].Y, Samples[index].Y, yLine);

                void Draw(double? previous, double? current, Pen pen)
                {
                    if (!previous.HasValue || !current.HasValue) return;
                    var denominator = Math.Max(179, Samples.Count - 1);
                    var x0 = (index - 1) * area.Width / denominator;
                    var x1 = index * area.Width / denominator;
                    context.DrawLine(pen, new(x0, area.Center.Y - previous.Value * area.Height),
                        new(x1, area.Center.Y - current.Value * area.Height));
                }
            }
        }
    }
}
