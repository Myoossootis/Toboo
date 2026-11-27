using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Toboo.Desktop.Controls;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class StartupWindowTests
{
    [AvaloniaFact]
    public async Task SplashRendersEachExpressionAndCanShowAStartupFailure()
    {
        var splash = new StartupWindow();
        try
        {
            splash.Show();
            var progress = splash.FindControl<ProgressBar>("LaunchProgress")!;
            var status = splash.FindControl<TextBlock>("LaunchStatus")!;
            Assert.True(progress.IsVisible && progress.IsIndeterminate);
            Assert.Equal("正在启动", status.Text);

            var mascot = splash.FindControl<StartupMascot>("Mascot")!;
            var folder = PreviewFolder();
            foreach (var (at, name) in new[] { (0, "power-on"), (395, "blink"), (790, "smile") })
            {
                mascot.ShowFrame(at);
                await Task.Delay(40);
                using var frame = splash.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(folder, $"{name}.png"));
            }

            splash.ShowFailure();
            Assert.False(progress.IsVisible);
            Assert.True(splash.FindControl<Button>("FailureCloseButton")!.IsVisible);
            Assert.Contains("启动失败", status.Text);
        }
        finally { splash.Close(); }
    }

    [AvaloniaFact]
    public async Task MainWindowOpensBeforeStartupWindowCloses()
    {
        var splash = new StartupWindow();
        var main = new Window { Content = new Border() };
        using var desktop = new ClassicDesktopStyleApplicationLifetime { MainWindow = splash };
        var splashWasVisible = false;
        main.Opened += (_, _) => splashWasVisible = splash.IsVisible;
        try
        {
            splash.Show();
            var startup = App.CompleteStartupAsync(desktop, splash, () => main);
            Assert.False(main.IsVisible);
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(splashWasVisible && main.IsVisible);
            Assert.False(splash.IsVisible);
            Assert.Same(main, desktop.MainWindow);
        }
        finally { main.Close(); splash.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingTheSplashDisposesThePreparedWindowWithoutOpeningIt()
    {
        var splash = new StartupWindow();
        var main = new Window();
        using var desktop = new ClassicDesktopStyleApplicationLifetime { MainWindow = splash };
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainWasOpened = false;
        var mainWasClosed = false;
        main.Opened += (_, _) => mainWasOpened = true;
        main.Closed += (_, _) => mainWasClosed = true;
        try
        {
            splash.Show();
            var startup = App.CompleteStartupAsync(desktop, splash, () =>
            {
                prepared.SetResult();
                return main;
            });
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(3));
            splash.Close();
            await startup.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(mainWasOpened);
            Assert.True(mainWasClosed);
            Assert.Same(splash, desktop.MainWindow);
        }
        finally { if (!mainWasClosed) main.Close(); splash.Close(); }
    }

    private static string PreviewFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Toboo.sln"))) continue;
            return Directory.CreateDirectory(Path.Combine(directory.FullName, "artifacts", "startup-preview")).FullName;
        }
        throw new DirectoryNotFoundException();
    }
}
