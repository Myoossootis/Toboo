using Avalonia;
using Toboo.Desktop.Services;

namespace Toboo.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test", StringComparer.Ordinal)) { Diagnostics.SelfTest(); return 0; }
            if (args.Contains("--chat-self-test", StringComparer.Ordinal)) { Diagnostics.ChatSelfTestAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--lcd-self-test", StringComparer.Ordinal))
            {
                var port = Array.IndexOf(args, "--lcd-port");
                if (port < 0 || port + 1 >= args.Length || string.IsNullOrWhiteSpace(args[port + 1]))
                    throw new ArgumentException("--lcd-self-test requires --lcd-port with the explicitly confirmed UNO port.");
                Diagnostics.LcdSelfTestAsync(args[port + 1]).GetAwaiter().GetResult(); return 0;
            }
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
