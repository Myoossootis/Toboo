using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using Whisper.net;

namespace Toboo.Desktop.Services;

internal static class WhisperPcmRecognizer
{
    internal const string ModelSha256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
    internal const string VadSha256 = "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf";
    internal static string ModelPath => Path.Combine(AppContext.BaseDirectory, "models", "speech", "ggml-base.bin");
    internal static string VadPath => Path.Combine(AppContext.BaseDirectory, "models", "speech", "ggml-silero-v5.1.2.bin");
    internal static string? UnavailableReason => !Avx.IsSupported || !Avx2.IsSupported || !Fma.IsSupported || !F16cSupported()
        ? "本地模块识别需要支持 AVX2 / FMA / F16C 的 x64 电脑"
        : !File.Exists(ModelPath) || !File.Exists(VadPath) ? "缺少本地中文识别模型，请运行 scripts/prepare-speech-model.ps1" : null;

    // .NET 8 exposes F16C through CPUID rather than a dedicated intrinsic class.
    private static bool F16cSupported() => X86Base.IsSupported && (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0;

    public static Task<string> RecognizeAsync(byte[] pcm, CancellationToken cancellation,
        Action<string, float, float, bool>? observe = null) => Task.Run(async () =>
    {
        cancellation.ThrowIfCancellationRequested();
        var samples = ToSamples(pcm);
        if (samples.Length < 1600 || !samples.Any(value => Math.Abs(value) > 0.00001f)) return "";
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        await VerifyModelAsync(VadPath, VadSha256, cancellation);
        using var vadFactory = WhisperVadFactory.FromPath(VadPath);
        await using var vad = vadFactory.CreateBuilder().WithUseGpu(false).WithThreads(1).WithThreshold(0.5f)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(150))
            .WithSpeechPadding(TimeSpan.FromMilliseconds(200)).Build();
        var speech = await vad.DetectSpeechAsync(samples, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (speech.Count == 0) return "";
        var start = Math.Clamp((int)(speech[0].Start.TotalSeconds * 16000), 0, samples.Length);
        var end = Math.Clamp((int)(speech[^1].End.TotalSeconds * 16000), start, samples.Length);
        if (end <= start) return "";
        samples = samples[start..end];
        await VerifyModelAsync(ModelPath, ModelSha256, cancellation);
        cancellation.ThrowIfCancellationRequested();
        // A fresh processor has no transcript context from previous conversations.
        using var factory = WhisperFactory.FromPath(ModelPath);
        await using var processor = factory.CreateBuilder().WithLanguage("zh").WithNoContext()
            .WithPrompt("以下是普通话简体中文对话，可能包含 toboo、DeepSeek、摄像头、云台等术语。")
            .WithTemperature(0).WithBeamSearchSamplingStrategy(strategy => strategy.WithBeamSize(3))
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 6))
            .WithProbabilities().WithNoSpeechThreshold(0.6f).Build();
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            var accepted = AcceptSegment(segment.Text, segment.Probability, segment.NoSpeechProbability);
            observe?.Invoke(segment.Text, segment.Probability, segment.NoSpeechProbability, accepted);
            if (accepted) text.Append(segment.Text.Trim());
            if (text.Length > 4000) throw new InvalidOperationException("这段语音太长，请分成几句说。");
        }
        cancellation.ThrowIfCancellationRequested();
        return text.ToString().Trim();
    }, cancellation);

    private static async Task VerifyModelAsync(string path, string expected, CancellationToken cancellation)
    {
        using var model = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(model, cancellation)).ToLowerInvariant();
        if (hash != expected) throw new InvalidDataException("本地中文识别模型校验失败，请重新准备模型。");
    }

    internal static bool AcceptSegment(string text, float probability, float noSpeech) =>
        !string.IsNullOrWhiteSpace(text) && float.IsFinite(probability) && probability >= 0.2f
        && float.IsFinite(noSpeech) && noSpeech < 0.6f;

    internal static float[] ToSamples(byte[] pcm)
    {
        if ((pcm.Length & 1) != 0 || pcm.Length > 16000 * 2 * 31)
            throw new InvalidDataException("录音必须是最长 31 秒的 16kHz / 16 位单声道 PCM。");
        var samples = new float[pcm.Length / 2];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2, 2)) / 32768f;
        return samples;
    }
}
