using System.Buffers.Binary;
using System.Text;
using Whisper.net;

namespace Toboo.Desktop.Services;

internal static class WhisperLiveSpeech
{
    private const int SampleRate = 16000;
    private const int FrameSamples = 3200;
    private const int VadWindow = SampleRate;
    private const int EndSilence = SampleRate / 2;
    private static readonly string[] Greetings = ["你好", "hey", "hello", "哈喽", "哈啰", "嘿", "嗨", "nihao"];
    private static readonly string[] Names = ["toboo", "tobo", "toubow", "toeboo", "tooboo", "toebow", "toebeau",
        "tobu", "tuboo", "tubow", "tobow", "托布", "托波", "偷波", "图布", "偷包"];
    private static readonly HashSet<string> WakePhrases = new(Greetings.Concat(Names)
        .Concat(from greeting in Greetings.Append("hi") from name in Names select greeting + name), StringComparer.Ordinal);

    internal static bool IsWakePhrase(string text) =>
        WakePhrases.Contains(string.Concat(text.ToLowerInvariant().Where(char.IsLetterOrDigit)));

    internal static Task<bool> WaitForWakeAsync(Action ready, CancellationToken cancellation, Stream audio) =>
        Task.Run(async () =>
        {
            await WhisperPcmRecognizer.VerifyModelAsync(WhisperPcmRecognizer.ModelPath, WhisperPcmRecognizer.ModelSha256, cancellation);
            await WhisperPcmRecognizer.VerifyModelAsync(WhisperPcmRecognizer.VadPath, WhisperPcmRecognizer.VadSha256, cancellation);
            using var model = WhisperFactory.FromPath(WhisperPcmRecognizer.ModelPath);
            using var vadModel = WhisperVadFactory.FromPath(WhisperPcmRecognizer.VadPath);
            using var vad = CreateVad(vadModel);
            // Short clips fit in a smaller context, so listening can resume while the next phrase is still fresh.
            await using var english = WhisperBuilder(model, "en").WithAudioContextSize(192).Build();
            await using var chinese = WhisperBuilder(model, "zh").WithAudioContextSize(192).Build();
            ready();
            while (true)
            {
                var speech = ReadUtterance(audio, vad, maxWait: null, maxSpeech: 3, cancellation);
                if (speech is null) return false;
                try
                {
                    if (speech.Length == 0) continue;
                    if (IsWakePhrase(await DecodeAsync(english, speech, cancellation)) ||
                        IsWakePhrase(await DecodeAsync(chinese, speech, cancellation))) return true;
                }
                finally { Array.Clear(speech); }
            }
        }, cancellation);

    internal static Task<string> ListenOnceAsync(Action ready, CancellationToken cancellation, Stream audio, Action? recognizing = null) =>
        Task.Run(async () =>
        {
            await WhisperPcmRecognizer.VerifyModelAsync(WhisperPcmRecognizer.VadPath, WhisperPcmRecognizer.VadSha256, cancellation);
            using var vadModel = WhisperVadFactory.FromPath(WhisperPcmRecognizer.VadPath);
            using var vad = CreateVad(vadModel);
            ready();
            var speech = ReadUtterance(audio, vad, maxWait: IVoiceWakeService.ReplyWaitSeconds, maxSpeech: 15, cancellation);
            StreamingSpeechInput.EndCapture(audio);
            if (speech is not { Length: > 0 }) return "";
            try
            {
                recognizing?.Invoke();
                await WhisperPcmRecognizer.VerifyModelAsync(WhisperPcmRecognizer.ModelPath, WhisperPcmRecognizer.ModelSha256, cancellation);
                using var model = WhisperFactory.FromPath(WhisperPcmRecognizer.ModelPath);
                await using var chinese = WhisperBuilder(model, "zh")
                    .WithPrompt(WhisperPcmRecognizer.ChinesePrompt)
                    .WithBeamSearchSamplingStrategy(strategy => strategy.WithBeamSize(3)).Build();
                return await DecodeAsync(chinese, speech, cancellation);
            }
            finally { Array.Clear(speech); }
        }, cancellation);

    private static WhisperVadProcessor CreateVad(WhisperVadFactory factory) =>
        factory.CreateBuilder().WithUseGpu(false).WithThreads(1).WithThreshold(0.46f)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(100))
            .WithSpeechPadding(TimeSpan.FromMilliseconds(100)).Build();

    private static WhisperProcessorBuilder WhisperBuilder(WhisperFactory model, string language) =>
        model.CreateBuilder().WithLanguage(language).WithNoContext().WithTemperature(0)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 6)).WithProbabilities().WithNoSpeechThreshold(0.6f);

    private static async Task<string> DecodeAsync(WhisperProcessor processor, float[] speech, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(speech, cancellation).ConfigureAwait(false))
        {
            if (WhisperPcmRecognizer.AcceptSegment(segment.Text, segment.Probability, segment.NoSpeechProbability))
                text.Append(segment.Text);
            if (text.Length > 4000) return "";
        }
        return text.ToString().Trim();
    }

    // Keep one second of pre-roll while waiting. VAD checks overlapping PCM from the same device.
    internal static float[]? ReadUtterance(Stream audio, WhisperVadProcessor vad, int? maxWait, int maxSpeech,
        CancellationToken cancellation)
    {
        var frame = new byte[FrameSamples * sizeof(short)];
        var samples = new float[(maxSpeech + 2) * SampleRate];
        var length = 0;
        var elapsed = 0;
        var start = -1;
        var end = 0;
        var oversized = false;
        try
        {
            for (;;)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = audio.ReadAtLeast(frame, frame.Length, false) / sizeof(short);
                cancellation.ThrowIfCancellationRequested();
                if (count == 0) return start < 0 ? null : oversized ? [] : samples[start..end];
                for (var index = 0; index < count; index++)
                    samples[length + index] = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(index * sizeof(short))) / 32768f;
                length += count;
                elapsed += count;
                var windowStart = Math.Max(0, length - VadWindow);
                var detected = vad.DetectSpeech(samples.AsSpan(windowStart, length - windowStart));
                if (detected.Count > 0)
                {
                    if (start < 0)
                        start = Math.Max(0, windowStart + (int)(detected[0].Start.TotalSeconds * SampleRate) - FrameSamples / 2);
                    end = Math.Max(end, Math.Min(length, windowStart + (int)(detected[^1].End.TotalSeconds * SampleRate)));
                }

                if (start >= 0 && length - end >= EndSilence)
                    return oversized ? [] : samples[start..Math.Min(end + FrameSamples, length)];
                if (start >= 0 && length - start >= maxSpeech * SampleRate)
                {
                    if (maxWait is not null) return samples[start..length];
                    // Skip the rest of an overlong conversation instead of recognizing a suffix as a greeting.
                    oversized = true;
                }
                if (start < 0 && maxWait is { } seconds && elapsed >= seconds * SampleRate) return null;
                if ((start < 0 || oversized) && length > VadWindow)
                {
                    var discard = length - VadWindow;
                    samples.AsSpan(discard, VadWindow).CopyTo(samples);
                    Array.Clear(samples, VadWindow, discard);
                    length = VadWindow;
                    if (oversized) { start = 0; end = Math.Max(0, end - discard); }
                }
            }
        }
        finally
        {
            Array.Clear(frame);
            Array.Clear(samples);
        }
    }
}
