using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Toboo.Desktop.Services;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class TemporaryMicrophoneRecordingTests
{
    [Fact]
    public void RemovesOnlyExpiredAppRecordings()
    {
        var directory = Directory.CreateTempSubdirectory("toboo-audio-");
        var expired = TemporaryMicrophoneRecording.Save(directory.FullName, [1, 0]);
        var fresh = TemporaryMicrophoneRecording.Save(directory.FullName, [1, 0]);
        var folder = Path.GetDirectoryName(expired)!;
        var other = Path.Combine(folder, "mic-user-export.wav");
        var legacy = Path.Combine(directory.FullName, "mic-debug.wav");
        try
        {
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(expired, now - TemporaryMicrophoneRecording.Lifetime - TimeSpan.FromMinutes(1));
            File.WriteAllBytes(other, [1, 0]);
            File.SetLastWriteTimeUtc(other, File.GetLastWriteTimeUtc(expired));
            File.WriteAllBytes(legacy, [1, 0]);
            File.SetLastWriteTimeUtc(legacy, File.GetLastWriteTimeUtc(expired));

            TemporaryMicrophoneRecording.RemoveExpired(directory.FullName, now);

            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(other));
            Assert.False(File.Exists(legacy));
        }
        finally
        {
            File.Delete(expired);
            File.Delete(fresh);
            File.Delete(other);
            File.Delete(legacy);
            Directory.Delete(folder);
            directory.Delete();
        }
    }
}

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
        internal bool HoldHello;
        internal uint SessionFrames = uint.MaxValue;
        internal ConcurrentQueue<string> Written = new();
        public int ReadByte()
        {
            if (incoming.TryDequeue(out var value)) return value;
            Thread.Sleep(1); throw new TimeoutException();
        }
        private void Enqueue(byte[] bytes) { foreach (var value in bytes) incoming.Enqueue(value); }
        internal void ReplyHello() => Enqueue(TruncatedHello ? [0xA5] : Encoding.ASCII.GetBytes("TOBOO_MIC,1,ICS43432,16000,16,1,4,5,6,LEFT\n"));
        public void WriteLine(string line)
        {
            Written.Enqueue(line);
            if (line == "TOBOO_MIC?" && !HoldHello) ReplyHello();
            else if (line == "STATUS") Enqueue(Encoding.ASCII.GetBytes("STATUS,DISARMED\n"));
            else if (line.StartsWith("MIC_START,"))
            {
                var startedSession = Convert.ToUInt32(line[10..], 16);
                lock (sync)
                {
                    session = startedSession; sequence = 0; running = true;
                    Enqueue(Encoding.ASCII.GetBytes("MIC_BEGIN," + line[10..] + "\n"));
                }
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        lock (sync)
                        {
                            if (!running || disposed || session != startedSession) return;
                            if (sequence >= SessionFrames)
                            {
                                running = false;
                                Enqueue(Frame(session, sequence++, 2, []));
                                return;
                            }
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

    [Fact]
    public async Task StreamRenewsAudioSessionsAndReleasesOwnershipForManualCapture()
    {
        var device = new Device { SessionFrames = 4 };
        using var bus = new SharedEsp32(device);
        var mic = new I2sMicrophone(_ => bus);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var packets = 0;
        var stream = mic.StreamAsync("fake", pcm =>
        {
            Assert.InRange(I2sMicrophone.Peak(pcm), 0.03, 0.04);
            if (++packets == 10) cancellation.Cancel();
        }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
        Assert.Equal(10, packets);
        Assert.Single(device.Written, line => line == "TOBOO_MIC?");
        Assert.Equal(3, device.Written.Count(line => line.StartsWith("MIC_START,", StringComparison.Ordinal)));
        Assert.Single(device.Written, line => line.StartsWith("MIC_STOP,", StringComparison.Ordinal));

        var manual = await mic.RecordAsync("fake", () => { }, _ => { }, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4 * 256, manual.Length);
    }

    [Fact]
    public async Task CancellingDuringTheUntaggedProbeDoesNotLeaveAReplyForTheNextOwner()
    {
        var device = new Device { HoldHello = true, SessionFrames = 4 };
        using var bus = new SharedEsp32(device);
        var mic = new I2sMicrophone(_ => bus);
        using var cancellation = new CancellationTokenSource();
        var stream = mic.StreamAsync("fake", _ => throw new InvalidOperationException("Audio started after cancellation."), cancellation.Token);
        cancellation.Cancel();
        Assert.False(stream.IsCompleted);
        device.ReplyHello();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
        Assert.DoesNotContain(device.Written, line => line.StartsWith("MIC_START,", StringComparison.Ordinal));

        device.HoldHello = false;
        var manual = await mic.RecordAsync("fake", () => { }, _ => { }, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEmpty(manual);
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
    public async Task StreamingFrameLossUnblocksRecognitionAndRejectsAPartialMatch()
    {
        using var bus = new SharedEsp32(new Device { SkipFrame = true });
        var recognizer = new BlockingRecognizer();
        var wake = new ModuleWakeSpeech(new I2sMicrophone(_ => bus), recognizer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await Assert.ThrowsAsync<IOException>(() => wake.WaitForWakeAsync("fake", () => { }, deadline.Token));
        Assert.True(recognizer.ReadEnded);
    }

    [Fact]
    public async Task EndingUtteranceCaptureReleasesInputWhileRecognitionContinues()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = StreamingSpeechInput.RunAsync(async (samples, cancellation) =>
        {
            try
            {
                samples([1, 0]);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            finally { captureStopped.TrySetResult(); }
        }, async (started, cancellation, audio) =>
        {
            started();
            await ready.Task.WaitAsync(cancellation);
            StreamingSpeechInput.EndCapture(audio);
            return await decode.Task.WaitAsync(cancellation);
        }, () => ready.TrySetResult(), deadline.Token);

        await captureStopped.Task.WaitAsync(deadline.Token);
        Assert.False(result.IsCompleted);
        decode.SetResult("测试收音完成");
        Assert.Equal("测试收音完成", await result.WaitAsync(deadline.Token));
    }

    private sealed class BlockingRecognizer : IVoiceWakeService
    {
        internal bool ReadEnded;

        public Task<bool> WaitForWakeAsync(Action ready, CancellationToken cancellation, Stream? audio = null, Action<double>? level = null) => Task.Run(() =>
        {
            ready();
            var bytes = new byte[1024];
            while (audio!.Read(bytes, 0, bytes.Length) > 0) { }
            ReadEnded = true;
            return true;
        });

        public Task<string> ListenOnceAsync(Action ready, CancellationToken cancellation, Stream? audio = null,
            Action<double>? level = null, Action? recognizing = null) => throw new NotSupportedException();
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
