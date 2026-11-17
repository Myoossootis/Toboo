using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Toboo.Desktop.Services;

internal interface IEsp32Serial : IDisposable
{
    int ReadByte();
    void WriteLine(string line);
}

// One USB owner routes bounded text replies to the servo helper and binary audio to the microphone.
internal sealed class SharedEsp32 : IDisposable
{
    private static readonly Dictionary<string, SharedEsp32> ports = new(StringComparer.OrdinalIgnoreCase);
    private readonly IEsp32Serial serial;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private Channel<string>? servo;
    private Channel<MicPacket>? microphone;
    private int disposed;

    internal static SharedEsp32 Get(string port)
    {
        lock (ports)
        {
            if (ports.TryGetValue(port, out var current) && Volatile.Read(ref current.disposed) == 0) return current;
            var opened = new SharedEsp32(new SerialChannel(port));
            ports[port] = opened; return opened;
        }
    }
    internal static void CloseAll()
    {
        lock (ports) { foreach (var item in ports.Values) item.Dispose(); ports.Clear(); }
    }
    internal SharedEsp32(IEsp32Serial channel)
    {
        serial = channel;
        try
        {
            listener.Start();
            _ = Task.Run(ReadUsb);
            _ = AcceptServoAsync();
        }
        catch { serial.Dispose(); listener.Stop(); throw; }
    }
    internal (string Url, string Token) Proxy => ($"socket://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", token);
    internal ChannelReader<MicPacket> BeginMicrophone()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (microphone is not null) throw new InvalidOperationException("麦克风正在使用中。");
            microphone = Channel.CreateBounded<MicPacket>(128);
            return microphone.Reader;
        }
    }
    internal void EndMicrophone()
    {
        lock (sync) { microphone?.Writer.TryComplete(); microphone = null; }
    }
    internal void WriteLine(string line)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            serial.WriteLine(line);
        }
    }
    private int ReadByte(bool partial = false)
    {
        var started = Stopwatch.GetTimestamp();
        while (!lifetime.IsCancellationRequested)
        {
            try { return serial.ReadByte(); }
            catch (TimeoutException)
            {
                if (partial && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 300)
                    throw new IOException("ESP32 返回了不完整的数据帧。");
            }
        }
        throw new OperationCanceledException();
    }
    private void ReadUsb()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var first = ReadByte();
                if (first == 0xA5)
                {
                    var header = new byte[16]; header[0] = 0xA5;
                    for (var i = 1; i < 16; i++) header[i] = (byte)ReadByte(partial: true);
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
                    if (length > 256) throw new IOException("ESP32 音频帧过长。");
                    var bytes = new byte[20 + length]; header.CopyTo(bytes, 0);
                    for (var i = 16; i < bytes.Length; i++) bytes[i] = (byte)ReadByte(partial: true);
                    Deliver(MicPacket.Decode(bytes));
                }
                else
                {
                    var text = new StringBuilder();
                    var value = first;
                    while (value != '\n')
                    {
                        if (value is < 0 or > 126 || (value < 32 && value != '\r') || text.Length >= 240)
                            throw new IOException("ESP32 返回无效文本。");
                        if (value != '\r') text.Append((char)value);
                        value = ReadByte(partial: true);
                    }
                    var line = text.ToString();
                    if (line.StartsWith("MIC_", StringComparison.Ordinal) || line.StartsWith("TOBOO_MIC,", StringComparison.Ordinal))
                        Deliver(new(0, 0, 0, [], line));
                    else
                    {
                        lock (sync)
                            if (servo is { } active && !active.Writer.TryWrite(line)) throw new IOException("云台回复积压。");
                    }
                }
            }
        }
        catch (Exception error)
        {
            lock (sync) { microphone?.Writer.TryComplete(error); servo?.Writer.TryComplete(error); }
        }
        finally { Dispose(); }
    }
    private void Deliver(MicPacket packet)
    {
        lock (sync)
            if (microphone is { } active && !active.Writer.TryWrite(packet))
                throw new IOException("音频接收积压，本次采音已取消。");
    }
    private async Task AcceptServoAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                _ = ServeAsync(client);
            }
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        Channel<string>? replies = null;
        using (client)
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                cancel.CancelAfter(TimeSpan.FromSeconds(3));
                if (await ReadLineAsync(stream, cancel.Token) != "AUTH," + token) return;
                cancel.CancelAfter(Timeout.Infinite);
                lock (sync)
                {
                    if (servo is not null) return;
                    servo = replies = Channel.CreateBounded<string>(128);
                }
                var sending = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var line in replies.Reader.ReadAllAsync(cancel.Token))
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), cancel.Token);
                    }
                    finally { cancel.Cancel(); }
                });
                try
                {
                    while (await ReadLineAsync(stream, cancel.Token) is { } line)
                    {
                        if (line.StartsWith("MIC_", StringComparison.Ordinal) || line == "TOBOO_MIC?")
                            throw new IOException("云台通道收到采音命令。");
                        WriteLine(line);
                    }
                }
                finally { cancel.Cancel(); await sending; }
            }
            catch (Exception) { /* Disconnect lets the independent firmware watchdog hold servo output. */ }
            finally
            {
                lock (sync) if (ReferenceEquals(servo, replies)) { servo?.Writer.TryComplete(); servo = null; }
            }
        }
    }
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellation)
    {
        var bytes = new byte[1];
        var line = new StringBuilder();
        while (await stream.ReadAsync(bytes, cancellation) == 1)
        {
            var value = bytes[0];
            if (value == '\n') return line.ToString();
            if (value is < 32 or > 126 || line.Length >= 192) throw new IOException("串口代理命令无效。");
            line.Append((char)value);
        }
        return null;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); listener.Stop(); serial.Dispose();
        lock (sync) { microphone?.Writer.TryComplete(new IOException("ESP32 已断开。")); servo?.Writer.TryComplete(); }
    }
    private sealed class SerialChannel : IEsp32Serial
    {
        private readonly SerialPort port;
        internal SerialChannel(string name)
        {
            port = new(name, 115200) { DtrEnable = false, RtsEnable = false, ReadTimeout = 50, WriteTimeout = 100,
                NewLine = "\n", ReadBufferSize = 16384 };
            try { port.Open(); }
            catch { port.Dispose(); throw; }
        }
        public int ReadByte() => port.ReadByte();
        public void WriteLine(string line) => port.WriteLine(line);
        public void Dispose() => port.Dispose();
    }
}
