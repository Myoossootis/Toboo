using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Toboo.Desktop.ViewModels;
using Toboo.Desktop.Views;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class CommercialShellTests
{
    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(980, 700)]
    public async Task ThreePagesRenderRealIdleStatesAndKeepMotionAndUnimplementedFeaturesDisabled(int width, int height)
    {
        using var model = new MainViewModel(Output());
        var window = new MainWindow(model) { Width = width, Height = height };
        try
        {
            window.Show(); await Task.Delay(80);
            var home = window.FindControl<CompanionHomeView>("HomeView")!;
            Assert.False(home.FindControl<TextBox>("ConversationInput")!.IsEffectivelyEnabled);
            Assert.Contains("待确认", window.Shell.RobotConnection);
            Assert.False(model.IsRunning); Assert.False(model.Servo.Ready);
            Capture("companion");
            window.Shell.NavigateCommand.Execute("1"); await Task.Delay(50);
            var devices = window.FindControl<DevicesView>("DevicesView")!;
            Assert.True(devices.IsEffectivelyVisible);
            Assert.False(devices.FindControl<Button>("DeviceHomeButton")!.IsEffectivelyEnabled);
            Assert.False(devices.FindControl<Button>("DeviceFollowButton")!.IsEffectivelyEnabled);
            Assert.False(devices.FindControl<Button>("StartVisionButton")!.IsEffectivelyEnabled);
            Assert.Contains("未开启", window.Shell.CameraState);
            Capture("devices");
            window.Shell.NavigateCommand.Execute("2"); await Task.Delay(50);
            var settings = window.FindControl<PreferencesView>("PreferencesView")!;
            Assert.True(settings.IsEffectivelyVisible);
            settings.FindControl<ToggleSwitch>("SettingsExpressionToggle")!.IsChecked = false;
            Assert.False(model.ExpressionEnabled);
            settings.FindControl<ToggleSwitch>("ShowVisualToggle")!.IsChecked = false;
            Assert.False(window.Shell.ShowVisualPanel);
            Capture("settings");
            window.Shell.ShowPrivacyCommand.Execute(null); await Task.Delay(40);
            Assert.Equal(2, settings.FindControl<TabControl>("PreferencesTabs")!.SelectedIndex);
            Capture("privacy");
            Assert.False(model.IsRunning); Assert.False(model.Servo.Ready);
            void Capture(string page)
            {
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                bitmap.Save(Path.Combine(Output(), $"{page}-{width}.png"));
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NavigationAndClosingSharedDiagnosticsKeepOneAcquisitionAndShellCloseStopsIt()
    {
        using var model = new MainViewModel(Output());
        var window = new MainWindow(model);
        window.Show();
        var running = model.StartAsync(); // Synthetic source; no physical camera or transport.
        try
        {
            await Until(() => model.ProcessedFrames >= 3);
            foreach (var page in new[] { "1", "2", "0" })
            {
                var before = model.ProcessedFrames;
                window.Shell.NavigateCommand.Execute(page);
                await Until(() => model.ProcessedFrames > before);
                Assert.True(model.IsRunning); Assert.False(model.IsFollowing);
                Assert.True(window.FindControl<Button>("GlobalStopButton")!.IsEffectivelyVisible);
                Assert.Contains("合成场景", window.Shell.SourceCaption);
            }
            window.ShowAdvanced(2); await Task.Delay(40);
            var advanced = window.OwnedWindows.OfType<AdvancedWindow>().Single();
            Assert.Same(model, advanced.ViewModel);
            Assert.Equal(2, advanced.FindControl<TabControl>("SettingsTabs")!.SelectedIndex);
            advanced.Close();
            var count = model.ProcessedFrames;
            await Until(() => model.ProcessedFrames > count);
            Assert.True(model.IsRunning);
            window.Close();
            await Until(() => !window.IsVisible && !model.IsRunning);
            await running;
        }
        finally { await model.StopAsync(); await running; window.Close(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static string Output()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Toboo.sln")))
            {
                var path = Path.Combine(directory.FullName, "artifacts", "commercial-shell");
                Directory.CreateDirectory(path); return path;
            }
        throw new DirectoryNotFoundException();
    }
}
