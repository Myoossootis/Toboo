using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Toboo.Desktop;

[assembly: AvaloniaTestApplication(typeof(Toboo.Desktop.Tests.TestAppBuilder))]

namespace Toboo.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
