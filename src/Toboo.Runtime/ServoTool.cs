using System.Diagnostics;

namespace Toboo.Runtime;

internal static class ServoTool
{
    internal sealed record Result(int ExitCode, string Output, string Error);

    internal static async Task<Result> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken = default)
    {
        var start = CreateStartInfo("single_servo.py", args);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("无法启动舵机串口工具。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* The process already exited. */ }
            await process.WaitForExitAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("串口工具超时，操作已结束。请检查舵机和连接后重新读取状态。");
        }
        return new(process.ExitCode, await output, await error);
    }

    internal static ProcessStartInfo CreateStartInfo(string script, IEnumerable<string> args)
    {
        var checkout = new DirectoryInfo(AppContext.BaseDirectory);
        while (checkout is not null && !File.Exists(Path.Combine(checkout.FullName, "firmware", "tools", "single_servo.py")))
            checkout = checkout.Parent;
        if (checkout is null) throw new InvalidOperationException("舵机工具需要从 toboo 源码目录运行。");
        var python = Path.Combine(checkout.FullName, "tmp", "esp32-tools", "host", "Scripts", "python.exe");
        if (!File.Exists(python)) throw new InvalidOperationException("请先按 firmware/README.md 安装舵机串口环境。");

        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.ArgumentList.Add(Path.Combine(checkout.FullName, "firmware", "tools", script));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }
}
