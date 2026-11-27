using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Toboo.Desktop.Controls;

/// <summary>Shows the recent relative microphone level as a row of bars.</summary>
public sealed class InputWaveform : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<InputWaveform, double>(nameof(Level));
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<InputWaveform, bool>(nameof(IsActive));
    public static readonly StyledProperty<IBrush> PulseBrushProperty =
        AvaloniaProperty.Register<InputWaveform, IBrush>(nameof(PulseBrush), Brush.Parse("#126F65"));
    public static readonly StyledProperty<IBrush> TrackBrushProperty =
        AvaloniaProperty.Register<InputWaveform, IBrush>(nameof(TrackBrush), Brush.Parse("#AFD0C9"));

    private readonly double[] history = new double[64];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private bool attached;
    private int nextSample;
    private int nonzeroSamples;
    private double peakSinceSample;

    static InputWaveform() => AffectsRender<InputWaveform>(LevelProperty, PulseBrushProperty, TrackBrushProperty);

    public InputWaveform() => timer.Tick += (_, _) => Sample();

    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public IBrush PulseBrush { get => GetValue(PulseBrushProperty); set => SetValue(PulseBrushProperty, value); }
    public IBrush TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        RefreshSampling();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        RefreshSampling();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!attached) return;
        if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty)
            RefreshSampling();
        else if (change.Property == LevelProperty && IsActive && IsEffectivelyVisible)
            peakSinceSample = Math.Max(peakSinceSample, CurrentLevel());
    }

    private void RefreshSampling()
    {
        timer.Stop();
        ResetHistory();
        if (!attached || !IsActive || !IsVisible) return;
        Sample();
        timer.Start();
    }

    private void ResetHistory()
    {
        Array.Clear(history);
        nextSample = nonzeroSamples = 0;
        peakSinceSample = 0;
        InvalidateVisual();
    }

    private double CurrentLevel() => double.IsFinite(Level) ? Math.Clamp(Level / 100, 0, 1) : 0;

    private void Sample()
    {
        if (!IsEffectivelyVisible)
        {
            if (nonzeroSamples > 0 || peakSinceSample > 0) ResetHistory();
            return;
        }

        var value = Math.Max(peakSinceSample, CurrentLevel());
        peakSinceSample = 0;
        if (value == 0 && nonzeroSamples == 0) return;
        if (history[nextSample] > 0) nonzeroSamples--;
        history[nextSample] = value;
        if (value > 0) nonzeroSamples++;
        nextSample = (nextSample + 1) % history.Length;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var count = Math.Clamp((int)(Bounds.Width / 6), 1, history.Length);
        var spacing = Bounds.Width / count;
        var width = Math.Min(2, spacing * 0.5);
        var dotHeight = Math.Min(width, Bounds.Height);
        var maxHeight = Math.Max(dotHeight, Bounds.Height * 0.86);
        for (var i = 0; i < count; i++)
        {
            var value = history[(nextSample + history.Length - count + i) % history.Length];
            if (i == count - 1 && IsActive)
                value = Math.Max(value, Math.Max(peakSinceSample, CurrentLevel()));
            var height = dotHeight + (maxHeight - dotHeight) * value;
            var rect = new Rect(spacing * (i + 0.5) - width / 2, (Bounds.Height - height) / 2, width, height);
            context.DrawRectangle(value > 0 ? PulseBrush : TrackBrush, null, rect, width / 2, width / 2);
        }
    }
}
