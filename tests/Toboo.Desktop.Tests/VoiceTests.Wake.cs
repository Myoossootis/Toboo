using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Toboo.Desktop.Services;
using Toboo.Desktop.ViewModels;
using Toboo.Desktop.Views;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed partial class VoiceTests
{
    [Theory]
    [InlineData("Hey, Tobo!", true)]
    [InlineData("你好，托波。", true)]
    [InlineData("Toubow.", true)]
    [InlineData("Hello everyone!", false)]
    [InlineData("请给我讲一个故事。", false)]
    [InlineData("打开摄像机跟随", false)]
    public void WhisperWakeRequiresAWholeGreetingOrName(string text, bool expected) =>
        Assert.Equal(expected, WhisperLiveSpeech.IsWakePhrase(text));

    [Theory]
    [InlineData("帮我关掉语音唤醒功能。", "DisableWake")]
    [InlineData("请暂停语音对话吧。", "EndConversation")]
    [InlineData("麻烦，打开摄像头跟踪一下。", "StartFollow")]
    [InlineData("停止跟随", "StopFollow")]
    [InlineData("可以告诉我怎么开启人脸跟踪吗", "None")]
    [InlineData("不要打开摄像头跟踪", "None")]
    [InlineData("「打开摄像头跟踪」", "None")]
    [InlineData("打开摄像头看看", "None")]
    public void LocalCommandsMatchWholeRequestsWithCommonVariants(string text, string expected) =>
        Assert.Equal(expected, VoiceCommands.Match(text).ToString());

    [AvaloniaFact]
    public async Task WakeIsOptInAndRoutesTrackingLocally()
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var wake = new WakeSpeech();
        var cloud = new CloudSpeech();
        using var voice = new ViewModels.VoiceViewModel(chat, new Speech(), recognitionStore: CloudOptions(), cloudSpeech: cloud, wakeSpeech: wake);
        var commands = 0;
        voice.LocalCommandHandler = (_, _) => { commands++; return Task.FromResult<string?>("started"); };
        await chat.ConnectAsync();
        await voice.InitializeAsync();
        Assert.Empty(wake.Waits);
        Assert.True(voice.ToggleWakeCommand.CanExecute(null));
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1 && voice.WakeStatus.Contains("hi toboo"));
        Assert.Contains("hi toboo", voice.WakeHint);
        Assert.True(voice.ShowInputMeter);
        wake.Waits[0].Level!(0.25);
        await Until(() => voice.InputMeterLevel == 25);
        Assert.False(voice.IsListening);
        Assert.False(chat.VoiceInputActive);

        wake.Waits[0].Result.SetResult("");
        await Until(() => wake.Captures.Count == 1 && voice.WakeStatus.Contains("正在听"));
        Assert.Equal(0, voice.InputMeterLevel);
        wake.Waits[0].Level!(0.9);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, voice.InputMeterLevel);
        wake.Captures[0].Level!(0.5);
        await Until(() => voice.ShowInputMeter && voice.InputMeterLevel == 50);
        Assert.Contains("关闭", voice.WakeButtonLabel);
        Assert.False(voice.CanUseRecordButton);
        Assert.False(chat.VoiceInputActive);
        Assert.True(chat.CanEditDraft);
        wake.Captures[0].Recognizing!();
        await Until(() => !voice.ShowInputMeter);
        Assert.Contains("识别", voice.VoiceActivityMessage);
        wake.Captures[0].Level!(0.8);
        Dispatcher.UIThread.RunJobs();
        Assert.False(voice.ShowInputMeter);
        Assert.Equal(0, voice.InputMeterLevel);
        wake.Captures[0].Result.SetResult("请打开摄像头跟踪。");
        await Until(() => commands == 1 && wake.Captures.Count == 2);

        Assert.Empty(chat.Draft);
        Assert.True(chat.CanEditDraft);
        Assert.Equal(0, client.Sends);
        Assert.Equal(0, cloud.Transcriptions);
        Assert.Equal(0, cloud.Corrections);
        Assert.Single(wake.Waits);
        await voice.CloseWakeAsync();
    }

    [AvaloniaFact]
    public async Task UnavailableWakeLeavesChinesePushToTalkAvailable()
    {
        using var chat = Chat(new TobooChatTests.FakeClient());
        using var voice = NewVoice(chat, new Speech());
        await chat.ConnectAsync();
        await voice.InitializeAsync();

        Assert.True(voice.CanRecord);
        Assert.False(voice.ToggleWakeCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ManualRecordingWaitsForKeywordRecognizerToReleaseTheMicrophone()
    {
        using var chat = Chat(new TobooChatTests.FakeClient());
        var wake = new WakeSpeech { HoldKeywordOnCancellation = true };
        var speech = new Speech();
        using var voice = NewVoice(chat, speech, wakeSpeech: wake);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1 && voice.ShowInputMeter);
        wake.Waits[0].Level!(0.6);
        await Until(() => voice.InputMeterLevel == 60);
        var recording = voice.BeginListeningAsync();
        Assert.True(wake.Waits[0].Cancellation.IsCancellationRequested);
        Assert.Equal(0, speech.Listens);
        Assert.False(voice.ShowInputMeter);
        Assert.Equal(0, voice.InputMeterLevel);

        wake.HoldKeywordOnCancellation = false;
        wake.Waits[0].Result.SetResult("");
        await Until(() => speech.Listens == 1 && voice.ShowInputMeter);
        speech.InputLevel!(0.5);
        await Until(() => voice.InputMeterLevel == 50);
        wake.Waits[0].Level!(0.9);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(50, voice.InputMeterLevel);
        Assert.Empty(wake.Captures);
        voice.Stop();
        Assert.False(voice.ShowInputMeter);
        Assert.Equal(0, voice.InputMeterLevel);
        await recording;
        await Until(() => wake.Waits.Count == 2 && voice.ShowInputMeter);
        wake.Waits[1].Level!(0.25);
        await Until(() => voice.InputMeterLevel == 25);
        speech.InputLevel!(0.9);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(25, voice.InputMeterLevel);
        await voice.CloseWakeAsync();
        Assert.False(voice.ShowInputMeter);
        Assert.Equal(0, voice.InputMeterLevel);
    }

    [AvaloniaFact]
    public async Task ClosingWakeCancelsDictationAndDiscardsItsLateResult()
    {
        using var chat = Chat(new TobooChatTests.FakeClient());
        var wake = new WakeSpeech { HoldCaptureOnCancellation = true };
        using var voice = NewVoice(chat, new Speech(), wakeSpeech: wake);
        await voice.InitializeAsync();
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1);
        wake.Waits[0].Result.SetResult("");
        await Until(() => wake.Captures.Count == 1);

        var drain = voice.CloseWakeAsync();
        Assert.True(wake.Captures[0].Cancellation.IsCancellationRequested);
        Assert.False(drain.IsCompleted);
        wake.Captures[0].Result.SetResult("迟到的转写");
        await drain;
        Assert.False(chat.VoiceInputActive);
        Assert.Empty(chat.Draft);
        Assert.Empty(voice.OriginalTranscript);
        Assert.Single(wake.Waits);
    }

    [AvaloniaFact]
    public async Task WakeRearmsAfterSilenceAndPausesForPlayback()
    {
        using var chat = Chat(new TobooChatTests.FakeClient());
        var wake = new WakeSpeech();
        var speech = new Speech();
        using var voice = NewVoice(chat, speech, wakeSpeech: wake);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1);
        wake.Waits[0].Result.SetResult("");
        await Until(() => wake.Captures.Count == 1);
        wake.Captures[0].Result.SetResult(" ");
        await Until(() => wake.Waits.Count == 2);
        Assert.Empty(chat.Draft);

        chat.Draft = "你好";
        await chat.SendAsync();
        await Until(() => voice.IsSpeaking);
        Assert.True(wake.Waits[1].Cancellation.IsCancellationRequested);
        Assert.Equal(2, wake.Waits.Count);
        speech.Playback.SetResult();
        await Until(() => wake.Waits.Count == 3);

        await voice.CloseWakeAsync();
        Assert.True(wake.Waits[2].Cancellation.IsCancellationRequested);
        Assert.Equal("开启语音唤醒", voice.WakeButtonLabel);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneWakeStartsTwoTurnsWithPlaybackIsolationAndAnExplicitEnd(bool readReplies)
    {
        var client = new TobooChatTests.FakeClient { StreamReply = true };
        using var chat = Chat(client);
        var speech = new Speech();
        var wake = new WakeSpeech();
        using var voice = NewVoice(chat, speech, wakeSpeech: wake);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        chat.ReadReplies = readReplies;
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1);
        wake.Waits[0].Result.SetResult("");
        await Until(() => wake.Captures.Count == 1);
        wake.Captures[0].Result.SetResult("讲个故事。");
        await Until(() => chat.RemoteRunning && client.Sends == 1);
        Assert.Empty(chat.Draft);
        Assert.Single(wake.Captures);

        client.Turns[0] = client.Turns[0] with { State = "completed", Reply = "从前有一只小猫。" };
        await chat.RefreshAsync();
        if (readReplies)
        {
            await Until(() => voice.IsSpeaking);
            Assert.Single(wake.Captures);
            speech.Playback.SetResult();
        }
        await Until(() => wake.Captures.Count == 2);
        Assert.Single(wake.Waits);
        wake.Captures[1].Result.SetResult("不要关闭语音唤醒。");
        await Until(() => client.Sends == 2 && chat.RemoteRunning);
        Assert.Equal("不要关闭语音唤醒。", client.Turns[1].Text);
        client.Turns[1] = client.Turns[1] with { State = "completed", Reply = "" };
        await chat.RefreshAsync();
        await Until(() => wake.Captures.Count == 3);
        wake.Captures[2].Result.SetResult("结束语音对话。");
        await Until(() => wake.Waits.Count == 2);
        Assert.Equal(2, client.Sends);
        Assert.Empty(chat.Draft);
        await voice.CloseWakeAsync();
    }

    [AvaloniaFact]
    public async Task TypingKeepsTheComposerAndResumesAfterClearingWithoutRepeatingWake()
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var wake = new WakeSpeech { HoldCaptureOnCancellation = true };
        using var voice = NewVoice(chat, new Speech(), wakeSpeech: wake);
        using var tracking = new MainViewModel();
        var window = new MainWindow(tracking, chat, voice);
        try
        {
            await chat.ConnectAsync(); await voice.InitializeAsync();
            window.Show();
            voice.ToggleWakeCommand.Execute(null);
            await Until(() => wake.Waits.Count == 1);
            wake.Waits[0].Result.SetResult("");
            await Until(() => wake.Captures.Count == 1 && voice.ShowInputMeter);
            var home = window.FindControl<CompanionHomeView>("HomeView")!;
            var composer = home.FindControl<TextBox>("ConversationInput")!;
            var meter = home.FindControl<Control>("VoiceStandbyMeter")!;
            Assert.True(composer.IsVisible && composer.IsEnabled && meter.IsVisible);
            composer.Text = "自己输入的内容";
            await Until(() => wake.Captures[0].Cancellation.IsCancellationRequested);
            wake.HoldCaptureOnCancellation = false;
            wake.Captures[0].Result.SetResult("迟到结果不应替换输入");
            await Until(() => !voice.IsListening);
            Assert.Equal("自己输入的内容", chat.Draft);
            Assert.True(meter.IsVisible);
            Assert.Equal(0, voice.InputMeterLevel);
            Assert.Single(wake.Captures);
            Assert.Equal(0, client.Sends);
            composer.Text = "";
            await Until(() => wake.Captures.Count == 2);
            Assert.True(meter.IsVisible);
            Assert.Single(wake.Waits);
            wake.Captures[1].Result.SetResult("关闭语音唤醒。");
            await Until(() => !voice.ShowWakeMeter);
            Assert.False(meter.IsVisible);
            Assert.Empty(chat.Draft);
            Assert.Equal(0, client.Sends);
            await voice.CloseWakeAsync();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task UncertainVoiceSendPreservesDraftAndPausesWithoutRetry()
    {
        var client = new TobooChatTests.FakeClient { LoseAcknowledgement = true };
        using var chat = Chat(client);
        var wake = new WakeSpeech();
        using var voice = NewVoice(chat, new Speech(), wakeSpeech: wake);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1);
        wake.Waits[0].Result.SetResult("");
        await Until(() => wake.Captures.Count == 1);
        wake.Captures[0].Result.SetResult("测试一条语音消息");
        await Until(() => !chat.IsBusy && chat.HasPending && !voice.IsListening);
        Assert.Equal("测试一条语音消息", chat.Draft);
        Assert.Equal(1, client.Sends);
        Assert.Single(wake.Captures);
        Assert.Single(wake.Waits);
        await voice.CloseWakeAsync();
    }

    [AvaloniaFact]
    public async Task ModuleWakeUsesTheSelectedPortForBothStagesAndKeepsCloudAsrOutOfTheDraftPath()
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var wake = new WakeSpeech();
        var microphone = new StreamingMicrophone();
        var cloud = new CloudSpeech();
        using var voice = new ViewModels.VoiceViewModel(chat, new Speech(), microphone, CloudOptions(), cloud, wake);
        await voice.InitializeAsync();
        voice.MicrophoneSourceIndex = 1;
        Assert.False(voice.ToggleWakeCommand.CanExecute(null));
        Assert.Contains("串口", voice.WakeStatus);
        voice.MicrophonePort = "COM-test";
        Assert.True(voice.ToggleWakeCommand.CanExecute(null));
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1 && voice.WakeStatus.Contains("等待唤醒"));
        Assert.NotNull(wake.Waits[0].Audio);
        Assert.False(chat.VoiceInputActive);

        wake.Waits[0].Result.SetResult("");
        await Until(() => wake.Captures.Count == 1 && voice.WakeStatus.Contains("正在听"));
        Assert.NotNull(wake.Captures[0].Audio);
        Assert.False(chat.VoiceInputActive);
        Assert.True(voice.IsListening);
        var streams = microphone.Streams.ToArray();
        Assert.Equal(["COM-test", "COM-test"], streams.Select(value => value.Port));
        Assert.True(streams[0].Cancellation.IsCancellationRequested);
        wake.Captures[0].Result.SetResult("可以给我讲个故事吗");
        await Until(() => chat.Draft.Length > 0 && !chat.VoiceInputActive);
        Assert.True(streams[1].Cancellation.IsCancellationRequested);
        Assert.Equal("可以给我讲个故事吗", chat.Draft);
        Assert.Equal(0, cloud.Transcriptions);
        Assert.Equal(0, cloud.Corrections);
        Assert.Equal(0, client.Sends);
        await voice.CloseWakeAsync();
    }

    [AvaloniaTheory]
    [InlineData("今天心晴不错。", "今天心情不错。", "今天心情不错。", true)]
    [InlineData("今天心情不错。", "今天心情不错。", "今天心情不错。", true)]
    [InlineData("关闭摄像机跟水。", "关闭摄像机跟随。", "关闭摄像机跟随。", false)]
    [InlineData("聊聊摄像机画质。", "打开摄像机跟随。", "打开摄像机跟随。", false)]
    [InlineData("关闭语音唤新。", "关闭语音唤醒。", "关闭语音唤醒。", false)]
    [InlineData("今天心晴不错。", null, "今天心晴不错。", true)]
    public async Task ContinuousCorrectionRoutesCommandsWithMatchingIntentAndSendsOrdinaryText(
        string original, string? correction, string expected, bool sends)
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var wake = new WakeSpeech();
        var cloud = new CloudSpeech();
        var store = CloudOptions();
        store.Save(0, true, true, correctWakeText: true);
        using var voice = new VoiceViewModel(chat, new Speech(), recognitionStore: store, cloudSpeech: cloud, wakeSpeech: wake);
        var commands = 0;
        voice.LocalCommandHandler = (_, _) => { commands++; return Task.FromResult<string?>("stopped"); };
        await chat.ConnectAsync(); await voice.InitializeAsync();
        chat.ReadReplies = false;
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1);
        wake.Waits[0].Result.SetResult("");
        await Until(() => wake.Captures.Count == 1);
        wake.Captures[0].Result.SetResult(original);
        await Until(() => cloud.Corrections == 1);
        Assert.Equal(0, cloud.Transcriptions);
        Assert.Equal(0, client.Sends);
        if (correction is null) cloud.Correction.SetException(new InvalidDataException("invalid correction response"));
        else cloud.Correction.SetResult(correction);
        if (sends)
        {
            await Until(() => client.Sends == 1 && wake.Captures.Count == 2);
            Assert.Equal(expected, client.Turns[0].Text);
        }
        else
        {
            await Until(() => !voice.ShowWakeMeter || wake.Captures.Count == 2);
            Assert.Equal(0, client.Sends);
            Assert.Empty(chat.Draft);
        }
        Assert.Equal(original.Contains("跟水") ? 1 : 0, commands);
        Assert.Equal(!original.Contains("唤新"), voice.ShowWakeMeter);
        Assert.Equal(original, voice.OriginalTranscript);
        Assert.Equal(expected, voice.CorrectedTranscript);
        var feedback = sends ? correction is null ? "请求失败" : original == expected ? "模型保留原文" : "已校正文字"
            : original.Contains("跟水") ? "stopped" : original.Contains("唤新") ? "已执行语音指令" : "请重新说一遍";
        Assert.Contains(feedback, voice.WakeCorrectionNotice);
        await voice.CloseWakeAsync();
    }

    [AvaloniaTheory]
    [InlineData("浪独，幸运春雪。")]
    [InlineData("浪独庆元春雪。")]
    public async Task KnownReadingRequestIsFixedBeforeChatSend(string transcript)
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var wake = new WakeSpeech();
        var cloud = new CloudSpeech();
        var store = CloudOptions();
        store.Save(0, true, true, correctWakeText: true);
        using var voice = new VoiceViewModel(chat, new Speech(), recognitionStore: store, cloudSpeech: cloud, wakeSpeech: wake);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        chat.ReadReplies = false;
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1);
        wake.Waits[0].Result.SetResult("");
        await Until(() => wake.Captures.Count == 1);
        wake.Captures[0].Result.SetResult(transcript);
        await Until(() => client.Sends == 1 && wake.Captures.Count == 2);
        Assert.Equal("朗读《沁园春·雪》。", client.Turns[0].Text);
        Assert.Equal(transcript, voice.OriginalTranscript);
        Assert.Contains("本地纠错", voice.WakeCorrectionNotice);
        Assert.Equal(0, cloud.Corrections);
        Assert.Null(TranscriptCorrection.RestoreKnownReadingRequest("不要浪独庆元春雪"));
        Assert.Null(TranscriptCorrection.RestoreKnownReadingRequest("幸运春雪"));
        await voice.CloseWakeAsync();
    }

    [AvaloniaFact]
    public async Task ScreenshotStopCommandHomophoneIsHandledLocallyBeforeCorrection()
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var wake = new WakeSpeech();
        var cloud = new CloudSpeech();
        var store = CloudOptions();
        store.Save(0, true, true, correctWakeText: true);
        using var voice = new VoiceViewModel(chat, new Speech(), recognitionStore: store, cloudSpeech: cloud, wakeSpeech: wake);
        var commands = new List<string>();
        voice.LocalCommandHandler = (text, _) => { commands.Add(text); return Task.FromResult<string?>("stopped"); };
        await chat.ConnectAsync(); await voice.InitializeAsync();
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1);
        wake.Waits[0].Result.SetResult("");
        await Until(() => wake.Captures.Count == 1);
        wake.Captures[0].Result.SetResult("关闭摄像机跟谁？");
        await Until(() => wake.Captures.Count == 2);
        Assert.Equal(["关闭摄像机跟随"], commands);
        Assert.Equal(0, cloud.Corrections);
        Assert.Equal(0, client.Sends);
        Assert.Equal(VoiceCommand.None, VoiceCommands.Match("不要关闭摄像机跟谁。"));
        await voice.CloseWakeAsync();
    }

    [AvaloniaFact]
    public async Task ChangingASelectedModulePortReleasesOldAudioBeforeStartingTheNewInput()
    {
        using var chat = Chat(new TobooChatTests.FakeClient());
        var wake = new WakeSpeech();
        var microphone = new StreamingMicrophone();
        using var voice = NewVoice(chat, new Speech(), microphone, wake);
        await voice.InitializeAsync();
        voice.MicrophoneSourceIndex = 1;
        voice.MicrophonePort = "COM-old";
        voice.ToggleWakeCommand.Execute(null);
        await Until(() => wake.Waits.Count == 1 && voice.WakeStatus.Contains("等待唤醒"));
        var old = Assert.Single(microphone.Streams);
        voice.MicrophonePort = "COM-new";
        Assert.True(old.Cancellation.IsCancellationRequested);
        await Until(() => microphone.Streams.Count == 2 && voice.WakeStatus.Contains("等待唤醒"));
        Assert.Equal("COM-new", microphone.Streams.Last().Port);
        Assert.Empty(wake.Captures);
        Assert.Equal(1, microphone.PeakConcurrentStreams);
        await voice.CloseWakeAsync();
    }

    private sealed class WakeSpeech : IVoiceWakeService
    {
        public readonly List<Attempt> Waits = [];
        public readonly List<Attempt> Captures = [];
        public bool HoldKeywordOnCancellation, HoldCaptureOnCancellation;

        public async Task<bool> WaitForWakeAsync(Action ready, CancellationToken cancellation, Stream? audio = null, Action<double>? level = null)
        {
            await Start(Waits, ready, cancellation, HoldKeywordOnCancellation, audio, level);
            return true;
        }

        public Task<string> ListenOnceAsync(Action ready, CancellationToken cancellation, Stream? audio = null,
            Action<double>? level = null, Action? recognizing = null) =>
            Start(Captures, ready, cancellation, HoldCaptureOnCancellation, audio, level, recognizing);

        private static Task<string> Start(List<Attempt> attempts, Action ready, CancellationToken cancellation,
            bool holdOnCancellation, Stream? audio, Action<double>? level, Action? recognizing = null)
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            attempts.Add(new(result, cancellation, audio, level, recognizing));
            ready();
            return holdOnCancellation ? result.Task : result.Task.WaitAsync(cancellation);
        }

        public sealed record Attempt(TaskCompletionSource<string> Result, CancellationToken Cancellation, Stream? Audio,
            Action<double>? Level, Action? Recognizing);
    }

    private sealed class StreamingMicrophone : IMicrophoneCapture, IStreamingMicrophoneCapture
    {
        public readonly ConcurrentQueue<(string Port, CancellationToken Cancellation)> Streams = new();
        private int active;
        public int PeakConcurrentStreams { get; private set; }

        public async Task StreamAsync(string port, Action<byte[]> samples, CancellationToken cancellation)
        {
            PeakConcurrentStreams = Math.Max(PeakConcurrentStreams, Interlocked.Increment(ref active));
            try
            {
                Streams.Enqueue((port, cancellation));
                samples([0, 0]);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            finally { Interlocked.Decrement(ref active); }
        }

        public Task<byte[]> RecordAsync(string port, Action started, Action<double> level, CancellationToken cancellation) =>
            throw new InvalidOperationException("Unexpected push-to-talk test call.");

        public void Finish() => throw new InvalidOperationException("Unexpected push-to-talk test call.");
    }
}
