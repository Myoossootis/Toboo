using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toboo.Desktop.Services;

namespace Toboo.Desktop.ViewModels;

public sealed partial class VoiceViewModel
{
    private readonly IVoiceWakeService? wakeSpeech;
    private readonly ModuleWakeSpeech? moduleWakeSpeech;
    private CancellationTokenSource? wakeOperation;
    private Task wakeTask = Task.CompletedTask;
    private long? lastSpeechEnd;
    private bool wakeEnabled;
    private bool voiceSessionActive;
    private string? wakeError;
    [ObservableProperty] private string wakeStatus = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWakeCorrectionNotice))]
    private string wakeCorrectionNotice = "";

    public bool HasWakeCorrectionNotice => WakeCorrectionNotice.Length > 0;
    public string WakeButtonLabel => wakeEnabled ? "关闭语音唤醒" : "开启语音唤醒";
    public string WakeHint => $"说「{IVoiceWakeService.Phrase}」或「你好」开始连续对话。连接 Harness 后，说完停顿即可发送文字。说「结束语音对话」回到待机，或说「关闭语音唤醒」关闭麦克风监听。";
    public bool ShowWakeMeter => wakeEnabled;
    public bool CanToggleWake => !disposed && (wakeEnabled || WakeAvailable);
    private bool WakeAvailable => capabilities is { CanListen: true } && wakeSpeech is not null
        && (!IsI2sMicrophone || moduleWakeSpeech is not null && !string.IsNullOrWhiteSpace(MicrophonePort));
    private bool CanUseWake => !disposed && wakeEnabled && WakeAvailable && !Initializing
        && input is null && output is null && !chat.IsBusy && !chat.RemoteRunning && !chat.HasPending
        && string.IsNullOrWhiteSpace(chat.Draft);

    partial void OnWakeStatusChanged(string value) => OnPropertyChanged(nameof(VoiceActivityMessage));

    [RelayCommand(CanExecute = nameof(CanToggleWake))]
    private void ToggleWake()
    {
        wakeError = null;
        preferVoiceOutcome = false;
        SetWakeEnabled(!wakeEnabled);
        OnPropertyChanged(nameof(VoiceActivityMessage));
    }

    internal Task CloseWakeAsync()
    {
        SetWakeEnabled(false);
        return wakeTask;
    }

    private void SetWakeEnabled(bool enabled)
    {
        if (wakeEnabled != enabled)
        {
            wakeEnabled = enabled;
            OnPropertyChanged(nameof(WakeButtonLabel));
            OnPropertyChanged(nameof(ShowWakeMeter));
        }
        if (!enabled) voiceSessionActive = false;
        RefreshWake();
    }

    private void WakeInputChanged()
    {
        wakeError = null;
        voiceSessionActive = false;
        ResetWakeMeter();
        preferVoiceOutcome = false;
        if (wakeEnabled && wakeOperation is not null) WakeStatus = "正在切换麦克风…";
        wakeOperation?.Cancel();
    }

    private void RefreshWake()
    {
        ToggleWakeCommand.NotifyCanExecuteChanged();
        if (!CanUseWake)
        {
            wakeOperation?.Cancel();
            ResetWakeMeter();
            WakeStatus = wakeError
                ?? (capabilities?.CanListen == false ? "唤醒后的转写需要 Windows 中文语音识别组件。"
                : IsI2sMicrophone && string.IsNullOrWhiteSpace(MicrophonePort) ? "请在设备页选择 ESP32-S3 麦克风串口。"
                : !wakeEnabled ? ""
                : chat.IsBusy || chat.RemoteRunning ? "等待回复 · 回复后" + (voiceSessionActive ? "继续听" : "恢复唤醒")
                : chat.HasPending ? "发送状态待确认 · 语音已暂停"
                : !string.IsNullOrWhiteSpace(chat.Draft) ? "正在编辑文字 · 语音已暂停"
                : "麦克风暂未监听");
            return;
        }
        if (wakeOperation is not null || chat.VoiceInputActive) return;
        preferVoiceOutcome = false;
        WakeStatus = voiceSessionActive ? "正在准备收音…" : "正在准备语音唤醒…";
        wakeOperation = new CancellationTokenSource();
        wakeTask = RunWakeAsync(wakeOperation);
    }

    private void ResetWakeMeter()
    {
        if (inputMeterMode is InputMeterMode.Wake or InputMeterMode.WakeRecording)
            SetInputMeterMode(InputMeterMode.Off);
    }

    private async Task RunWakeAsync(CancellationTokenSource operation)
    {
        var recorded = false;
        var captureCompleted = false;
        var modulePort = IsI2sMicrophone ? MicrophonePort : null;
        var cancellation = operation.Token;
        try
        {
            // The task must be registered before any status or capture callbacks can reenter the UI.
            await Task.Yield();
            if (lastSpeechEnd is { } ended)
            {
                var delay = TimeSpan.FromMilliseconds(500) - Stopwatch.GetElapsedTime(ended);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            bool IsCurrent(bool capturing) => !disposed && !captureCompleted && recorded == capturing
                && ReferenceEquals(wakeOperation, operation) && !cancellation.IsCancellationRequested;
            void SetPrompt(bool capturing, string prompt, InputMeterMode meter) => Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrent(capturing)) return;
                SetInputMeterMode(meter);
                WakeStatus = prompt;
            });
            void ReportLevel(bool capturing, double level) => Dispatcher.UIThread.Post(() =>
            {
                if (IsCurrent(capturing))
                    UpdateInputMeter(capturing ? InputMeterMode.WakeRecording : InputMeterMode.Wake, level);
            });
            if (!voiceSessionActive)
            {
                void KeywordReady() => SetPrompt(false, $"等待唤醒 · {IVoiceWakeService.Phrase} / 你好", InputMeterMode.Wake);
                void KeywordLevel(double level) => ReportLevel(false, level);
                var detected = modulePort is null ? await wakeSpeech!.WaitForWakeAsync(KeywordReady, cancellation, level: KeywordLevel)
                    : await moduleWakeSpeech!.WaitForWakeAsync(modulePort, KeywordReady, cancellation, KeywordLevel);
                cancellation.ThrowIfCancellationRequested();
                if (!detected) throw new IOException("唤醒监听已结束。");
                voiceSessionActive = true;
            }
            if (!CanUseWake || chat.VoiceInputActive) return;

            recorded = true;
            ResetWakeMeter();
            preferVoiceOutcome = false;
            IsListening = true;
            WakeStatus = "正在准备收音…";
            Refresh();
            var dictationPrompt = chat.IsConnected && chat.SelectedModel is not null
                ? "正在听 · 说完停一下，文字会自动发送" : "正在听 · 说完停一下，文字会留在输入框";
            void DictationReady() => SetPrompt(true, dictationPrompt, InputMeterMode.WakeRecording);
            void DictationRecognizing() => SetPrompt(true, "正在识别…", InputMeterMode.Off);
            void DictationLevel(double level) => ReportLevel(true, level);
            var text = (modulePort is null ? await wakeSpeech!.ListenOnceAsync(DictationReady, cancellation, level: DictationLevel,
                    recognizing: DictationRecognizing)
                : await moduleWakeSpeech!.ListenOnceAsync(modulePort, DictationReady, cancellation, DictationLevel, DictationRecognizing)).Trim();
            cancellation.ThrowIfCancellationRequested();
            if (!CanUseWake) return;
            captureCompleted = true;
            ResetWakeMeter();
            if (text.Length == 0)
            {
                voiceSessionActive = false;
                return;
            }
            if (text.Length > 4000)
            {
                ShowVoiceOutcome("这段语音太长，请分成几句说。");
                return;
            }
            OriginalTranscript = text;
            var normalized = TranscriptCorrection.NormalizeTerms(text);
            AudioDiagnostics = "";
            WakeCorrectionNotice = "";
            CorrectedTranscript = normalized;
            var command = VoiceCommands.Match(text);
            if (command != VoiceCommand.None)
            {
                await ExecuteWakeCommandAsync(command, corrected: false, cancellation);
                return;
            }
            if (recognitionStore.Current.CorrectWakeText)
            {
                if (TranscriptCorrection.RestoreKnownReadingRequest(text) is { } restored)
                {
                    CorrectedTranscript = restored;
                    WakeCorrectionNotice = "本地纠错 · 已校正朗读请求";
                }
                else CorrectedTranscript = await CorrectWakeTextAsync(normalized, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!CanUseWake) return;
                command = VoiceCommands.Match(CorrectedTranscript);
                if (command != VoiceCommand.None)
                {
                    if (VoiceCommands.SupportsCorrection(text, CorrectedTranscript, command))
                        await ExecuteWakeCommandAsync(command, corrected: true, cancellation);
                    else WakeCorrectionNotice = "语音指令不明确 · 请重新说一遍";
                    return;
                }
            }
            else WakeCorrectionNotice = "连续语音纠错未开启 · 使用本地转写";
            IsListening = false;
            chat.Draft = CorrectedTranscript;
            if (chat.CanSend)
            {
                Status = "已识别，正在发送…";
                chat.SendCommand.Execute(null);
            }
            else ShowVoiceOutcome("已填入输入框，请核对后发送。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!disposed && !cancellation.IsCancellationRequested)
            {
                preferVoiceOutcome = false;
                wakeError = modulePort is null ? "语音唤醒已关闭，请检查默认麦克风后重试。"
                    : "模块语音唤醒已关闭，请检查 ESP32-S3 串口、USB 和采音固件。";
                Debug.WriteLine(error);
                SetWakeEnabled(false);
            }
        }
        finally
        {
            ResetWakeMeter();
            if (recorded && input is null) IsListening = false;
            wakeOperation = null;
            operation.Dispose();
            Refresh();
        }
    }

    private async Task ExecuteWakeCommandAsync(VoiceCommand command, bool corrected, CancellationToken cancellation)
    {
        if (TryHandleVoiceSessionCommand(command))
        {
            if (corrected) WakeCorrectionNotice = "DeepSeek 纠错 · 已执行语音指令";
            return;
        }
        var result = localCommandHandler is { } handle
            ? await handle(VoiceCommands.FollowText(command), cancellation)
            : null;
        cancellation.ThrowIfCancellationRequested();
        WakeCorrectionNotice = result ?? "当前无法执行跟随指令";
    }

    private async Task<string> CorrectWakeTextAsync(string original, CancellationToken cancellation)
    {
        if (cloudApiKey is not { Length: > 0 } key)
        {
            WakeCorrectionNotice = "连续语音纠错需要保存 DeepSeek API Key · 使用本地转写";
            return original;
        }
        WakeStatus = "正在用 DeepSeek 校正转写…";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var corrected = await cloudSpeech.CorrectAsync(original, key, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (TranscriptCorrection.IsConservative(original, corrected))
            {
                corrected = corrected.Trim();
                WakeCorrectionNotice = corrected == original ? "DeepSeek 纠错 · 模型保留原文" : "DeepSeek 纠错 · 已校正文字";
                return corrected;
            }
            WakeCorrectionNotice = "DeepSeek 纠错改动超出允许范围 · 保留原文";
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            WakeCorrectionNotice = "DeepSeek 纠错超时 · 保留原文";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or TimeoutException)
        {
            cancellation.ThrowIfCancellationRequested();
            WakeCorrectionNotice = "DeepSeek 纠错请求失败 · 保留原文";
        }
        return original;
    }

    private bool TryHandleVoiceSessionCommand(VoiceCommand command)
    {
        switch (command)
        {
            case VoiceCommand.DisableWake:
                SetWakeEnabled(false);
                ShowVoiceOutcome("麦克风监听已关闭。");
                return true;
            case VoiceCommand.EndConversation:
                voiceSessionActive = false;
                Status = wakeEnabled ? "语音对话已结束，等待唤醒。" : "语音对话已结束。";
                return true;
            default:
                return false;
        }
    }
}
