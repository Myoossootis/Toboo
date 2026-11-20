using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Toboo.Desktop.Services;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class DeepSeekSpeechTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; return send(request, cancellation); }
    }
    [Fact]
    public async Task ModulePcmIsSentOnceAsDocumentedWaveMultipart()
    {
        var pcm = new byte[32000]; pcm[100] = 55;
        var handler = new Handler(async (request, cancellation) =>
        {
            Assert.Equal("https://api.deepseek.com/v1/tools/asr", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-key", request.Headers.Authorization!.Parameter);
            Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellation);
            Assert.Contains("name=file", Encoding.ASCII.GetString(body).Replace("\"", ""));
            var offset = body.AsSpan().IndexOf("RIFF"u8);
            Assert.True(offset > 0);
            var wave = body.AsSpan(offset, 44 + pcm.Length).ToArray();
            Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)));
            Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34)));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)));
            Assert.Equal(pcm, wave[44..]);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"request_id\":\"fixture\",\"text\":\"今天测试语音\",\"duration_ms\":1000}", Encoding.UTF8, "application/json") };
        });
        using var client = new DeepSeekSpeechClient(handler);
        Assert.Equal("今天测试语音", await client.TranscribeAsync(pcm, "fixture-key", default));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task SilenceInvalidAudioAndCancellationDoNotUpload()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not upload."));
        using var client = new DeepSeekSpeechClient(handler);
        Assert.Equal("", await client.TranscribeAsync(new byte[32000], "fixture-key", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.TranscribeAsync(new byte[3201], "fixture-key", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.TranscribeAsync(new byte[16000 * 2 * 32], "fixture-key", default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TranscribeAsync(new byte[32000], "fixture-key", cancelled.Token));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task ServiceErrorsAreRedactedAndNeverAutomaticallyRetried()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("fixture-key server detail") }));
        using var client = new DeepSeekSpeechClient(handler);
        var pcm = new byte[32000]; pcm[10] = 5;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.TranscribeAsync(pcm, "fixture-key", default));
        Assert.Contains("手动重试", error.Message);
        Assert.DoesNotContain("fixture-key", error.Message);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task OversizedRepliesAreRejected()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) }));
        using var client = new DeepSeekSpeechClient(handler);
        var pcm = new byte[32000]; pcm[10] = 5;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.TranscribeAsync(pcm, "fixture-key", default));
    }
    [Fact]
    public async Task CorrectionUsesAnIsolatedTextRequestAndRequiresACompleteReply()
    {
        var handler = new Handler(async (request, cancellation) =>
        {
            Assert.Equal("https://api.deepseek.com/chat/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            Assert.Equal("deepseek-flash", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.TryGetProperty("tools", out _));
            Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            using var payload = JsonDocument.Parse(user!);
            Assert.Equal("不要打开摄像机根随", payload.RootElement.GetProperty("transcript").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = "{\"text\":\"不要打开摄像机跟随\"}" } } } })) };
        });
        using var client = new DeepSeekSpeechClient(handler);
        Assert.Equal("不要打开摄像机跟随", await client.CorrectAsync("不要打开摄像机根随", "fixture-key", default));
    }
    [Theory]
    [InlineData("不要打开摄像头", "打开摄像头", false)]
    [InlineData("转动5度", "转动50度", false)]
    [InlineData("不要打开摄像机根随", "不要打开摄像机跟随", true)]
    [InlineData("今天心晴不错", "今天心情不错", true)]
    public void CorrectionPreservesNegationAndNumbers(string original, string corrected, bool accepted)
        => Assert.Equal(accepted, TranscriptCorrection.IsConservative(original, corrected));

    [Fact]
    public void CredentialsAreEncryptedRoundTripAndCorruptionDoesNotBreakStartup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "toboo-asr-key-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "voice.local.json");
        var store = new SpeechRecognitionStore(path);
        store.Save(1, true, true, "fixture-key", source: 1, port: "COM6");
        Assert.DoesNotContain("fixture-key", File.ReadAllText(path));
        var restored = new SpeechRecognitionStore(path);
        Assert.Equal("fixture-key", restored.ReadKey());
        Assert.Equal(1, restored.Current.Source);
        Assert.Equal("COM6", restored.Current.Port);
        restored.Save(0, false, false, clearKey: true);
        Assert.Null(new SpeechRecognitionStore(path).ReadKey());
        File.WriteAllText(path, "broken-json");
        var broken = new SpeechRecognitionStore(path);
        Assert.NotNull(broken.LoadError);
        broken.Save(0, false, false);
        Assert.Equal("broken-json", File.ReadAllText(Directory.GetFiles(folder, "*.bak").Single()));
    }
}
