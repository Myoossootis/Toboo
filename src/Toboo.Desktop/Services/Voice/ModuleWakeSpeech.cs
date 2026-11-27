namespace Toboo.Desktop.Services;

internal sealed class ModuleWakeSpeech(IStreamingMicrophoneCapture microphone, IVoiceWakeService recognizer)
{
    internal Task<bool> WaitForWakeAsync(string port, Action ready, CancellationToken cancellation, Action<double>? level = null) =>
        StreamingSpeechInput.RunAsync((samples, token) => microphone.StreamAsync(port, samples, token),
            (onReady, token, audio) => recognizer.WaitForWakeAsync(onReady, token, audio), ready, cancellation, level);

    internal Task<string> ListenOnceAsync(string port, Action ready, CancellationToken cancellation, Action<double>? level = null,
        Action? recognizing = null) =>
        StreamingSpeechInput.RunAsync((samples, token) => microphone.StreamAsync(port, samples, token),
            (onReady, token, audio) => recognizer.ListenOnceAsync(onReady, token, audio, recognizing: recognizing), ready, cancellation, level);
}
