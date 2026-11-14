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
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? input, output;
    private Task listeningTask = Task.CompletedTask, speakingTask = Task.CompletedTask;
    private long listenStarted, lastWord;
    private long? repliedAt;
    private bool disposed;
    [ObservableProperty] private bool initializing;
    [ObservableProperty] private bool canListen;
    [ObservableProperty] private bool canSpeak;
    [ObservableProperty] private bool isListening;
    [ObservableProperty] private bool isFinishing;
    [ObservableProperty] private bool isSpeaking;
    [ObservableProperty] private string status = "正在检查本机语音…";
    [ObservableProperty] private string inputDescription = "使用系统默认麦克风";
    [ObservableProperty] private string outputDescription = "使用系统默认音频输出";
    public bool CanRecord => !disposed && CanListen && !IsListening && chat.IsConnected && !chat.IsBusy
        && !chat.RemoteRunning && !chat.HasPending && chat.SelectedModel is not null && string.IsNullOrWhiteSpace(chat.Draft);
    public bool CanStop => IsListening || output is not null;
    public bool CanUseRecordButton => CanRecord || IsListening;
    public string RecordLabel => IsFinishing ? "正在识别…" : IsListening ? "松开发送" : "按住说话";
    public string RecordHint => !CanListen ? InputDescription : !chat.IsConnected ? "连接 Harness 后可语音对话"
        : !string.IsNullOrWhiteSpace(chat.Draft) ? "先发送或清空输入框，再按住说话" : "按住说话，松开发送；Esc 取消";

    internal VoiceViewModel(ChatViewModel chat, ISpeechService? speech = null)
    {
        this.chat = chat;
        this.speech = speech ?? (OperatingSystem.IsWindows() ? new WindowsSpeechService() : null);
        chat.PropertyChanged += ChatChanged;
        chat.ReplyCompleted += ReplyCompleted;
        chat.ConversationChanged += Stop;
        timer.Tick += Tick;
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (disposed || Initializing || CanStop) return;
        Initializing = true;
        try
        {
            var capabilities = speech is null
                ? new SpeechCapabilities(false, false, "本机语音需要 Windows", "本机语音需要 Windows")
                : await speech.InspectAsync(CancellationToken.None);
            if (disposed) return;
            CanListen = capabilities.CanListen; CanSpeak = capabilities.CanSpeak;
            InputDescription = capabilities.Input; OutputDescription = capabilities.Output;
            Status = CanListen ? "按住说话，松开发送" : InputDescription;
        }
        catch (Exception)
        {
            if (!disposed)
            {
                CanListen = false; CanSpeak = false;
                InputDescription = "请检查 Windows 中文语音组件";
                Status = "无法初始化语音，请检查 Windows 中文语音组件后重试。";
            }
        }
        finally { Initializing = false; Refresh(); }
    }

    public Task BeginListeningAsync()
    {
        if (!CanRecord || speech is null) return Task.CompletedTask;
        Stop();
        repliedAt = null;
        input = new CancellationTokenSource();
        IsListening = true; IsFinishing = false; chat.VoiceInputActive = true;
        listenStarted = Stopwatch.GetTimestamp();
        Status = "正在准备麦克风…";
        timer.Start(); Refresh();
        listeningTask = ListenAndSendAsync(input);
        return listeningTask;
    }

    private async Task ListenAndSendAsync(CancellationTokenSource operation)
    {
        try
        {
            await speakingTask;
            operation.Token.ThrowIfCancellationRequested();
            if (IsFinishing) return;
            var text = await speech!.ListenAsync(() => Post(operation, () =>
            {
                if (!IsFinishing) Status = "正在听… 松开发送";
            }), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) { Status = "没有听清，请按住后再说一次。"; return; }
            if (text.Length > 4000) { Status = "这段语音太长，请分成几句说。"; return; }
            // The draft stays editable after transcription, including on an uncertain send.
            var released = IsFinishing;
            chat.Draft = text.Trim();
            chat.VoiceInputActive = false;
            IsListening = false; IsFinishing = false; timer.Stop(); Refresh();
            Status = released && chat.CanSend ? "已识别，正在发送…" : "语音已填入输入框，可编辑后发送。";
            if (released && chat.CanSend)
            {
                await chat.SendAsync();
                if (output is null) Status = chat.HasPending || chat.HasError ? "请查看对话发送状态。" : "已发送 · 等待回复";
            }
        }
        catch (OperationCanceledException) { if (!disposed) Status = "本次语音已取消"; }
        catch (Exception error)
        {
            if (!disposed) Status = "语音输入失败，请检查默认麦克风和中文识别组件。" + " " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(input, operation))
            {
                input = null; IsListening = false; IsFinishing = false;
                chat.VoiceInputActive = false; timer.Stop(); Refresh();
            }
            operation.Dispose();
        }
    }

    public void FinishListening()
    {
        if (!IsListening || IsFinishing || speech is null) return;
        IsFinishing = true; Status = "正在识别…"; Refresh();
        try { speech.FinishListening(); input?.CancelAfter(TimeSpan.FromSeconds(8)); }
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
        OnPropertyChanged(nameof(RecordLabel)); OnPropertyChanged(nameof(RecordHint));
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop(); timer.Stop(); timer.Tick -= Tick;
        chat.PropertyChanged -= ChatChanged; chat.ReplyCompleted -= ReplyCompleted;
        chat.ConversationChanged -= Stop;
    }
}
