using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toboo.Desktop.Services;

namespace Toboo.Desktop.ViewModels;

public sealed partial class VoiceViewModel : ObservableObject, IDisposable
{
    private readonly ChatViewModel chat;
    private readonly ISpeechService? speech;
    private readonly IMicrophoneCapture microphone;
    private readonly SpeechRecognitionStore recognitionStore;
    private readonly IDeepSeekSpeech cloudSpeech;
    private readonly bool ownsCloudSpeech;
    private string? cloudApiKey;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? input, output;
    private Task listeningTask = Task.CompletedTask, speakingTask = Task.CompletedTask;
    private long listenStarted, lastWord;
    private long? repliedAt;
    private bool disposed;
    private SpeechCapabilities? capabilities;
    private Func<string, CancellationToken, Task<string?>>? localCommandHandler;
    internal Func<string, CancellationToken, Task<string?>>? LocalCommandHandler
    {
        get => localCommandHandler;
        set { localCommandHandler = value; Refresh(); }
    }
    [ObservableProperty] private bool initializing;
    [ObservableProperty] private bool canListen;
    [ObservableProperty] private bool canSpeak;
    [ObservableProperty] private bool isListening;
    [ObservableProperty] private bool isFinishing;
    [ObservableProperty] private bool isSpeaking;
    [ObservableProperty] private string status = "正在检查本机语音…";
    [ObservableProperty] private string inputDescription = "使用系统默认麦克风";
    [ObservableProperty] private string outputDescription = "使用系统默认音频输出";
    [ObservableProperty] private int microphoneSourceIndex;
    [ObservableProperty] private string? microphonePort;
    [ObservableProperty] private string[] microphonePorts = [];
    [ObservableProperty] private double microphoneLevel;
    [ObservableProperty] private int moduleRecognizerIndex;
    [ObservableProperty] private bool correctTranscript = true;
    [ObservableProperty] private bool reviewTranscript = true;
    [ObservableProperty] private string apiKeyDraft = "";
    [ObservableProperty] private string voiceSettingsStatus = "本地识别在电脑上运行；DeepSeek 模式会上传本次录音，并按服务计费。";
    [ObservableProperty] private string originalTranscript = "";
    [ObservableProperty] private string correctedTranscript = "";
    [ObservableProperty] private string audioDiagnostics = "";
    public bool IsCloudModule => ModuleRecognizerIndex == 1;
    public bool HasCloudKey => !string.IsNullOrWhiteSpace(cloudApiKey);
    public bool HasTranscript => OriginalTranscript.Length > 0;
    public bool IsI2sMicrophone => MicrophoneSourceIndex == 1;
    public bool CanConfigureMicrophone => !CanStop && !Initializing;
    public bool CanRecord => !disposed && CanListen && !IsListening && !chat.IsBusy
        && !chat.RemoteRunning && !chat.HasPending && (localCommandHandler is not null || chat.IsConnected && chat.SelectedModel is not null)
        && string.IsNullOrWhiteSpace(chat.Draft)
        && (!IsI2sMicrophone || !string.IsNullOrWhiteSpace(MicrophonePort));
    public bool CanStop => IsListening || output is not null;
    public bool CanUseRecordButton => CanRecord || IsListening;
    public string RecordLabel => IsFinishing ? "正在识别…" : IsListening ? IsI2sMicrophone && IsCloudModule && ReviewTranscript ? "松开识别" : "松开发送" : "按住说话";
    public string RecordHint => !CanListen ? InputDescription : !chat.IsConnected
        ? IsI2sMicrophone && IsCloudModule && ReviewTranscript ? "可识别并查看文字；语音对话需连接 Harness"
            : localCommandHandler is not null ? "可说“打开摄像机跟随”；语音对话需连接 Harness" : "连接 Harness 后可语音对话"
        : IsI2sMicrophone && string.IsNullOrWhiteSpace(MicrophonePort) ? "请到设备页选择 ESP32-S3 麦克风串口"
        : !string.IsNullOrWhiteSpace(chat.Draft) ? "先发送或清空输入框，再按住说话"
        : IsI2sMicrophone && IsCloudModule && ReviewTranscript ? "按住说话，松开识别；确认文字后点击发送；Esc 取消" : "按住说话，松开发送；Esc 取消";

    internal VoiceViewModel(ChatViewModel chat, ISpeechService? speech = null, IMicrophoneCapture? microphone = null,
        SpeechRecognitionStore? recognitionStore = null, IDeepSeekSpeech? cloudSpeech = null)
    {
        this.chat = chat;
        this.speech = speech ?? (OperatingSystem.IsWindows() ? new WindowsSpeechService() : null);
        this.microphone = microphone ?? new I2sMicrophone();
        this.recognitionStore = recognitionStore ?? new();
        this.cloudSpeech = cloudSpeech ?? new DeepSeekSpeechClient();
        ownsCloudSpeech = cloudSpeech is null;
        moduleRecognizerIndex = this.recognitionStore.Current.Engine;
        microphoneSourceIndex = this.recognitionStore.Current.Source;
        correctTranscript = this.recognitionStore.Current.CorrectText;
        reviewTranscript = this.recognitionStore.Current.ReviewText;
        if (this.recognitionStore.LoadError is { } loadError) voiceSettingsStatus = loadError;
        try { cloudApiKey = this.recognitionStore.ReadKey(); }
        catch (Exception error) when (error is InvalidOperationException) { voiceSettingsStatus = error.Message; }
        chat.PropertyChanged += ChatChanged;
        chat.ReplyCompleted += ReplyCompleted;
        chat.ConversationChanged += Stop;
        timer.Tick += Tick;
        RefreshMicrophonePorts();
    }
    partial void OnMicrophoneSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsI2sMicrophone));
        ApplyInputCapabilities();
        Status = CanListen ? "按住说话，松开发送" : InputDescription;
        Refresh();
    }
    partial void OnMicrophonePortChanged(string? value) => Refresh();
    partial void OnModuleRecognizerIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCloudModule));
        ApplyInputCapabilities(); Refresh();
    }
    partial void OnReviewTranscriptChanged(bool value) => Refresh();
    partial void OnOriginalTranscriptChanged(string value) => OnPropertyChanged(nameof(HasTranscript));
    [RelayCommand(CanExecute = nameof(CanConfigureMicrophone))]
    private void SaveRecognitionSettings()
    {
        try
        {
            recognitionStore.Save(ModuleRecognizerIndex, CorrectTranscript, ReviewTranscript,
                string.IsNullOrWhiteSpace(ApiKeyDraft) ? null : ApiKeyDraft, source: MicrophoneSourceIndex, port: MicrophonePort);
            cloudApiKey = recognitionStore.ReadKey(); ApiKeyDraft = "";
            VoiceSettingsStatus = HasCloudKey ? "语音设置已保存，密钥由本机 Windows 账户加密。" : "语音设置已保存；DeepSeek 模式仍需配置 API Key。";
            OnPropertyChanged(nameof(HasCloudKey)); ApplyInputCapabilities(); Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { VoiceSettingsStatus = "语音配置未保存：" + error.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanConfigureMicrophone))]
    private void ClearSpeechKey()
    {
        try
        {
            recognitionStore.Save(ModuleRecognizerIndex, CorrectTranscript, ReviewTranscript, clearKey: true);
            cloudApiKey = null; ApiKeyDraft = "";
            VoiceSettingsStatus = "已清除本机保存的 DeepSeek 语音密钥。";
            OnPropertyChanged(nameof(HasCloudKey)); ApplyInputCapabilities(); Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { VoiceSettingsStatus = "无法清除密钥：" + error.Message; }
    }
    [RelayCommand]
    private void RefreshMicrophonePorts()
    {
        if (!CanConfigureMicrophone) return;
        var selected = MicrophonePort ?? recognitionStore.Current.Port;
        try
        {
            MicrophonePorts = SerialPortCatalog.Normalize(SerialPortCatalog.List());
            MicrophonePort = MicrophonePorts.FirstOrDefault(port => port == selected);
        }
        catch (Exception) { MicrophonePorts = []; MicrophonePort = null; }
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (disposed || Initializing || CanStop) return;
        Initializing = true;
        try
        {
            capabilities = speech is null
                ? new SpeechCapabilities(false, false, "本机语音需要 Windows", "本机语音需要 Windows", false, "本机语音需要 Windows")
                : await speech.InspectAsync(CancellationToken.None);
            if (disposed) return;
            ApplyInputCapabilities(); CanSpeak = capabilities.CanSpeak;
            OutputDescription = capabilities.Output;
            Status = CanListen ? "按住说话，松开发送" : InputDescription;
        }
        catch (Exception)
        {
            if (!disposed)
            {
                capabilities = new(false, false, "请检查 Windows 中文语音组件", "系统朗读不可用",
                    WhisperPcmRecognizer.UnavailableReason is null, WhisperPcmRecognizer.UnavailableReason ?? "Whisper 本地中文识别");
                ApplyInputCapabilities(); CanSpeak = false; OutputDescription = capabilities.Output;
                Status = CanListen ? "系统语音组件不可用，模块识别仍可使用。" : "无法初始化语音，请检查 Windows 中文语音组件后重试。";
            }
        }
        finally { Initializing = false; Refresh(); }
    }

    private void ApplyInputCapabilities()
    {
        CanListen = capabilities is not null && (IsI2sMicrophone ? IsCloudModule ? HasCloudKey : capabilities.CanRecognizePcm : capabilities.CanListen);
        InputDescription = IsI2sMicrophone
            ? IsCloudModule ? HasCloudKey ? "ICS43432 · DeepSeek 云端中文识别" : "DeepSeek 模块识别 · 请配置并保存 API Key"
                : capabilities?.PcmInput ?? "ICS43432 · 选择 ESP32-S3 串口"
            : capabilities?.Input ?? "中文识别 · 系统默认麦克风";
    }

    public Task BeginListeningAsync()
    {
        if (!CanRecord || speech is null) return Task.CompletedTask;
        Stop();
        repliedAt = null;
        input = new CancellationTokenSource();
        IsListening = true; IsFinishing = false; chat.VoiceInputActive = true;
        OriginalTranscript = ""; CorrectedTranscript = "";
        AudioDiagnostics = "";
        listenStarted = Stopwatch.GetTimestamp();
        Status = "正在准备麦克风…";
        timer.Start(); Refresh();
        listeningTask = ListenAndSendAsync(input);
        return listeningTask;
    }

    private async Task ListenAndSendAsync(CancellationTokenSource operation)
    {
        var useModule = IsI2sMicrophone;
        var useCloud = useModule && IsCloudModule;
        var key = cloudApiKey;
        var correct = CorrectTranscript;
        var review = useCloud && ReviewTranscript;
        try
        {
            await speakingTask;
            operation.Token.ThrowIfCancellationRequested();
            if (IsFinishing) return;
            void Started() => Post(operation, () =>
            {
                if (!IsFinishing) Status = review ? "正在听… 松开识别，核对后发送" : "正在听… 松开发送";
            });
            string text;
            if (useModule)
            {
                var pcm = await microphone.RecordAsync(MicrophonePort!, Started,
                    value => Post(operation, () => MicrophoneLevel = value * 100), operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (pcm.Length < 3200 || I2sMicrophone.Peak(pcm) == 0)
                {
                    Status = "模块没有收到有效声音，请检查接线和声道选择。"; return;
                }
                AudioDiagnostics = DescribeAudio(pcm);
                Status = useCloud ? "正在使用 DeepSeek 识别模块录音…" : "正在本地识别模块录音…";
                I2sMicrophone.Normalize(pcm);
                text = useCloud ? await cloudSpeech.TranscribeAsync(pcm, key ?? "", operation.Token)
                    : await speech!.RecognizePcmAsync(pcm, operation.Token);
            }
            else text = await speech!.ListenAsync(Started, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) { Status = "没有听清，请按住后再说一次。"; return; }
            if (text.Length > 4000) { Status = "这段语音太长，请分成几句说。"; return; }
            OriginalTranscript = text.Trim(); CorrectedTranscript = OriginalTranscript;
            var original = OriginalTranscript;
            var correctionNotice = "";
            // The draft stays editable after transcription, including on an uncertain send.
            var released = IsFinishing;
            if (released && !review && localCommandHandler is not null)
            {
                var result = await localCommandHandler(original, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (result is not null) { Status = result; return; }
            }
            text = TranscriptCorrection.NormalizeTerms(original);
            if (useCloud && correct)
            {
                try
                {
                    Status = "识别完成，正在校正明显错字…";
                    var corrected = await cloudSpeech.CorrectAsync(text, key ?? "", operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    if (TranscriptCorrection.IsConservative(text, corrected)) text = corrected.Trim();
                    else correctionNotice = "纠错建议改变了数字、否定词或改动过大，已保留原文。";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or TimeoutException)
                { correctionNotice = "纠错未完成，已保留识别原文。"; }
            }
            operation.Token.ThrowIfCancellationRequested();
            CorrectedTranscript = text;
            if (!chat.IsConnected)
            {
                Status = "未匹配本地语音命令；识别文字已保留在语音详情中。连接 Harness 后可语音对话。";
                return;
            }
            chat.Draft = text.Trim();
            chat.VoiceInputActive = false;
            IsListening = false; IsFinishing = false; timer.Stop(); Refresh();
            var mustReview = review || text != original || correctionNotice.Length > 0;
            Status = correctionNotice.Length > 0 ? correctionNotice + "请核对输入框后发送。"
                : !mustReview && released && chat.CanSend ? "已识别，正在发送…" : "语音已填入输入框，请确认或编辑后发送。";
            if (!mustReview && released && chat.CanSend)
            {
                await chat.SendAsync();
                if (output is null) Status = chat.HasPending || chat.HasError ? "请查看对话发送状态。" : "已发送 · 等待回复";
            }
        }
        catch (OperationCanceledException)
        {
            if (!disposed)
            { OriginalTranscript = ""; CorrectedTranscript = ""; Status = "本次语音已取消"; }
        }
        catch (Exception error)
        {
            if (!disposed) Status = (IsI2sMicrophone ? "模块语音输入失败。" : "语音输入失败，请检查默认麦克风和中文识别组件。") + " " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(input, operation))
            {
                input = null; IsListening = false; IsFinishing = false;
                chat.VoiceInputActive = false; MicrophoneLevel = 0; timer.Stop(); Refresh();
            }
            operation.Dispose();
        }
    }

    public void FinishListening()
    {
        if (!IsListening || IsFinishing || speech is null) return;
        IsFinishing = true; Status = "正在识别…"; Refresh();
        try
        {
            if (IsI2sMicrophone) microphone.Finish();
            else speech.FinishListening();
            input?.CancelAfter(TimeSpan.FromSeconds(IsI2sMicrophone ? IsCloudModule ? 90 : 45 : 8));
        }
        catch (Exception) { Stop(); Status = "无法结束语音识别，请重试。"; }
    }

    [RelayCommand]
    public void Stop()
    {
        input?.Cancel(); output?.Cancel();
        IsSpeaking = false; repliedAt = null;
        if (!disposed) Status = "已停止语音";
        Refresh();
    }

    private void ReplyCompleted(string text)
    {
        if (disposed || !chat.ReadReplies || !CanSpeak || IsListening) return;
        output?.Cancel();
        output = new CancellationTokenSource();
        var previous = speakingTask;
        speakingTask = SpeakReplyAsync(text, previous, output);
        Refresh();
    }

    private async Task SpeakReplyAsync(string text, Task previous, CancellationTokenSource operation)
    {
        try
        {
            await previous;
            operation.Token.ThrowIfCancellationRequested();
            text = ReadableText(text);
            if (text.Length == 0) return;
            Status = "正在准备朗读…";
            await speech!.SpeakAsync(text,
                playing => Post(operation, () =>
                {
                    IsSpeaking = playing;
                    if (playing) { lastWord = Stopwatch.GetTimestamp(); Status = "正在朗读 · 可停止或按住说下一句"; }
                }),
                () => Post(operation, () => lastWord = Stopwatch.GetTimestamp()), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            repliedAt = Stopwatch.GetTimestamp();
            Status = "朗读结束 · 按住可继续说话";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!disposed) Status = "朗读失败，请检查音频输出。" + " " + error.Message; }
        finally
        {
            if (ReferenceEquals(output, operation)) { output = null; IsSpeaking = false; Refresh(); }
            operation.Dispose();
        }
    }

    internal static string ReadableText(string text)
    {
        text = Regex.Replace(text, @"```[\s\S]*?(?:```|$)", " ");
        text = Regex.Replace(text, @"!?\[([^\]]*)\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s+", "");
        return Regex.Replace(text.Replace("**", "").Replace("`", ""), @"\s+", " ").Trim();
    }

    internal LcdRole? CaptureLcdRole()
    {
        if (IsListening) return LcdRole.Attentive;
        if (IsSpeaking) return Stopwatch.GetElapsedTime(lastWord).TotalMilliseconds < 300 ? LcdRole.Responding : LcdRole.Neutral;
        return repliedAt is { } at && Stopwatch.GetElapsedTime(at).TotalMilliseconds < 700 ? LcdRole.Happy : null;
    }

    private void Post(CancellationTokenSource operation, Action action) => Dispatcher.UIThread.Post(() =>
    {
        if (!disposed && (ReferenceEquals(input, operation) || ReferenceEquals(output, operation))
            && !operation.IsCancellationRequested) action();
    });

    private void Tick(object? sender, EventArgs args)
    {
        if (IsListening && !IsFinishing && Stopwatch.GetElapsedTime(listenStarted).TotalSeconds >= 30) FinishListening();
    }
    private void ChatChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (output is not null && (args.PropertyName == nameof(ChatViewModel.ReadReplies) && !chat.ReadReplies
            || args.PropertyName == nameof(ChatViewModel.IsBusy) && chat.IsBusy))
        {
            output.Cancel(); IsSpeaking = false; repliedAt = null; Status = "已停止朗读";
        }
        Refresh();
    }
    private void Refresh()
    {
        OnPropertyChanged(nameof(CanRecord)); OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanUseRecordButton));
        OnPropertyChanged(nameof(CanConfigureMicrophone));
        OnPropertyChanged(nameof(RecordLabel)); OnPropertyChanged(nameof(RecordHint));
        SaveRecognitionSettingsCommand.NotifyCanExecuteChanged(); ClearSpeechKeyCommand.NotifyCanExecuteChanged();
    }
    private static string DescribeAudio(byte[] pcm)
    {
        var samples = WhisperPcmRecognizer.ToSamples(pcm);
        var mean = samples.Average(v => (double)v);
        var rms = Math.Sqrt(samples.Average(v => Math.Pow(v - mean, 2)));
        var clipped = samples.Count(v => Math.Abs(v) >= 0.99) / (double)samples.Length;
        var level = 20 * Math.Log10(Math.Max(rms, 1.0 / 32768));
        var note = clipped > 0.02 ? " · 录音过载，请降低增益或离麦克风远些"
            : level < -50 ? " · 音量偏低，请靠近模块并检查供电" : "";
        return $"16kHz 单声道 · {samples.Length / 16000.0:0.0}s · 原始音量 {level:0}dBFS{note}";
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop(); timer.Stop(); timer.Tick -= Tick;
        chat.PropertyChanged -= ChatChanged; chat.ReplyCompleted -= ReplyCompleted;
        chat.ConversationChanged -= Stop;
        cloudApiKey = null;
        if (ownsCloudSpeech && cloudSpeech is IDisposable resource) resource.Dispose();
    }
}
