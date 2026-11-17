using Toboo.Desktop.Services;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class WhisperPcmTests
{
    [Fact]
    public void SignedPcmKeepsItsPolarityAndAmplitude()
    {
        Assert.Equal(new[] { 0f, -1f, 32767 / 32768f, -1 / 32768f },
            WhisperPcmRecognizer.ToSamples([0, 0, 0, 128, 255, 127, 255, 255]));
        Assert.Throws<InvalidDataException>(() => WhisperPcmRecognizer.ToSamples([0]));
        Assert.Throws<InvalidDataException>(() => WhisperPcmRecognizer.ToSamples(new byte[16000 * 2 * 31 + 2]));
    }

    [Fact]
    public async Task SilenceAndCancellationDoNotProduceATranscript()
    {
        Assert.Equal("", await WhisperPcmRecognizer.RecognizeAsync(new byte[32000], CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WhisperPcmRecognizer.RecognizeAsync(new byte[32000], cancelled.Token));
    }

    [Theory]
    [InlineData("你好", 0.8f, 0.01f, true)]
    [InlineData("你好", 0.19f, 0.01f, false)]
    [InlineData("你好", 0.8f, 0.8f, false)]
    [InlineData("", 0.8f, 0.01f, false)]
    [InlineData("你好", float.NaN, 0f, false)]
    [InlineData("你好", 0.8f, float.NaN, false)]
    public void UncertainOrNoSpeechSegmentsAreDiscarded(string text, float probability, float noSpeech, bool accepted)
        => Assert.Equal(accepted, WhisperPcmRecognizer.AcceptSegment(text, probability, noSpeech));

    [SpeechSampleFact]
    public async Task CancellingAnActiveNativeTranscriptAllowsTheNextRecognition()
    {
        var pcm = File.ReadAllBytes(Environment.GetEnvironmentVariable("TOBOO_SPEECH_TEST_PCM")!);
        I2sMicrophone.Normalize(pcm);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var segments = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WhisperPcmRecognizer.RecognizeAsync(pcm,
            cancel.Token, (_, _, _, _) => { segments++; cancel.Cancel(); }));
        Assert.True(segments > 0);
        var text = await WhisperPcmRecognizer.RecognizeAsync(pcm, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}

public sealed class SpeechSampleFactAttribute : FactAttribute
{
    public SpeechSampleFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("TOBOO_SPEECH_TEST_PCM")))
            Skip = "Set TOBOO_SPEECH_TEST_PCM to an ignored local 16kHz / 16-bit mono speech PCM sample.";
        else if (WhisperPcmRecognizer.UnavailableReason is { } reason) Skip = reason;
    }
}
