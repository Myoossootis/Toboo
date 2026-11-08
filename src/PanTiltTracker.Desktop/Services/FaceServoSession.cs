using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PanTiltTracker.Core;
using PanTiltTracker.Shared;

namespace PanTiltTracker.Desktop.Services;

/// <summary>One exclusive interactive transport. No camera images cross this process boundary.</summary>
internal sealed class FaceServoSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly int[] bounds;
    private readonly string axes;
    private bool started;
    private bool disposed;
    public string? LimitNotice { get; private set; }
    public AimReference Reference { get; private set; } = new(-1, -1);
    public int[] Bounds => [.. bounds];
    private FaceServoSession(Process process, int[] bounds, string axes)
    { this.process = process; this.bounds = bounds; this.axes = axes; errors = process.StandardError.ReadToEndAsync(); }

    public static async Task<FaceServoSession> OpenAsync(string port, int[] bounds, string axes, CancellationToken token)
    {
        if (!Regex.IsMatch(port, @"\ACOM[1-9][0-9]{0,3}\z")) throw new ArgumentException("请填写有效串口号。");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanTiltTracker", "servo-logs");
        Directory.CreateDirectory(folder);
        var log = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fffffff}-face-follow.jsonl");
        var start = ServoTool.CreateStartInfo("face_follow.py", ["--port", port, "--log", log, "--execute"]);
        start.RedirectStandardInput = true;
        var session = new FaceServoSession(Process.Start(start) ?? throw new IOException("无法启动跟随串口工具。"), bounds, axes);
        try
        {
            var ready = await session.ReadAsync("ready", token, 10);
            if (ready.GetProperty("firmware").GetString() is not ("0.5.0" or "0.5.1" or "0.5.2" or "0.5.3")) throw new IOException("人脸跟随需要固件 v0.5.0。");
            session.Reference = ReadReference(ready);
            if (session.Reference.PanDegrees < bounds[0] || session.Reference.PanDegrees > bounds[1] ||
                session.Reference.TiltDegrees < bounds[2] || session.Reference.TiltDegrees > bounds[3])
                throw new IOException("请先在舵机页回位，建立位于当前边界内的两轴请求参考。");
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    public void RestrictTrialRange(int halfRange)
    {
        if (started) throw new InvalidOperationException("跟随已启动，不能修改本轮边界。");
        CalculateTrialBounds(bounds, Reference, halfRange).CopyTo(bounds, 0);
    }

    internal static int[] CalculateTrialBounds(int[] limits, AimReference reference, int halfRange)
    {
        if (halfRange is < 2 or > 10 || limits.Length != 4 ||
            reference.PanDegrees < limits[0] || reference.PanDegrees > limits[1] ||
            reference.TiltDegrees < limits[2] || reference.TiltDegrees > limits[3] ||
            !double.IsFinite(reference.PanDegrees) || !double.IsFinite(reference.TiltDegrees))
            throw new ArgumentException("试跟随半范围应为 2–10°，参考须位于边界内。");
        return [Math.Max(limits[0], (int)reference.PanDegrees - halfRange),
            Math.Min(limits[1], (int)reference.PanDegrees + halfRange),
            Math.Max(limits[2], (int)reference.TiltDegrees - halfRange),
            Math.Min(limits[3], (int)reference.TiltDegrees + halfRange)];
    }

    public async Task<AimReference?> UpdateAsync(FaceAimResult aim, TimeSpan age, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        if (!started)
        {
            await WriteAsync(new { type = "start", bounds, axes,
                reference = new[] { (int)Reference.PanDegrees, (int)Reference.TiltDegrees } }, token);
            await ReadAsync("state", token);
            started = true;
        }
        await WriteAsync(new { type = "frame", permit = aim.RequestPermitted,
            age_ms = age.TotalMilliseconds + watch.Elapsed.TotalMilliseconds,
            pan = aim.Command.PanDegrees, tilt = aim.Command.TiltDegrees }, token);
        var state = await ReadAsync("state", token);
        var reference = ReadReference(state);
        if (reference.PanDegrees < bounds[0] || reference.PanDegrees > bounds[1] ||
            reference.TiltDegrees < bounds[2] || reference.TiltDegrees > bounds[3])
            throw new IOException("舵机返回的请求参考超出本轮边界。");
        Reference = reference;
        LimitNotice = state.TryGetProperty("limited", out var limited) && limited.GetArrayLength() > 0
            ? "目标超出工作边界，已将请求保持在边界内；画面与跟随继续。" : null;
        return state.GetProperty("held").GetBoolean() || LimitNotice is not null ? Reference : null;
    }

    private async Task WriteAsync(object value, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }
    private async Task<JsonElement> ReadAsync(string expected, CancellationToken token, int seconds = 2)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        string? line;
        try { line = await process.StandardOutput.ReadLineAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("跟随串口响应超时，运动目标已停止更新。"); }
        if (line is null)
            throw new IOException("跟随串口工具已停止：" + (await errors.WaitAsync(TimeSpan.FromSeconds(1))).Trim());
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.GetProperty("type").GetString() != expected) throw new IOException("跟随串口返回了意外状态。");
        return document.RootElement.Clone();
    }
    private static AimReference ReadReference(JsonElement root) =>
        new(root.GetProperty("reference")[0].GetInt32(), root.GetProperty("reference")[1].GetInt32());

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                await WriteAsync(new { type = "stop" }, timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        finally
        {
            await process.WaitForExitAsync();
            await errors;
            process.Dispose();
            // Firmware watchdog remains independent if process termination interrupted DISARM.
            await Task.Delay(550);
        }
    }
}
