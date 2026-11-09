using System.Diagnostics;
using Toboo.Runtime;

namespace Toboo.Desktop.Services;

internal interface ILcdConnection : IDisposable
{
    Task SendAsync(string label, int lifetimeMilliseconds, CancellationToken cancellation);
}

/// <summary>One request at a time; no queued observations or retries of old labels.</summary>
internal sealed class LcdConnection : ILcdConnection
{
    private readonly Process process;
    private readonly Task<string> errors;
    private LcdConnection(Process process) { this.process = process; errors = process.StandardError.ReadToEndAsync(); }

    internal static async Task<ILcdConnection> OpenAsync(string port, CancellationToken cancellation)
    {
        var start = ServoTool.CreateStartInfo("lcd_expression.py", ["--port", port]);
        start.RedirectStandardInput = true;
        var connection = new LcdConnection(Process.Start(start) ?? throw new IOException("无法启动 LCD 串口连接。"));
        try
        {
            if (await connection.ReadAsync(TimeSpan.FromSeconds(8), cancellation) != "READY")
                throw new IOException("UNO LCD 握手不匹配，请检查端口和固件。");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public async Task SendAsync(string label, int lifetimeMilliseconds, CancellationToken cancellation)
    {
        if (!Enum.TryParse<Toboo.Core.FacialExpression>(label, true, out var expression)
            || expression.ToString().ToLowerInvariant() != label || lifetimeMilliseconds is < 1 or > 500)
            throw new ArgumentException("Invalid LCD cue.");
        await process.StandardInput.WriteLineAsync($"E,{label},{lifetimeMilliseconds}".AsMemory(), cancellation);
        await process.StandardInput.FlushAsync(cancellation);
        if (await ReadAsync(TimeSpan.FromSeconds(1), cancellation) != "OK," + label)
            throw new IOException("LCD 未确认本次显示请求。");
    }

    private async Task<string> ReadAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        var line = await process.StandardOutput.ReadLineAsync(cancellation).AsTask().WaitAsync(timeout, cancellation);
        if (line is not null) return line;
        var detail = await errors.WaitAsync(TimeSpan.FromSeconds(1), cancellation);
        throw new IOException(string.IsNullOrWhiteSpace(detail) ? "UNO LCD 连接已结束。" : detail.Trim());
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        process.Dispose(); // The firmware independently expires the last observation within 500ms.
    }
}
