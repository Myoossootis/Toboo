using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Toboo.Desktop.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsWakeMicrophone
{
    internal const int SampleRate = 16000;
    private const int BytesPerSample = sizeof(short);

    internal static string? DefaultInputName()
    {
        try
        {
            using var endpoints = new MMDeviceEnumerator();
            using var microphone = endpoints.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return microphone.FriendlyName.Trim();
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException) { return null; }
    }

    // GetService, packet reads and COM cleanup stay on one background thread.
    internal static Task StreamAsync(Action<byte[]> samples, CancellationToken cancellation) =>
        Task.Run(() => Capture(samples, cancellation), cancellation);

    private static void Capture(Action<byte[]> samples, CancellationToken cancellation)
    {
        using var endpoints = new MMDeviceEnumerator();
        using var microphone = endpoints.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        using var client = microphone.AudioClient;
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            TimeSpan.FromMilliseconds(100).Ticks, 0, new WaveFormat(SampleRate, BytesPerSample * 8, 1), Guid.Empty);
        var input = client.AudioCaptureClient;
        cancellation.ThrowIfCancellationRequested();
        client.Start();
        try
        {
            var lastPacket = Stopwatch.GetTimestamp();
            while (!cancellation.IsCancellationRequested)
            {
                while (!cancellation.IsCancellationRequested && input.GetNextPacketSize() > 0)
                {
                    var buffer = input.GetBuffer(out var frames, out var flags);
                    byte[] pcm;
                    try
                    {
                        pcm = new byte[checked(frames * BytesPerSample)];
                        if (pcm.Length > 0 && (flags & AudioClientBufferFlags.Silent) == 0)
                            Marshal.Copy(buffer, pcm, 0, pcm.Length);
                    }
                    finally { input.ReleaseBuffer(frames); }
                    if (pcm.Length == 0) continue;
                    lastPacket = Stopwatch.GetTimestamp();
                    samples(pcm);
                }
                if (Stopwatch.GetElapsedTime(lastPacket) >= TimeSpan.FromSeconds(3))
                    throw new IOException("系统默认麦克风没有返回音频。");
                cancellation.WaitHandle.WaitOne(10);
            }
            cancellation.ThrowIfCancellationRequested();
        }
        finally
        {
            try { client.Stop(); }
            catch (COMException) { /* The endpoint may have been removed. AudioClient still releases it. */ }
        }
    }
}
