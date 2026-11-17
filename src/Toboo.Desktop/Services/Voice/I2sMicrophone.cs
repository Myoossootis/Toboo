using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Toboo.Desktop.Services;

internal interface IMicrophoneCapture
{
    Task<byte[]> RecordAsync(string port, Action started, Action<double> level, CancellationToken cancellation);
    void Finish();
}

internal sealed class I2sMicrophone : IMicrophoneCapture
{
    private readonly Func<string, SharedEsp32> connect;
    internal I2sMicrophone(Func<string, SharedEsp32>? connect = null) => this.connect = connect ?? SharedEsp32.Get;
    private int finishing;
    public void Finish() => Interlocked.Exchange(ref finishing, 1);
    public Task<byte[]> RecordAsync(string port, Action started, Action<double> level, CancellationToken cancellation)
    {
        Volatile.Write(ref finishing, 0);
        return RecordCoreAsync(port, started, level, cancellation);
    }
    private async Task<byte[]> RecordCoreAsync(string port, Action started, Action<double> level, CancellationToken cancellation)
    {
        var bus = connect(port);
        var incoming = bus.BeginMicrophone();
        var session = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)) | 1u;
        var id = session.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        var admitted = false;
        var ended = false;
        try
        {
            bus.WriteLine("TOBOO_MIC?");
            var hello = await NextAsync(incoming, cancellation);
            if (hello.Text != "TOBOO_MIC,1,ICS43432,16000,16,1,4,5,6,LEFT")
                throw new IOException("此串口不是 ICS43432 合并固件，请核对 ESP32 端口与固件。");
            if (Volatile.Read(ref finishing) != 0) return [];
            bus.WriteLine("MIC_START," + id);
            admitted = true;
            var begin = await NextAsync(incoming, cancellation);
            if (!string.Equals(begin.Text, "MIC_BEGIN," + id, StringComparison.OrdinalIgnoreCase))
                throw new IOException("ESP32 未确认本次采音。");
            started();
            using var pcm = new MemoryStream();
            uint expected = 0;
            var watch = Stopwatch.StartNew();
            var stopSent = false;
            while (true)
            {
                if (!stopSent && (Volatile.Read(ref finishing) != 0 || watch.Elapsed.TotalSeconds >= 30))
                {
                    bus.WriteLine("MIC_STOP," + id); stopSent = true;
                }
                var packet = await NextAsync(incoming, cancellation);
                ended = packet.Session == session && packet.Kind is 2 or 3;
                if (packet.Session != session || packet.Sequence != expected++ || packet.Kind is not (1 or 2))
                    throw new IOException("采音丢帧或设备报告错误，本次语音已取消。");
                if (packet.Kind == 2) { ended = true; break; }
                if (pcm.Length + packet.Pcm.Length > 16000 * 2 * 31)
                    throw new IOException("采音超过时长限制。");
                pcm.Write(packet.Pcm);
                if (packet.Sequence % 8 == 0)
                {
                    var decibels = 20 * Math.Log10(Math.Max(Peak(packet.Pcm), 1.0 / 32768));
                    level(Math.Clamp((decibels + 72) / 72, 0, 1));
                }
            }
            return pcm.ToArray();
        }
        finally
        {
            if (admitted && !ended)
            {
                try
                {
                    bus.WriteLine("MIC_STOP," + id);
                    using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    while (!ended)
                    {
                        var packet = await incoming.ReadAsync(drain.Token);
                        ended = packet.Session == session && packet.Kind is 2 or 3;
                    }
                }
                catch (Exception) { bus.Dispose(); }
            }
            bus.EndMicrophone();
        }
    }
    private static async Task<MicPacket> NextAsync(ChannelReader<MicPacket> input, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try { return await input.ReadAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("ESP32 没有返回音频，请核对固件和 USB 接口。"); }
        catch (ChannelClosedException error) { throw new IOException("ESP32 音频连接已中断。", error); }
    }
    internal static double Peak(byte[] pcm)
    {
        var peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i))));
        return peak / 32768.0;
    }
    internal static void Normalize(byte[] pcm)
    {
        if (pcm.Length == 0 || pcm.Length % 2 != 0) throw new ArgumentException("PCM 数据长度无效。");
        long sum = 0;
        for (var i = 0; i < pcm.Length; i += 2)
            sum += System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i));
        var mean = (int)(sum / (pcm.Length / 2));
        var peak = 0;
        for (var i = 0; i < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i)) - mean));
        var gain = Math.Clamp(26000.0 / Math.Max(peak, 1), 1, 16);
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var value = (System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i)) - mean) * gain;
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)Math.Clamp(value, short.MinValue, short.MaxValue));
        }
    }
}
