using System.Diagnostics;

namespace Toboo.Runtime;

internal static class ServoTool
{
    internal sealed record Result(int ExitCode, string Output, string Error);

    internal static Task<Result> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken = default,
        Func<string, (string Url, string Token)>? serialProxy = null) =>
        RunScriptAsync("single_servo.py", args, cancellationToken, serialProxy);

    internal static async Task<Result> RunScriptAsync(string script, IEnumerable<string> args, CancellationToken cancellationToken = default,
        Func<string, (string Url, string Token)>? serialProxy = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = CreateStartInfo(script, args);
        ApplySerialProxy(start, script, serialProxy, cancellationToken);
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
        return CreateStartInfo(AppContext.BaseDirectory, script, args);
    }

    internal static ProcessStartInfo CreateStartInfo(string baseDirectory, string script, IEnumerable<string> args)
    {
        if (script is not ("single_servo.py" or "face_follow.py" or "sensor_telemetry.py"))
            throw new ArgumentException("未知串口工具。", nameof(script));
        var root = FindRuntimeRoot(baseDirectory);
        var portable = File.Exists(Path.Combine(root, "portable.mode"));
        var python = portable ? Path.Combine(root, "runtime", "python", "python.exe")
            : Path.Combine(root, "tmp", "esp32-tools", "host", "Scripts", "python.exe");
        if (!File.Exists(python))
            throw new InvalidOperationException(portable
                ? "便携包的串口运行环境不完整，请完整解压最新版，保留 runtime 文件夹。"
                : "请先按 firmware/README.md 安装源码串口环境，或使用完整便携版。");
        var scriptPath = Path.Combine(root, "firmware", "tools", script);
        if (!File.Exists(scriptPath)) throw new InvalidOperationException("串口脚本缺失，请完整解压程序文件夹。");
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment.Remove("PYTHONHOME");
        start.Environment.Remove("PYTHONPATH");
        start.Environment.Remove("TOBOO_SERIAL_URL");
        start.Environment.Remove("TOBOO_SERIAL_TOKEN");
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("utf8");
        if (!portable)
        {
            // -I excludes the script directory; add only this checkout's owned helper folder.
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import runpy,sys;sys.path.insert(0,sys.argv.pop(1));sys.argv=sys.argv[1:];runpy.run_path(sys.argv[0],run_name='__main__')");
            start.ArgumentList.Add(Path.GetDirectoryName(scriptPath)!);
        }
        start.ArgumentList.Add(scriptPath);
        var arguments = args.ToArray();
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    // Explicit execution boundary: parameter inspection never resolves a physical port.
    internal static void ApplySerialProxy(ProcessStartInfo start, string script,
        Func<string, (string Url, string Token)>? connect, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connect is null || script is not ("single_servo.py" or "face_follow.py")) return;
        var arguments = start.ArgumentList.ToArray();
        var portIndex = Array.IndexOf(arguments, "--port");
        if (portIndex < 0 || portIndex + 1 >= arguments.Length) return;
        var endpoint = connect(arguments[portIndex + 1]);
        cancellationToken.ThrowIfCancellationRequested();
        start.Environment["TOBOO_SERIAL_URL"] = endpoint.Url;
        start.Environment["TOBOO_SERIAL_TOKEN"] = endpoint.Token;
    }

    private static string FindRuntimeRoot(string baseDirectory)
    {
        var checkout = new DirectoryInfo(baseDirectory);
        // A portable marker is a hard boundary: an incomplete package cannot silently use a developer checkout.
        if (File.Exists(Path.Combine(checkout.FullName, "portable.mode"))) return checkout.FullName;
        while (checkout is not null && !File.Exists(Path.Combine(checkout.FullName, "Toboo.sln")))
            checkout = checkout.Parent;
        return checkout?.FullName ?? throw new InvalidOperationException("未找到完整程序包或源码目录，请完整解压最新版。");
    }
}
