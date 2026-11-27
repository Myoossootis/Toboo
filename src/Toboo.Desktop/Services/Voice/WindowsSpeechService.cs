using System.Diagnostics;
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
    Task<string> ListenAsync(Action started, CancellationToken cancellation, Action<double>? level = null);
    Task<string> RecognizePcmAsync(byte[] pcm, CancellationToken cancellation);
    void FinishListening();
    Task SpeakAsync(string text, Action<bool> playing, Action word, CancellationToken cancellation);
}

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsSpeechService : ISpeechService, IVoiceWakeService
{
    private readonly object sync = new();
    private SpeechRecognitionEngine? listening;
    private CancellationTokenSource? pcmRecording;
    private bool finishRequested;

    public Task<SpeechCapabilities> InspectAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        var recognizer = ChineseRecognizer();
        using var synth = new SpeechSynthesizer();
        var voice = ChineseVoice(synth);
        var localWhisper = WhisperPcmRecognizer.UnavailableReason is null;
        var canListen = localWhisper || recognizer is not null;
        var microphone = canListen ? WindowsWakeMicrophone.DefaultInputName() : null;
        var input = canListen ? $"{(localWhisper ? "Whisper 本地中文识别" : "Windows 中文识别")} · {microphone ?? "系统默认麦克风"}"
            : "请准备本地语音模型或安装 Windows 中文语音识别组件";
        return new SpeechCapabilities(canListen, voice is not null, input,
            voice is null ? "请在 Windows 添加中文朗读声音" : voice.VoiceInfo.Name + " · 系统默认音频输出",
            localWhisper,
            WhisperPcmRecognizer.UnavailableReason ?? "ICS43432 · Whisper 本地中文识别");
    }, cancellation);

    public Task<string> ListenAsync(Action started, CancellationToken cancellation, Action<double>? level = null)
        => WhisperPcmRecognizer.UnavailableReason is null ? RecordWithWhisperAsync(started, cancellation, level: level)
            : RecognizeAsync(started, cancellation, level);

    public Task<string> RecognizePcmAsync(byte[] pcm, CancellationToken cancellation)
        => WhisperPcmRecognizer.RecognizeAsync(pcm, cancellation);

    internal Task<string> RecordWithWhisperAsync(Action started, CancellationToken cancellation, Action<byte[]>? observe = null,
        Action<double>? level = null)
    {
        lock (sync) finishRequested = false;
        return Task.Run(async () =>
        {
            const int maximumBytes = WindowsWakeMicrophone.SampleRate * sizeof(short) * 30;
            using var capture = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            using var buffer = new MemoryStream();
            capture.CancelAfter(TimeSpan.FromSeconds(30));
            lock (sync)
            {
                if (finishRequested) return "";
                pcmRecording = capture;
            }
            try
            {
                var ready = false;
                var lastMeter = Stopwatch.GetTimestamp();
                var peak = 0d;
                try
                {
                    await WindowsWakeMicrophone.StreamAsync(frame =>
                    {
                        var room = maximumBytes - (int)buffer.Length;
                        buffer.Write(frame, 0, Math.Min(room, frame.Length));
                        if (!ready)
                        {
                            ready = true;
                            started();
                        }
                        if (level is not null)
                        {
                            peak = Math.Max(peak, I2sMicrophone.Peak(frame));
                            var now = Stopwatch.GetTimestamp();
                            if (Stopwatch.GetElapsedTime(lastMeter, now) >= TimeSpan.FromMilliseconds(100))
                            {
                                level(I2sMicrophone.MeterLevel(peak));
                                lastMeter = now;
                                peak = 0;
                            }
                        }
                        if (buffer.Length == maximumBytes) capture.Cancel();
                        Array.Clear(frame);
                    }, capture.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (capture.IsCancellationRequested && !cancellation.IsCancellationRequested) { }
                cancellation.ThrowIfCancellationRequested();
                if (buffer.Length < 3200) return "";
                var pcm = buffer.ToArray();
                try
                {
                    observe?.Invoke(pcm);
                    I2sMicrophone.Normalize(pcm);
                    return await WhisperPcmRecognizer.RecognizeAsync(pcm, cancellation).ConfigureAwait(false);
                }
                finally { Array.Clear(pcm); }
            }
            finally
            {
                lock (sync) if (ReferenceEquals(pcmRecording, capture)) pcmRecording = null;
                if (buffer.TryGetBuffer(out var segment)) segment.AsSpan().Clear();
            }
        }, cancellation);
    }

    private Task<string> RecognizeAsync(Action started, CancellationToken cancellation, Action<double>? level)
    {
        lock (sync) finishRequested = false;
        return Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            var info = ChineseRecognizer() ?? throw new InvalidOperationException("未安装中文语音识别语言包。");
            using var engine = new SpeechRecognitionEngine(info);
            var complete = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var text = new StringBuilder();
            engine.LoadGrammar(new DictationGrammar());
            engine.LoadGrammar(new Grammar(new GrammarBuilder("打开摄像机跟随") { Culture = info.Culture })
                { Name = "TobooFollowCommand" });
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(30);
            engine.BabbleTimeout = TimeSpan.FromSeconds(15);
            engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(600);
            if (level is not null) engine.AudioLevelUpdated += (_, args) => level(args.AudioLevel / 100d);
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
                engine.SetInputToDefaultAudioDevice();
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
            pcmRecording?.Cancel();
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
