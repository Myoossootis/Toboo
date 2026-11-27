using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Toboo.Desktop.Controls;

namespace Toboo.Desktop;

public sealed partial class StartupWindow : Window
{
    internal static readonly TimeSpan IntroDuration = TimeSpan.FromMilliseconds(StartupMascot.AnimationEnd);
    private readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private long started;

    public StartupWindow()
    {
        InitializeComponent();
        animation.Tick += AdvanceAnimation;
    }

    internal void Play()
    {
        started = Stopwatch.GetTimestamp();
        Mascot.ShowFrame(0);
        LaunchProgress.Value = 0;
        animation.Start();
    }

    internal void ShowFailure()
    {
        animation.Stop();
        LaunchStatus.Text = "启动失败，请查看本机诊断。";
        LaunchProgress.IsVisible = false;
        FailureCloseButton.IsVisible = true;
    }

    private void AdvanceAnimation(object? sender, EventArgs args)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Mascot.ShowFrame(elapsed);
        LaunchProgress.Value = Math.Min(100, elapsed / IntroDuration.TotalMilliseconds * 100);
        if (elapsed >= StartupMascot.AnimationEnd) animation.Stop();
    }

    protected override void OnClosed(EventArgs args)
    {
        animation.Stop();
        base.OnClosed(args);
    }

    private void DragWindow(object? sender, PointerPressedEventArgs args)
    {
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
    }

    private void CloseWindow(object? sender, RoutedEventArgs args) => Close();
}
