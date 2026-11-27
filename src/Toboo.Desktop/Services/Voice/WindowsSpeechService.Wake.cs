using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Recognition.SrgsGrammar;
using System.Speech.Synthesis;

namespace Toboo.Desktop.Services;

internal interface IVoiceWakeService
{
    const string Phrase = "hi toboo";
    const int ReplyWaitSeconds = 12;
    Task<bool> WaitForWakeAsync(Action ready, CancellationToken cancellation, Stream? audio = null, Action<double>? level = null);
    Task<string> ListenOnceAsync(Action ready, CancellationToken cancellation, Stream? audio = null, Action<double>? level = null,
        Action? recognizing = null);
}

internal sealed partial class WindowsSpeechService
{
    private const float WakeConfidence = 0.38f;
    private static readonly SpeechAudioFormatInfo WakeAudioFormat = new(WindowsWakeMicrophone.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

    private static RecognizerInfo? EnglishWakeRecognizer() => SpeechRecognitionEngine.InstalledRecognizers()
        .Where(info => info.Culture.TwoLetterISOLanguageName == "en")
        .OrderByDescending(info => info.Culture.Name == "en-US")
        .FirstOrDefault();

    public Task<bool> WaitForWakeAsync(Action ready, CancellationToken cancellation, Stream? audio = null, Action<double>? level = null) =>
        audio is null ? StreamingSpeechInput.RunAsync(WindowsWakeMicrophone.StreamAsync, SelectWakeAsync, ready, cancellation, level)
            : SelectWakeAsync(ready, cancellation, audio);

    public Task<string> ListenOnceAsync(Action ready, CancellationToken cancellation, Stream? audio = null, Action<double>? level = null,
        Action? recognizing = null)
    {
        Func<Action, CancellationToken, Stream, Task<string>> recognize = WhisperPcmRecognizer.UnavailableReason is null
            ? (started, token, stream) => WhisperLiveSpeech.ListenOnceAsync(started, token, stream, recognizing)
            : DictateOnceAsync;
        return audio is null ? StreamingSpeechInput.RunAsync(WindowsWakeMicrophone.StreamAsync, recognize, ready, cancellation, level)
            : recognize(ready, cancellation, audio);
    }

    private static Task<bool> SelectWakeAsync(Action ready, CancellationToken cancellation, Stream audio) =>
        Task.Run(async () =>
        {
            if (WhisperPcmRecognizer.UnavailableReason is not null)
                return await RecognizeWakeAsync(ready, cancellation, audio).ConfigureAwait(false) is not null;
            var english = EnglishWakeRecognizer();
            if (english is null) return await WhisperLiveSpeech.WaitForWakeAsync(ready, cancellation, audio).ConfigureAwait(false);
            var match = await StreamingSpeechInput.FirstMatchAsync<string>(audio,
                async (started, token, input) => await WhisperLiveSpeech.WaitForWakeAsync(started, token, input).ConfigureAwait(false)
                    ? IVoiceWakeService.Phrase : null,
                async (started, token, input) => (await ScanWakeAsync(english, EnglishWakeGrammar, started, token, input)
                    .ConfigureAwait(false))?.Text, ready, cancellation).ConfigureAwait(false);
            return match is not null;
        }, cancellation);

    private static Task<RecognitionResult?> RecognizeWakeAsync(Action ready, CancellationToken cancellation, Stream audio) =>
        Task.Run(() =>
        {
            var chinese = ChineseRecognizer() ?? throw new InvalidOperationException("未安装中文语音识别语言包。");
            var english = EnglishWakeRecognizer();
            return english is null ? ScanWakeAsync(chinese, ChineseWakeGrammar, ready, cancellation, audio)
                : StreamingSpeechInput.FirstMatchAsync(audio,
                    (started, token, input) => ScanWakeAsync(chinese, ChineseWakeGrammar, started, token, input),
                    (started, token, input) => ScanWakeAsync(english, EnglishWakeGrammar, started, token, input), ready, cancellation);
        }, cancellation);

    private static Grammar ChineseWakeGrammar(RecognizerInfo info)
    {
        string[] greetings = ["你好", "哈喽", "哈啰"];
        string[] names = ["托布", "托波", "偷波", "图布", "偷包"];
        var pairs = from greeting in greetings from name in names select greeting + name;
        return new Grammar(new GrammarBuilder(new Choices(greetings.Concat(names).Concat(pairs).ToArray()))
            { Culture = info.Culture }) { Name = "TobooWakeZh" };
    }

    private static Grammar EnglishWakeGrammar(RecognizerInfo info)
    {
        // The brand is a fixed token with the pronunciation variants used in conversation.
        SrgsToken Brand() => new("toboo") { Pronunciation = "tobu;tubu;tobo;toʊboʊ;təʊbəʊ;toʊbaʊ" };
        var phrases = new SrgsOneOf(new SrgsItem(new SrgsToken("hey")), new SrgsItem(new SrgsToken("hello")),
            new SrgsItem(Brand()), new SrgsItem(new SrgsToken("hi"), Brand()),
            new SrgsItem(new SrgsToken("hey"), Brand()), new SrgsItem(new SrgsToken("hello"), Brand()));
        return new Grammar(new SrgsDocument(new SrgsRule("TobooWake", phrases))
            { Culture = info.Culture, PhoneticAlphabet = SrgsPhoneticAlphabet.Ipa }) { Name = "TobooWakeEn" };
    }

    private static Task<RecognitionResult?> ScanWakeAsync(RecognizerInfo info, Func<RecognizerInfo, Grammar> createWake,
        Action ready, CancellationToken cancellation, Stream audio) =>
        Task.Run(async () =>
        {
            using var engine = new SpeechRecognitionEngine(info);
            var wake = createWake(info);
            engine.LoadGrammar(wake);
            // The Chinese engine can otherwise force a short keyword match on unrelated sentences.
            if (info.Culture.TwoLetterISOLanguageName == "zh")
                engine.LoadGrammar(new DictationGrammar() { Weight = 0.20f });
            engine.InitialSilenceTimeout = TimeSpan.Zero;
            engine.BabbleTimeout = TimeSpan.Zero;
            engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(500);
            var detected = new TaskCompletionSource<RecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.SpeechRecognized += OnSpeech;
            void OnSpeech(object? sender, SpeechRecognizedEventArgs args)
            {
                var result = args.Result;
                if (ReferenceEquals(result.Grammar, wake) && result.Confidence >= WakeConfidence)
                    detected.TrySetResult(result);
            }

            var completed = StartRecognition(engine, audio, RecognizeMode.Multiple, cancellation);
            try
            {
                ready();
                await Task.WhenAny((Task)detected.Task, completed).WaitAsync(cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return detected.Task.IsCompletedSuccessfully ? detected.Task.Result
                    : await completed.ConfigureAwait(false);
            }
            finally { CancelRecognition(engine, audio); }
        }, cancellation);

    private static Task<string> DictateOnceAsync(Action ready, CancellationToken cancellation, Stream audio) =>
        Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(27));
            var info = ChineseRecognizer() ?? throw new InvalidOperationException("未安装中文语音识别语言包。");
            using var engine = new SpeechRecognitionEngine(info);
            engine.LoadGrammar(new DictationGrammar());
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(IVoiceWakeService.ReplyWaitSeconds);
            engine.BabbleTimeout = TimeSpan.FromSeconds(5);
            engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(850);
            engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(1000);
            var completed = StartRecognition(engine, audio, RecognizeMode.Single, deadline.Token);
            try
            {
                ready();
                var result = await completed.WaitAsync(deadline.Token).ConfigureAwait(false);
                // The user reviews the draft, so keep plausible low-confidence transcriptions.
                return result is { Confidence: >= 0.30f } ? result.Text : "";
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return ""; }
            finally { CancelRecognition(engine, audio); }
        }, cancellation);

    private static Task<RecognitionResult?> StartRecognition(SpeechRecognitionEngine engine, Stream audio,
        RecognizeMode mode, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        engine.SetInputToAudioStream(audio, WakeAudioFormat);
        var completed = new TaskCompletionSource<RecognitionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.RecognizeCompleted += (_, args) =>
        {
            if (args.Error is not null) completed.TrySetException(args.Error);
            else if (args.Cancelled) completed.TrySetCanceled(cancellation);
            else completed.TrySetResult(mode == RecognizeMode.Single ? args.Result : null);
        };
        cancellation.ThrowIfCancellationRequested();
        engine.RecognizeAsync(mode);
        return completed.Task;
    }

    private static void CancelRecognition(SpeechRecognitionEngine engine, Stream audio)
    {
        // Closing a streaming source unblocks any pending native read before cancellation.
        audio.Close();
        try { engine.RecognizeAsyncCancel(); }
        catch (InvalidOperationException) { /* The recognizer may have already completed. */ }
    }

    internal static async Task<(bool Success, object Report)> WakeSelfTestAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var english = new SpeechSynthesizer();
        var englishVoice = english.GetInstalledVoices().Where(voice => voice.Enabled && voice.VoiceInfo.Culture.TwoLetterISOLanguageName == "en")
            .OrderByDescending(voice => voice.VoiceInfo.Name.Contains("Zira", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault() ?? throw new InvalidOperationException("唤醒自检需要 Windows 英语朗读声音。");
        english.SelectVoice(englishVoice.VoiceInfo.Name);
        async Task<MemoryStream> SampleAsync(string phrase, bool inEnglish, int leadingSilenceBytes = 16000)
        {
            using var spoken = new MemoryStream();
            if (inEnglish)
            {
                english.SetOutputToAudioStream(spoken, WakeAudioFormat);
                try { english.Speak(phrase); }
                finally { english.SetOutputToNull(); }
            }
            else await SynthesizeAsync(phrase, _ => { }, () => { }, deadline.Token, spoken).ConfigureAwait(false);
            // Give the recognition engine time to close the utterance before end of stream.
            var pcm = new MemoryStream();
            pcm.Write(new byte[leadingSilenceBytes]);
            spoken.Position = 0;
            spoken.CopyTo(pcm);
            pcm.Write(new byte[48000]);
            pcm.Position = 0;
            return pcm;
        }

        // Use known English words to synthesize the name without the synthesizer guessing its pronunciation.
        (string Text, bool InEnglish, bool Expected)[] cases = [("hi toe boo", true, true), ("hi too boo", true, true),
            ("hi toe beau", true, true), ("toe beau", true, true), ("hey", true, true), ("hello", true, true),
            ("hello toe boo", true, true), ("你好", false, true), ("你好，托布", false, true), ("托波", false, true),
            ("hi to you", true, false), ("the weather is nice today", true, false),
            ("请给我讲一个故事", false, false), ("打开摄像机跟随", false, false)];
        var results = new List<object>();
        var success = true;
        foreach (var (phrase, inEnglish, expected) in cases)
        {
            var leadingSilenceBytes = phrase == "hi toe boo" ? 35 * 32000 : 16000;
            using var sample = await SampleAsync(phrase, inEnglish, leadingSilenceBytes).ConfigureAwait(false);
            var result = await RecognizeWakeAsync(() => { }, deadline.Token, sample).ConfigureAwait(false);
            success &= (result is not null) == expected;
            results.Add(new { phrase, expected, triggered = result is not null, recognized = result?.Text,
                grammar = result?.Grammar?.Name, confidence = result?.Confidence });
        }
        using var utterance = await SampleAsync("请给我讲一个故事", inEnglish: false).ConfigureAwait(false);
        var transcript = await DictateOnceAsync(() => { }, deadline.Token, utterance).ConfigureAwait(false);
        using var silence = new MemoryStream(new byte[7 * 32000]);
        var silenceTranscript = await DictateOnceAsync(() => { }, deadline.Token, silence).ConfigureAwait(false);
        using var modulePhrase = await SampleAsync("hi toe boo", inEnglish: true).ConfigureAwait(false);
        var module = new ModuleWakeSpeech(new SyntheticWakeMicrophone(32, modulePhrase.ToArray(), utterance.ToArray()), new WindowsSpeechService());
        var moduleWakeDetected = await module.WaitForWakeAsync("synthetic", () => { }, deadline.Token).ConfigureAwait(false);
        var moduleTranscript = await module.ListenOnceAsync("synthetic", () => { }, deadline.Token).ConfigureAwait(false);
        using var quietPhrase = await SampleAsync("hi toe beau", inEnglish: true).ConfigureAwait(false);
        var quietInput = new SyntheticWakeMicrophone(64, quietPhrase.ToArray(), utterance.ToArray());
        Task QuietStream(Action<byte[]> samples, CancellationToken cancellation) =>
            quietInput.StreamAsync("synthetic", samples, cancellation);
        var quietWake = await StreamingSpeechInput.RunAsync(QuietStream, RecognizeWakeAsync, () => { }, deadline.Token).ConfigureAwait(false);
        var quietTranscript = await StreamingSpeechInput.RunAsync(QuietStream, DictateOnceAsync, () => { }, deadline.Token).ConfigureAwait(false);
        bool? whisperGreeting = null;
        bool? whisperRejectsOrdinarySpeech = null;
        string? whisperDraft = null;
        if (WhisperPcmRecognizer.UnavailableReason is null)
        {
            using var greeting = await SampleAsync("你好", inEnglish: false).ConfigureAwait(false);
            using var ordinary = await SampleAsync("今天我们测试麦克风", inEnglish: false).ConfigureAwait(false);
            using var draft = await SampleAsync("请给我讲一个故事", inEnglish: false).ConfigureAwait(false);
            whisperGreeting = await WhisperLiveSpeech.WaitForWakeAsync(() => { }, deadline.Token, greeting).ConfigureAwait(false);
            whisperRejectsOrdinarySpeech = !await WhisperLiveSpeech.WaitForWakeAsync(() => { }, deadline.Token, ordinary).ConfigureAwait(false);
            whisperDraft = await WhisperLiveSpeech.ListenOnceAsync(() => { }, deadline.Token, draft).ConfigureAwait(false);
            success &= whisperGreeting == true && whisperRejectsOrdinarySpeech == true
                && whisperDraft.Contains("故事", StringComparison.Ordinal);
        }
        success &= transcript.Contains("故事", StringComparison.Ordinal) && silenceTranscript.Length == 0
            && moduleWakeDetected && moduleTranscript.Contains("故事", StringComparison.Ordinal)
            && quietWake is not null && quietTranscript.Contains("故事", StringComparison.Ordinal);
        return (success, new { success, source = "synthetic-chinese-english-wake-and-chinese-dictation", wakePhrase = IVoiceWakeService.Phrase,
            englishVoice = englishVoice.VoiceInfo.Name,
            results, transcript, silenceTranscript, moduleWakeDetected, moduleTranscript,
            quietWakeDetected = quietWake is not null, quietWakeConfidence = quietWake?.Confidence, quietTranscript,
            whisperGreeting, whisperRejectsOrdinarySpeech, whisperDraft,
            microphoneUsed = false, modelRequestsSent = false });
    }

    private sealed class SyntheticWakeMicrophone(int attenuation, params byte[][] recordings) : IStreamingMicrophoneCapture
    {
        private int next;
        public async Task StreamAsync(string port, Action<byte[]> samples, CancellationToken cancellation)
        {
            var pcm = recordings[next++];
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            for (var offset = 0; ;)
            {
                await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false);
                for (var frameIndex = 0; frameIndex < 2; frameIndex++)
                {
                    var frame = new byte[256];
                    for (var index = 0; index < frame.Length; index += 2)
                    {
                        var sample = offset < pcm.Length ? System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset, 2)) : 0;
                        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(index, 2), (short)(sample / attenuation + 90));
                        offset = Math.Min(pcm.Length, offset + 2);
                    }
                    samples(frame);
                }
            }
        }
    }
}
