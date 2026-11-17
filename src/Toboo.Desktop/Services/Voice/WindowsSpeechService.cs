using System.Runtime.Versioning;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Speech.AudioFormat;
using System.Text;

namespace Toboo.Desktop.Services;

internal sealed record SpeechCapabilities(bool CanListen, bool CanSpeak, string Input, string Output,
    bool CanRecognizePcm, string PcmInput);

internal interface ISpeechService
{
    Task<SpeechCapabilities> InspectAsync(CancellationToken cancellation);
    Task<string> ListenAsync(Action started, CancellationToken cancellation);
    Task<string> RecognizePcmAsync(byte[] pcm, CancellationToken cancellation);
    void FinishListening();
    Task SpeakAsync(string text, Action<bool> playing, Action word, CancellationToken cancellation);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsSpeechService : ISpeechService
{
    private readonly object sync = new();
    private SpeechRecognitionEngine? listening;
    private bool finishRequested;

    public Task<SpeechCapabilities> InspectAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        var recognizer = ChineseRecognizer();
        using var synth = new SpeechSynthesizer();
        var voice = ChineseVoice(synth);
        return new SpeechCapabilities(recognizer is not null, voice is not null,
            recognizer is null ? "请在 Windows 添加中文语音识别语言包" : "中文识别 · 系统默认麦克风",
            voice is null ? "请在 Windows 添加中文朗读声音" : voice.VoiceInfo.Name + " · 系统默认音频输出",
            WhisperPcmRecognizer.UnavailableReason is null,
            WhisperPcmRecognizer.UnavailableReason ?? "ICS43432 · Whisper 本地中文识别");
    }, cancellation);

    public Task<string> ListenAsync(Action started, CancellationToken cancellation)
        => RecognizeAsync(started, cancellation, null);

    public Task<string> RecognizePcmAsync(byte[] pcm, CancellationToken cancellation)
        => WhisperPcmRecognizer.RecognizeAsync(pcm, cancellation);

    private Task<string> RecognizeAsync(Action started, CancellationToken cancellation, byte[]? pcm)
    {
        lock (sync) finishRequested = false;
        return Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            var info = ChineseRecognizer() ?? throw new InvalidOperationException("未安装中文语音识别语言包。");
            using var engine = new SpeechRecognitionEngine(info);
            using var audio = pcm is null ? null : new MemoryStream(pcm, writable: false);
            var complete = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var text = new StringBuilder();
            engine.LoadGrammar(new DictationGrammar());
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(30);
            engine.BabbleTimeout = TimeSpan.FromSeconds(15);
            engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(600);
            engine.SpeechRecognized += (_, args) =>
            {
                if (args.Result.Confidence < 0.35 || string.IsNullOrWhiteSpace(args.Result.Text)) return;
                lock (text)
                {
                    if (text.Length > 0) text.Append(' ');
                    text.Append(args.Result.Text);
                    if (text.Length > 4000) complete.TrySetException(new InvalidOperationException("这段语音太长，请分成几句说。"));
                }
            };
            engine.RecognizeCompleted += (_, args) =>
            {
                if (args.Error is not null) complete.TrySetException(args.Error);
                else
                {
                    lock (text) complete.TrySetResult(text.ToString().Trim());
                }
            };
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                lock (sync) if (finishRequested) return "";
                if (audio is null) engine.SetInputToDefaultAudioDevice();
                else engine.SetInputToAudioStream(audio, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                using var cancel = deadline.Token.Register(() =>
                {
                    lock (sync)
                    {
                        try { listening?.RecognizeAsyncCancel(); }
                        catch (InvalidOperationException) { /* Completion can win the cancellation race. */ }
                    }
                });
                lock (sync)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (finishRequested) return "";
                    listening = engine;
                    engine.RecognizeAsync(RecognizeMode.Multiple);
                }
                started();
                return await complete.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("语音识别超时，请重试。");
            }
            finally
            {
                lock (sync) listening = null;
            }
        }, cancellation);
    }

    public void FinishListening()
    {
        lock (sync)
        {
            finishRequested = true;
            listening?.RecognizeAsyncStop();
        }
    }

    public Task SpeakAsync(string text, Action<bool> playing, Action word, CancellationToken cancellation) =>
        SynthesizeAsync(text, playing, word, cancellation, null);

    private static Task SynthesizeAsync(string text, Action<bool> playing, Action word,
        CancellationToken cancellation, Stream? wave) => Task.Run(async () =>
    {
        using var synth = new SpeechSynthesizer();
        var voice = ChineseVoice(synth) ?? throw new InvalidOperationException("未安装中文朗读声音。");
        synth.SelectVoice(voice.VoiceInfo.Name);
        if (wave is null) synth.SetOutputToDefaultAudioDevice();
        else synth.SetOutputToAudioStream(wave, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        synth.SpeakStarted += (_, _) => playing(true);
        synth.SpeakProgress += (_, _) => word();
        synth.SpeakCompleted += (_, args) =>
        {
            if (args.Error is not null) complete.TrySetException(args.Error);
            else complete.TrySetResult();
        };
        using var cancel = cancellation.Register(synth.SpeakAsyncCancelAll);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            synth.SpeakAsync(text);
            await complete.Task.WaitAsync(cancellation).ConfigureAwait(false);
        }
        finally { playing(false); }
    }, cancellation);

    private static RecognizerInfo? ChineseRecognizer() => SpeechRecognitionEngine.InstalledRecognizers()
        .FirstOrDefault(info => info.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase));

    private static InstalledVoice? ChineseVoice(SpeechSynthesizer synth) => synth.GetInstalledVoices()
        .FirstOrDefault(voice => voice.Enabled && voice.VoiceInfo.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase));

    internal static Task<object> SelfTestAsync() => Task.Run<object>(async () =>
    {
        var info = ChineseRecognizer() ?? throw new InvalidOperationException("未安装中文识别引擎。");
        using var audio = new MemoryStream();
        var starts = 0;
        var words = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await SynthesizeAsync("你好，今天的天气很好，我们一起聊聊天。",
            playing => { if (playing) Interlocked.Increment(ref starts); },
            () => Interlocked.Increment(ref words), deadline.Token, audio);
        if (starts == 0 || words == 0) throw new InvalidOperationException("朗读播放状态事件没有触发。");
        audio.Position = 0;
        using var engine = new SpeechRecognitionEngine(info);
        engine.LoadGrammar(new DictationGrammar());
        engine.SetInputToAudioStream(audio, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        var result = engine.Recognize(TimeSpan.FromSeconds(15));
        if (result is null || string.IsNullOrWhiteSpace(result.Text))
            throw new InvalidOperationException("合成语音的本地识别自检没有返回文字。");
        var pcm = audio.ToArray();
        I2sMicrophone.Normalize(pcm);
        var moduleTranscript = await new WindowsSpeechService().RecognizePcmAsync(pcm, deadline.Token);
        if (string.IsNullOrWhiteSpace(moduleTranscript)) throw new InvalidOperationException("模块 PCM 识别入口自检失败。");
        return new { success = true, source = "synthetic-chinese-speech", recognizer = info.Id,
            recognized = result.Text, confidence = result.Confidence, pcmBytes = audio.Length, playbackStarts = starts, wordEvents = words,
            modulePcmRecognized = moduleTranscript,
            microphoneUsed = false, speakerUsed = false, modelRequestsSent = false };
    });
}
