using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PanTiltTracker.Desktop;

[assembly: AvaloniaTestApplication(typeof(PanTiltTracker.Desktop.Tests.TestAppBuilder))]

namespace PanTiltTracker.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
