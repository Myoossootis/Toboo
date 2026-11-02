using Avalonia;
using PanTiltTracker.Desktop.Services;

namespace PanTiltTracker.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test", StringComparer.Ordinal)) { Diagnostics.SelfTest(); return 0; }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error)
        {
            try { Diagnostics.Write("startup-error.json", new { success = false, error = error.ToString() }); }
            catch (Exception) { /* Preserve the original failure exit code even if the output directory is unwritable. */ }
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
