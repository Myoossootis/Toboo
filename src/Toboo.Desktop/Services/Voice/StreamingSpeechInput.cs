using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;

namespace Toboo.Desktop.Services;

internal static class StreamingSpeechInput
{
    private static readonly TimeSpan FrameLifetime = TimeSpan.FromSeconds(2);

    internal static void EndCapture(Stream audio)
    {
        if (audio is PcmInput input) input.EndCapture();
    }

    internal static async Task<T> RunAsync<T>(Func<Action<byte[]>, CancellationToken, Task> stream,
        Func<Action, CancellationToken, Stream, Task<T>> recognize, Action ready, CancellationToken cancellation,
        Action<double>? level = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        using var audio = new PcmInput(level, stopCapture: captureStop.Cancel);
        using var unblock = stop.Token.Register(audio.Stop);
        var readySignals = 0;
        void SignalReady()
        {
            if (Interlocked.Increment(ref readySignals) == 2 && !stop.IsCancellationRequested) ready();
        }
        var capture = Task.Run(async () =>
        {
            var first = true;
            try
            {
                await stream(pcm =>
                {
                    audio.Add(pcm);
                    if (!first) return;
                    first = false;
                    SignalReady();
                }, captureStop.Token).ConfigureAwait(false);
                captureStop.Token.ThrowIfCancellationRequested();
                throw new IOException("麦克风已停止采音。");
            }
            catch (OperationCanceledException) when (captureStop.IsCancellationRequested) { }
            finally { audio.Stop(); }
        });

        Task<T>? recognition = null;
        try
        {
            recognition = recognize(SignalReady, stop.Token, audio);
            await Task.WhenAny(capture, recognition).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (capture.IsCompleted) await capture.ConfigureAwait(false);
            return await recognition.ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            try { await capture.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                if (recognition is not null) await ((Task)recognition).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    // Separate recognizers need independent readers of the same microphone stream.
    internal static async Task<T?> FirstMatchAsync<T>(Stream source, Func<Action, CancellationToken, Stream, Task<T?>> first,
        Func<Action, CancellationToken, Stream, Task<T?>> second, Action ready, CancellationToken cancellation) where T : class
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var firstAudio = new PcmInput(null, 100);
        using var secondAudio = new PcmInput(null, 100);
        using var unblock = stop.Token.Register(() =>
        {
            source.Close();
            firstAudio.Stop();
            secondAudio.Stop();
        });
        var readyCount = 0;
        void SignalReady()
        {
            if (Interlocked.Increment(ref readyCount) == 2 && !stop.IsCancellationRequested) ready();
        }

        var firstResult = first(SignalReady, stop.Token, firstAudio);
        var secondResult = second(SignalReady, stop.Token, secondAudio);
        var copy = Task.Run(async () =>
        {
            var buffer = new byte[640];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var length = source.Read(buffer, 0, buffer.Length);
                    if (length == 0) break;
                    var frame = buffer.AsSpan(0, length).ToArray();
                    await firstAudio.CopyFrameAsync(frame, stop.Token).ConfigureAwait(false);
                    await secondAudio.CopyFrameAsync(frame, stop.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                firstAudio.Complete();
                secondAudio.Complete();
            }
        });

        try
        {
            var pending = new List<Task> { firstResult, secondResult, copy };
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).WaitAsync(cancellation).ConfigureAwait(false);
                pending.Remove(finished);
                if (finished is Task<T?> result)
                {
                    if (await result.ConfigureAwait(false) is { } match) return match;
                }
                else await finished.ConfigureAwait(false);
            }
            return null;
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(firstResult, secondResult, copy).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    // SAPI reads on a native worker. Fill each request until it is complete or the input is stopped.
    private readonly record struct AudioFrame(byte[] Samples, long ReceivedAt)
    {
        internal bool Expired => Stopwatch.GetElapsedTime(ReceivedAt) >= FrameLifetime;
    }

    private sealed class PcmInput(Action<double>? level, int capacity = 256, Action? stopCapture = null) : Stream
    {
        private readonly Channel<AudioFrame> frames = Channel.CreateBounded<AudioFrame>(capacity);
        private ReadOnlyMemory<byte> remaining;
        private long remainingAt;
        private int stopped;
        private long position;
        private long lastMeter = Stopwatch.GetTimestamp();
        private double dcOffset, peak, meterPeak;

        internal void Add(byte[] pcm)
        {
            if (Volatile.Read(ref stopped) != 0) return;
            if (pcm.Length == 0 || (pcm.Length & 1) != 0) throw new IOException("麦克风音频帧不是有效的 16 位采样。");
            // Remove DC offset and amplify quiet input, with immediate gain reduction on peaks.
            for (var i = 0; i < pcm.Length; i += 2)
            {
                var cell = pcm.AsSpan(i, 2);
                var sample = BinaryPrimitives.ReadInt16LittleEndian(cell);
                dcOffset += (sample - dcOffset) / 320;
                var centered = sample - dcOffset;
                var magnitude = Math.Abs(centered);
                peak = Math.Max(magnitude, peak * 0.9998);
                meterPeak = Math.Max(magnitude, meterPeak);
                var gain = Math.Clamp(20000 / Math.Max(1, peak), 1, 16);
                BinaryPrimitives.WriteInt16LittleEndian(cell, (short)Math.Clamp(centered * gain, short.MinValue, short.MaxValue));
            }
            if (!frames.Writer.TryWrite(new AudioFrame(pcm, Stopwatch.GetTimestamp())) && Volatile.Read(ref stopped) == 0)
                throw new IOException("语音唤醒的音频处理积压，请重试。");
            if (level is not null && Stopwatch.GetElapsedTime(lastMeter).TotalMilliseconds >= 100)
            {
                lastMeter = Stopwatch.GetTimestamp();
                level(I2sMicrophone.MeterLevel(meterPeak / 32768));
                meterPeak = 0;
            }
        }

        internal void Stop()
        {
            Interlocked.Exchange(ref stopped, 1);
            Complete();
            while (frames.Reader.TryRead(out _)) { }
        }

        internal void EndCapture()
        {
            Stop();
            stopCapture?.Invoke();
        }

        internal void Complete() => frames.Writer.TryComplete();

        internal async ValueTask CopyFrameAsync(byte[] pcm, CancellationToken cancellation)
        {
            try { await frames.Writer.WriteAsync(new AudioFrame(pcm, Stopwatch.GetTimestamp()), cancellation).ConfigureAwait(false); }
            catch (ChannelClosedException) when (Volatile.Read(ref stopped) != 0) { }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var destination = buffer.AsSpan(offset, count);
            while (!destination.IsEmpty && Volatile.Read(ref stopped) == 0)
            {
                if (!remaining.IsEmpty && Stopwatch.GetElapsedTime(remainingAt) >= FrameLifetime) remaining = default;
                if (remaining.IsEmpty)
                {
                    AudioFrame frame;
                    try { frame = frames.Reader.ReadAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (ChannelClosedException) { break; }
                    if (Volatile.Read(ref stopped) != 0) break;
                    if (frame.Expired) continue;
                    remaining = frame.Samples;
                    remainingAt = frame.ReceivedAt;
                }
                var length = Math.Min(destination.Length, remaining.Length);
                remaining.Span[..length].CopyTo(destination);
                remaining = remaining[length..];
                if (remaining.IsEmpty) remaining = default;
                destination = destination[length..];
            }
            var read = count - destination.Length;
            Interlocked.Add(ref position, read);
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => -1; // The System.Speech stream adapter uses -1 for unknown length.
        public override long Position { get => Interlocked.Read(ref position); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => offset == 0 &&
            (origin == SeekOrigin.Current || origin == SeekOrigin.Begin && Position == 0) ? Position : throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Stop();
            base.Dispose(disposing);
        }
    }
}
