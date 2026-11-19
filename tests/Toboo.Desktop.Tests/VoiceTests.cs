using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Toboo.Desktop.Services;
using Toboo.Desktop.ViewModels;
using Toboo.Desktop.Views;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class VoiceTests
{
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LocalVoiceCommandWorksWithoutHarnessAndKeepsChatDraftUntouched(int source)
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var speech = new Speech();
        using var voice = new VoiceViewModel(chat, speech, new Microphone());
        var commands = 0;
        voice.LocalCommandHandler = (text, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            Assert.Equal("打开摄像机跟随。", text);
            commands++;
            return Task.FromResult<string?>("正在开启摄像机跟随。");
        };
        await voice.InitializeAsync();
        voice.MicrophoneSourceIndex = source;
        voice.MicrophonePort = "COM-test";
        Assert.False(chat.IsConnected);
        Assert.True(voice.CanRecord);
        var recording = voice.BeginListeningAsync();
        voice.FinishListening();
        speech.Transcript.SetResult("打开摄像机跟随。");
        await recording;
        Assert.Equal(1, commands);
        Assert.Equal(0, client.Sends);
        Assert.Equal("", chat.Draft);
        Assert.Contains("正在开启", voice.Status);
        Assert.False(voice.IsListening);
    }

    [AvaloniaFact]
    public async Task CancelledRecognitionCannotTriggerALocalHardwareCommand()
    {
        using var chat = Chat(new TobooChatTests.FakeClient());
        var speech = new Speech { IgnoreCancellation = true };
        using var voice = new VoiceViewModel(chat, speech);
        var commands = 0;
        voice.LocalCommandHandler = (_, _) => { commands++; return Task.FromResult<string?>("started"); };
        await voice.InitializeAsync();
        var recording = voice.BeginListeningAsync();
        voice.FinishListening();
        voice.Stop();
        speech.Transcript.SetResult("打开摄像机跟随");
        await recording;
        Assert.Equal(0, commands);
        Assert.Equal("", chat.Draft);
    }

    [AvaloniaFact]
    public async Task UnmatchedOfflineSpeechLeavesTheCommandButtonAvailableForRetry()
    {
        using var chat = Chat(new TobooChatTests.FakeClient());
        var speech = new Speech();
        using var voice = new VoiceViewModel(chat, speech);
        voice.LocalCommandHandler = (_, _) => Task.FromResult<string?>(null);
        await voice.InitializeAsync();
        var recording = voice.BeginListeningAsync();
        voice.FinishListening();
        speech.Transcript.SetResult("你好");
        await recording;
        Assert.Equal("", chat.Draft);
        Assert.True(voice.CanRecord);
        Assert.Contains("未匹配", voice.Status);
    }

    private sealed class Speech : ISpeechService
    {
        public SpeechCapabilities Capabilities = new(true, true, "中文识别", "中文朗读", true, "ICS43432 · 本地中文识别");
        public TaskCompletionSource<string> Transcript = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Playback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Spoken = [];
        public int Listens, Finishes, PcmCalls;
        public bool IgnoreCancellation;
        public Action<bool>? Playing;
        public Action? Word;
        public Task<SpeechCapabilities> InspectAsync(CancellationToken cancellation) => Task.FromResult(Capabilities);
        public Task<string> ListenAsync(Action started, CancellationToken cancellation)
        {
            Listens++; started();
            return IgnoreCancellation ? Transcript.Task : Transcript.Task.WaitAsync(cancellation);
        }
        public void FinishListening() => Finishes++;
        public Task<string> RecognizePcmAsync(byte[] pcm, CancellationToken cancellation)
        {
            PcmCalls++; return Transcript.Task.WaitAsync(cancellation);
        }
        public async Task SpeakAsync(string text, Action<bool> playing, Action word, CancellationToken cancellation)
        {
            Spoken.Add(text); Playing = playing; Word = word; playing(true); word();
            try { await Playback.Task.WaitAsync(cancellation); }
            finally { playing(false); }
        }
    }

    private static ChatViewModel Chat(TobooChatTests.FakeClient client) => new(client,
        new(Path.Combine(Path.GetTempPath(), "toboo-voice-tests", Guid.NewGuid().ToString("N"), "client.json")));

    private sealed class Microphone : IMicrophoneCapture
    {
        private readonly TaskCompletionSource<byte[]> data = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]> RecordAsync(string port, Action started, Action<double> level, CancellationToken cancellation)
        {
            started(); level(0.5); return data.Task.WaitAsync(cancellation);
        }
        public void Finish()
        {
            var pcm = new byte[6400];
            for (var i = 0; i < pcm.Length; i += 4) pcm[i] = 100;
            data.SetResult(pcm);
        }
    }

    [AvaloniaFact]
    public async Task ModuleRecognitionDoesNotRequireAWindowsDictationLanguagePack()
    {
        using var chat = Chat(new TobooChatTests.FakeClient());
        var speech = new Speech { Capabilities = new(false, true, "未安装系统识别", "中文朗读", true, "Whisper 本地中文识别") };
        using var voice = new VoiceViewModel(chat, speech);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        Assert.False(voice.CanListen);
        voice.MicrophoneSourceIndex = 1; voice.MicrophonePort = "COM-test";
        Assert.True(voice.CanRecord);
        Assert.Contains("Whisper", voice.InputDescription);
        voice.MicrophoneSourceIndex = 0;
        Assert.False(voice.CanRecord);
    }

    [AvaloniaFact]
    public async Task MissingModuleModelDoesNotDisableComputerMicrophone()
    {
        using var chat = Chat(new TobooChatTests.FakeClient());
        var speech = new Speech { Capabilities = new(true, true, "中文识别", "中文朗读", false, "缺少识别模型") };
        using var voice = new VoiceViewModel(chat, speech);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        Assert.True(voice.CanRecord);
        voice.MicrophoneSourceIndex = 1; voice.MicrophonePort = "COM-test";
        Assert.False(voice.CanRecord);
        Assert.Contains("缺少识别模型", voice.RecordHint);
    }

    [AvaloniaFact]
    public async Task ModuleRecordingUsesPcmRecognitionAndTheExistingChatSend()
    {
        var client = new TobooChatTests.FakeClient { StreamReply = true };
        using var chat = Chat(client);
        var speech = new Speech();
        using var voice = new VoiceViewModel(chat, speech, new Microphone());
        await chat.ConnectAsync(); await voice.InitializeAsync();
        voice.MicrophoneSourceIndex = 1; voice.MicrophonePort = "COM-test";
        var recording = voice.BeginListeningAsync();
        Assert.True(voice.IsListening);
        voice.FinishListening();
        speech.Transcript.SetResult("这是模块采音");
        await recording;
        Assert.Equal(0, speech.Listens);
        Assert.Equal(1, speech.PcmCalls);
        Assert.Equal("这是模块采音", client.Turns.Single().Text);
        Assert.Equal(0, voice.MicrophoneLevel);
    }

    [AvaloniaFact]
    public async Task HoldReleaseSendsOnceAndReadsOnlyTheNewCompletedReply()
    {
        var client = new TobooChatTests.FakeClient { StreamReply = true };
        using var chat = Chat(client);
        var speech = new Speech();
        using var voice = new VoiceViewModel(chat, speech);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        var recording = voice.BeginListeningAsync();
        Assert.True(voice.IsListening && chat.VoiceInputActive);
        Assert.False(chat.CanChoose); Assert.False(chat.CanEditDraft);
        Assert.Empty(client.Turns);
        voice.FinishListening(); voice.FinishListening();
        Assert.Equal(1, speech.Finishes);
        speech.Transcript.SetResult("今天心情很好");
        await recording;
        Assert.Equal(1, client.Sends);
        Assert.Equal("今天心情很好", client.Turns[0].Text);
        Assert.False(chat.VoiceInputActive);
        Assert.Empty(speech.Spoken);

        client.Turns[0] = client.Turns[0] with { Reply = "真替你高兴。", State = "completed" };
        await chat.RefreshAsync();
        await Until(() => voice.IsSpeaking);
        Assert.Equal(["真替你高兴。"], speech.Spoken);
        Assert.Equal(LcdRole.Responding, voice.CaptureLcdRole());
        await chat.RefreshAsync(); await chat.RefreshAsync(reset: true);
        Assert.Single(speech.Spoken);
        speech.Playback.SetResult();
        await Until(() => !voice.CanStop);
        Assert.Equal(LcdRole.Happy, voice.CaptureLcdRole());
    }

    [AvaloniaFact]
    public async Task CancelOrCloseDiscardsALateRecognitionResult()
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var speech = new Speech { IgnoreCancellation = true };
        using var voice = new VoiceViewModel(chat, speech);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        var recording = voice.BeginListeningAsync();
        voice.Dispose();
        speech.Transcript.SetResult("迟到的识别");
        await recording;
        Assert.Equal(0, client.Sends);
        Assert.Equal("", chat.Draft);
        Assert.False(chat.VoiceInputActive);
    }

    [AvaloniaFact]
    public async Task EmptyRecognitionAndExistingDraftNeverSendAnUnintendedMessage()
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var speech = new Speech();
        using var voice = new VoiceViewModel(chat, speech);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        chat.Draft = "还没编辑好";
        await voice.BeginListeningAsync();
        Assert.Equal(0, speech.Listens);
        Assert.Equal("还没编辑好", chat.Draft);
        chat.Draft = "";
        var recording = voice.BeginListeningAsync();
        voice.FinishListening(); speech.Transcript.SetResult(" ");
        await recording;
        Assert.Equal(0, client.Sends);
        Assert.Contains("没有听清", voice.Status);
        Assert.True(voice.CanRecord);
    }

    [AvaloniaFact]
    public async Task StartingTheNextUtteranceInterruptsPlaybackBeforeOpeningTheMicrophone()
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var speech = new Speech();
        using var voice = new VoiceViewModel(chat, speech);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        chat.Draft = "你好"; await chat.SendAsync();
        await Until(() => voice.IsSpeaking);
        var recording = voice.BeginListeningAsync();
        await Until(() => speech.Listens == 1);
        Assert.False(voice.IsSpeaking);
        voice.Stop();
        await recording;
        Assert.Equal(1, client.Sends);
        speech.Playing?.Invoke(true); speech.Word?.Invoke();
        Dispatcher.UIThread.RunJobs();
        Assert.False(voice.IsSpeaking);
    }

    [AvaloniaFact]
    public async Task DisablingReadingOrChangingConversationStopsPlaybackAndPreferencesSurvive()
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        var speech = new Speech();
        using var voice = new VoiceViewModel(chat, speech);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        chat.Draft = "你好"; await chat.SendAsync();
        await Until(() => voice.IsSpeaking);
        chat.ReadReplies = false;
        await Until(() => !voice.CanStop);
        Assert.Null(voice.CaptureLcdRole());
        Assert.Null(chat.CaptureLcdRole(animateTextReply: false));
        chat.NewChatCommand.Execute(null);
        Assert.False(voice.IsSpeaking);
        chat.Draft = "再试一次"; await chat.SendAsync();
        Assert.Single(speech.Spoken);
    }

    [AvaloniaFact]
    public async Task MissingLanguageComponentsLeaveTypingAvailable()
    {
        var client = new TobooChatTests.FakeClient();
        using var chat = Chat(client);
        using var voice = new VoiceViewModel(chat, new Speech { Capabilities = new(false, false, "请安装中文组件", "请安装中文声音", false, "缺少识别模型") });
        await chat.ConnectAsync(); await voice.InitializeAsync();
        Assert.False(voice.CanRecord); Assert.False(voice.CanSpeak);
        chat.Draft = "仍然可以打字";
        Assert.True(chat.CanSend);
        Assert.Contains("中文组件", voice.Status);
    }

    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(980, 700)]
    public async Task PointerHoldAndReleaseUsesTheVisibleVoiceButton(int width, int height)
    {
        var client = new TobooChatTests.FakeClient { StreamReply = true };
        using var chat = Chat(client);
        var speech = new Speech();
        using var voice = new VoiceViewModel(chat, speech);
        using var tracking = new MainViewModel();
        var window = new MainWindow(tracking, chat, voice) { Width = width, Height = height };
        try
        {
            window.Show();
            await chat.ConnectAsync(); await voice.InitializeAsync(); await Task.Delay(40);
            var home = window.FindControl<CompanionHomeView>("HomeView")!;
            var button = home.FindControl<Button>("VoiceHoldButton")!;
            Assert.True(button.IsEffectivelyEnabled);
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            await Until(() => voice.IsListening);
            await Until(() => tracking.Lcd.PreviewRole == LcdRole.Attentive);
            Assert.Equal("松开发送", voice.RecordLabel);
            Assert.True(button.IsEffectivelyEnabled);
            using (var bitmap = window.CaptureRenderedFrame())
            {
                Assert.NotNull(bitmap);
                var directory = Path.Combine(Workspace(), "artifacts", "voice-preview");
                Directory.CreateDirectory(directory);
                bitmap.Save(Path.Combine(directory, $"listening-{width}.png"));
            }
            window.MouseUp(center, MouseButton.Left);
            Assert.Equal(1, speech.Finishes);
            speech.Transcript.SetResult("测试语音按钮");
            await Until(() => client.Sends == 1);
            Assert.False(chat.VoiceInputActive);
            Assert.Equal("测试语音按钮", client.Turns[0].Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReadReplyPreferenceSurvivesRestartAndOlderSettingsDefaultToReading()
    {
        var path = Path.Combine(Path.GetTempPath(), "toboo-voice-tests", Guid.NewGuid().ToString("N"), "client.json");
        using (var first = new ChatViewModel(new TobooChatTests.FakeClient(), new(path)))
        {
            await first.ConnectAsync(); first.ReadReplies = false;
        }
        using var restored = new ChatViewModel(new TobooChatTests.FakeClient(), new(path));
        await restored.ConnectAsync();
        Assert.False(restored.ReadReplies);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<ChatClientState>("{}")!.ReadReplies);
    }

    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(980, 700)]
    public async Task ModuleSelectorRendersAtBothWidthsWithoutOpeningHardware(int width, int height)
    {
        using var tracking = new MainViewModel();
        var window = new MainWindow(tracking) { Width = width, Height = height };
        try
        {
            window.Show(); window.Shell.PageIndex = 1;
            var view = window.FindControl<DevicesView>("DevicesView")!;
            view.FindControl<ComboBox>("MicrophoneSourceSelector")!.SelectedIndex = 1;
            await Task.Delay(30);
            Assert.True(window.Shell.Voice.IsI2sMicrophone);
            Assert.True(view.FindControl<ComboBox>("MicrophonePortSelector")!.IsEffectivelyVisible);
            Assert.Null(window.Shell.Voice.MicrophonePort);
            Assert.False(tracking.Servo.Ready);
            view.FindControl<ScrollViewer>("DeviceScroll")!.ScrollToEnd();
            using var bitmap = window.CaptureRenderedFrame();
            var directory = Path.Combine(Workspace(), "artifacts", "voice-preview");
            Directory.CreateDirectory(directory);
            bitmap!.Save(Path.Combine(directory, $"module-{width}.png"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelledGenerationNeverStartsReadingPartialText()
    {
        var client = new TobooChatTests.FakeClient { StreamReply = true };
        using var chat = Chat(client);
        var speech = new Speech();
        using var voice = new VoiceViewModel(chat, speech);
        await chat.ConnectAsync(); await voice.InitializeAsync();
        chat.Draft = "测试停止"; await chat.SendAsync();
        client.Turns[0] = client.Turns[0] with { Reply = "尚未完成", State = "cancelled" };
        await chat.RefreshAsync();
        Assert.Empty(speech.Spoken);
        Assert.False(voice.IsSpeaking);
    }

    [Theory]
    [InlineData("## 你好\n**朋友**，\n[看这里](https://example.com)", "你好 朋友， 看这里")]
    [InlineData("说明\n```cs\nsecretCode();\n```\n结束", "说明 结束")]
    public void SpeechUsesReadableText(string source, string expected) => Assert.Equal(expected, VoiceViewModel.ReadableText(source));

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Toboo.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
