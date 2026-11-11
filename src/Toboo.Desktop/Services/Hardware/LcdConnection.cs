using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace Toboo.Desktop.Services;

public enum LcdRole { Idle, Attentive, Thinking, Responding, Happy }

internal interface ILcdConnection : IDisposable
{
    bool SupportsRoles { get; }
    Task SendAsync(string label, int lifetimeMilliseconds, CancellationToken cancellation);
    Task<bool> SendRoleAsync(LcdRole role, int lifetimeMilliseconds, CancellationToken cancellation);
}

// Byte reads have a short bounded timeout, so cancellation never relies on SerialPort async support.
internal interface ILcdSerialChannel : IDisposable
{
    void Open();
    void DiscardInput();
    void WriteLine(string text);
    int ReadByte();
}

internal sealed class LcdConnection : ILcdConnection
{
    private readonly ILcdSerialChannel channel;
    private int busy, disposed;
    public bool SupportsRoles { get; private set; }
    private LcdConnection(ILcdSerialChannel channel) => this.channel = channel;

    internal static Task<ILcdConnection> OpenAsync(string port, CancellationToken cancellation) =>
        OpenAsync(new SerialChannel(port), cancellation, TimeSpan.FromSeconds(2));

    internal static async Task<ILcdConnection> OpenAsync(ILcdSerialChannel channel, CancellationToken cancellation, TimeSpan resetDelay)
    {
        var connection = new LcdConnection(channel);
        try
        {
            await Task.Run(channel.Open, cancellation);
            await Task.Delay(resetDelay, cancellation); // UNO USB serial resets on open.
            await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                channel.DiscardInput();
                channel.WriteLine("TOBOO_LCD?");
                if (connection.ReadLine(TimeSpan.FromSeconds(4), cancellation) != "TOBOO_LCD,1,UNO,16,2")
                    throw new IOException("UNO LCD 握手不匹配，请检查端口和固件。");
                channel.WriteLine("LCD_CAPS?");
                connection.SupportsRoles = connection.ReadLine(TimeSpan.FromMilliseconds(600), cancellation) switch
                {
                    "LCD_CAPS,ROLE1" => true,
                    "ERR" => false,
                    _ => throw new IOException("UNO LCD 返回了未知的固件能力。")
                };
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public async Task SendAsync(string label, int lifetimeMilliseconds, CancellationToken cancellation)
    {
        if (!Enum.TryParse<Toboo.Core.FacialExpression>(label, true, out var expression)
            || !Enum.IsDefined(expression) || expression.ToString().ToLowerInvariant() != label || lifetimeMilliseconds is < 1 or > 500)
            throw new ArgumentException("Invalid LCD cue.");
        await SendAsync("E", label, lifetimeMilliseconds, cancellation);
    }

    public Task<bool> SendRoleAsync(LcdRole role, int lifetimeMilliseconds, CancellationToken cancellation)
    {
        if (!SupportsRoles) throw new InvalidOperationException("Uno 固件需要更新才能播放角色动画。");
        if (!Enum.IsDefined(role) || lifetimeMilliseconds is < 1 or > 1500)
            throw new ArgumentException("Invalid LCD role.");
        return SendAsync("R", role.ToString().ToLowerInvariant(), lifetimeMilliseconds, cancellation);
    }

    private async Task<bool> SendAsync(string command, string label, int lifetimeMilliseconds, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InvalidOperationException("LCD 正在确认上一条请求，不能排队发送旧表情。");
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                var elapsed = (int)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                var remaining = label == "unknown" ? 1 : lifetimeMilliseconds - elapsed;
                if (remaining < 1)
                {
                    if (command == "R") return false;
                    throw new TimeoutException("表情在串口发送前已过期。");
                }
                channel.WriteLine(FormattableString.Invariant($"{command},{label},{remaining}"));
                var acknowledgement = command == "R" ? "OK,R," + label : "OK," + label;
                if (ReadLine(TimeSpan.FromMilliseconds(600), cancellation) != acknowledgement)
                    throw new IOException("LCD 未确认本次显示请求。");
                return true;
            }, cancellation);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private string ReadLine(TimeSpan timeout, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        var text = new StringBuilder();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellation.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            int value;
            try { value = channel.ReadByte(); }
            catch (TimeoutException) { continue; }
            if (value == '\n') return text.ToString().TrimEnd('\r');
            if (value is < 0 or > 126 || (value < 32 && value != '\r') || text.Length >= 80)
                throw new IOException("LCD 返回了无效或过长的响应。");
            text.Append((char)value);
        }
        throw new TimeoutException("LCD 响应超时。");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) channel.Dispose();
    }

    private sealed class SerialChannel(string port) : ILcdSerialChannel
    {
        private readonly SerialPort serial = new(port, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None, DtrEnable = true, RtsEnable = true,
            NewLine = "\n", Encoding = Encoding.ASCII, ReadTimeout = 50, WriteTimeout = 200
        };
        public void Open() => serial.Open();
        public void DiscardInput() => serial.DiscardInBuffer();
        public void WriteLine(string text) => serial.WriteLine(text);
        public int ReadByte() => serial.ReadByte();
        public void Dispose() => serial.Dispose();
    }
}
