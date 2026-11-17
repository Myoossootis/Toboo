using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Toboo.Desktop.Services;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class MicrophoneTests
{
    private sealed class Device : IEsp32Serial
    {
        private readonly ConcurrentQueue<byte> incoming = new();
        private readonly object sync = new();
        private uint session, sequence;
        private bool running, disposed;
        internal bool SkipFrame;
        internal bool TruncatedHello;
        internal ConcurrentQueue<string> Written = new();
        public int ReadByte()
        {
            if (incoming.TryDequeue(out var value)) return value;
            Thread.Sleep(1); throw new TimeoutException();
        }
        private void Enqueue(byte[] bytes) { foreach (var value in bytes) incoming.Enqueue(value); }
        public void WriteLine(string line)
        {
            Written.Enqueue(line);
            if (line == "TOBOO_MIC?") Enqueue(TruncatedHello ? [0xA5] : Encoding.ASCII.GetBytes("TOBOO_MIC,1,ICS43432,16000,16,1,4,5,6,LEFT\n"));
            else if (line == "STATUS") Enqueue(Encoding.ASCII.GetBytes("STATUS,DISARMED\n"));
            else if (line.StartsWith("MIC_START,"))
            {
                lock (sync)
                {
                    session = Convert.ToUInt32(line[10..], 16); sequence = 0; running = true;
                    Enqueue(Encoding.ASCII.GetBytes("MIC_BEGIN," + line[10..] + "\n"));
                }
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        lock (sync)
                        {
                            if (!running || disposed) return;
                            var pcm = new byte[256];
                            for (var i = 0; i < pcm.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), 1000);
                            if (SkipFrame && sequence == 1) sequence++;
                            Enqueue(Frame(session, sequence++, 1, pcm));
                        }
                        await Task.Delay(5);
                    }
                });
            }
            else if (line.StartsWith("MIC_STOP,"))
            {
                lock (sync) { if (!running) return; running = false; Enqueue(Frame(session, sequence++, 2, [])); }
            }
        }
        public void Dispose() { lock (sync) { disposed = true; running = false; } }
    }

    private static byte[] Frame(uint session, uint sequence, byte kind, byte[] pcm)
    {
        var frame = new byte[20 + pcm.Length];
        new byte[] { 0xA5, 0x5A, 0xC3, 0x3C }.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), session);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(12), (ushort)pcm.Length);
        frame[14] = kind; pcm.CopyTo(frame, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(frame.Length - 4), MicPacket.Crc32(frame.AsSpan(0, frame.Length - 4)));
        return frame;
    }

    [Fact]
    public async Task AudioAndServoTextShareOneUsbOwnerAndCancelCanRecordAgain()
    {
        var device = new Device();
        using var bus = new SharedEsp32(device);
        using var client = new TcpClient();
        var proxy = bus.Proxy;
        await client.ConnectAsync("127.0.0.1", int.Parse(proxy.Url.Split(':')[2]));
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("AUTH," + proxy.Token + "\n"));
        var mic = new I2sMicrophone(_ => bus);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = mic.RecordAsync("fake", () => started.SetResult(), _ => { }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("STATUS\n"));
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Assert.Equal("STATUS,DISARMED", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        mic.Finish();
        var pcm = await recording.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEmpty(pcm);
        Assert.InRange(I2sMicrophone.Peak(pcm), 0.03, 0.04);
        Assert.DoesNotContain(device.Written, line => line.StartsWith("ARM") || line.StartsWith("MOVE"));

        using var cancel = new CancellationTokenSource();
        var again = mic.RecordAsync("fake", () => { }, _ => { }, cancel.Token);
        cancel.CancelAfter(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => again);
        var third = mic.RecordAsync("fake", () => mic.Finish(), _ => { }, default);
        await third.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ASequenceGapRejectsTheUtterance()
    {
        using var bus = new SharedEsp32(new Device { SkipFrame = true });
        var mic = new I2sMicrophone(_ => bus);
        await Assert.ThrowsAsync<IOException>(() => mic.RecordAsync("fake", () => { }, _ => { }, default));
    }

    [Fact]
    public async Task TruncatedFramesCloseTheSharedConnectionSoCallersCanReconnect()
    {
        using var bus = new SharedEsp32(new Device { TruncatedHello = true });
        var mic = new I2sMicrophone(_ => bus);
        await Assert.ThrowsAsync<IOException>(() => mic.RecordAsync("fake", () => { }, _ => { }, default));
        Assert.Throws<ObjectDisposedException>(() => bus.WriteLine("STATUS"));
    }

    [Fact]
    public void CorruptionAndOversizedPayloadsAreRejectedBeforeRecognition()
    {
        Assert.Equal(0xCBF43926u, MicPacket.Crc32(Encoding.ASCII.GetBytes("123456789")));
        var bytes = Frame(7, 2, 1, [0, 128, 255, 127]);
        Assert.Equal(7u, MicPacket.Decode(bytes).Session);
        bytes[16] ^= 1;
        Assert.Throws<IOException>(() => MicPacket.Decode(bytes));
        Assert.Throws<IOException>(() => MicPacket.Decode(Frame(7, 2, 1, new byte[258])));
    }

    [Fact]
    public void QuietPcmLosesDcOffsetAndGetsBoundedAmplification()
    {
        var pcm = new byte[400];
        for (var i = 0; i < 200; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(i % 2 == 0 ? 1100 : -900));
        I2sMicrophone.Normalize(pcm);
        Assert.Equal(16000, BinaryPrimitives.ReadInt16LittleEndian(pcm));
        Assert.Equal(-16000, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2)));
        Assert.InRange(I2sMicrophone.Peak(pcm), 0.48, 0.49);
    }
}
