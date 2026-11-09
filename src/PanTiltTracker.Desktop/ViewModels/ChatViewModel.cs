using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PanTiltTracker.Desktop.Services;

namespace PanTiltTracker.Desktop.ViewModels;

public sealed partial class ChatMessageItem(string id, bool isUser) : ObservableObject
{
    public string Id { get; } = id;
    public bool IsUser { get; } = isUser;
    public string Author => IsUser ? "我" : "toboo";
    [ObservableProperty] private string text = "";
    [ObservableProperty] private string status = "";
}

/// <summary>Explicit sends only. Uncertain admission keeps a durable UUID/payload until reconciled or retried.</summary>
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly ITobooChatClient client;
    private readonly ChatStateStore store;
    private readonly Func<ChatExpression?> expression;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer;
    private readonly Dictionary<string, ChatTurn> turns = [];
    private readonly Dictionary<string, int> turnOrder = [];
    private ChatClientState state = new();
    private bool stateLoaded, polling, disposed, closing, loadingPreferences;
    private string? sessionId, currentRequestId;
    private long revision = -1;
    private int before;
    public event Action<bool>? Updated;
    public ObservableCollection<ChatMessageItem> Messages { get; } = [];
    public ObservableCollection<ChatModelChoice> Models { get; } = [];
    public ObservableCollection<ChatSessionChoice> Sessions { get; } = [];
    [ObservableProperty] private string draft = "";
    [ObservableProperty] private string connectionStatus = "尚未连接 Harness";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool remoteRunning;
    [ObservableProperty] private bool isStopping;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private bool includeExpression;
    [ObservableProperty] private int styleIndex;
    [ObservableProperty] private ChatModelChoice? selectedModel;
    [ObservableProperty] private ChatSessionChoice? selectedSession;
    public bool HasMessages => Messages.Count > 0;
    public bool HasError => Error.Length > 0;
    public bool HasPending => state.Pending is not null;
    public bool CanConnect => !IsBusy && !closing;
    public bool CanSend => IsConnected && !IsBusy && !RemoteRunning && !HasPending && !closing && SelectedModel is not null && Draft.Trim().Length > 0 && Draft.Length <= 4000;
    public bool CanRetry => IsConnected && HasPending && !IsBusy && !closing;
    public bool CanStop => !IsStopping && !closing && sessionId is not null && currentRequestId is not null && (RemoteRunning || HasPending || IsBusy);
    public bool CanChoose => IsConnected && !IsBusy && !RemoteRunning && !HasPending && !closing;
    public bool CanOpen => CanChoose && SelectedSession is not null;
    public bool CanLoadOlder => CanChoose && HasMore;
    public string SessionTitle => sessionId is null ? "新对话" : Sessions.FirstOrDefault(s => s.Id == sessionId)?.Title ?? "toboo 独立会话";
    public string PrivacyHint => IncludeExpression ? "发送文字和有效表情摘要 · 不发送画面" : "仅发送文字 · 历史保存在本机独立目录";
    private string Style => StyleIndex switch { 1 => "gentle", 2 => "concise", _ => "natural" };

    public ChatViewModel(ITobooChatClient? client = null, ChatStateStore? store = null, Func<ChatExpression?>? expression = null)
    {
        this.client = client ?? new TobooChatClient(); this.store = store ?? new(); this.expression = expression ?? (() => null);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += Tick; timer.Start();
    }
    partial void OnDraftChanged(string value) => RefreshCommands();
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsConnectedChanged(bool value) => RefreshCommands();
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnRemoteRunningChanged(bool value) => RefreshCommands();
    partial void OnIsStoppingChanged(bool value) => RefreshCommands();
    partial void OnHasMoreChanged(bool value) => RefreshCommands();
    partial void OnSelectedSessionChanged(ChatSessionChoice? value) => RefreshCommands();
    partial void OnSelectedModelChanged(ChatModelChoice? value) { SavePreferences(); RefreshCommands(); }
    partial void OnIncludeExpressionChanged(bool value) { SavePreferences(); OnPropertyChanged(nameof(PrivacyHint)); }
    partial void OnStyleIndexChanged(int value) => SavePreferences();
    private void RefreshCommands()
    {
        foreach (var name in new[] { nameof(CanConnect), nameof(CanSend), nameof(CanRetry), nameof(CanStop), nameof(CanChoose), nameof(CanOpen), nameof(CanLoadOlder), nameof(HasPending), nameof(SessionTitle) }) OnPropertyChanged(name);
        ConnectCommand.NotifyCanExecuteChanged(); SendCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged(); NewChatCommand.NotifyCanExecuteChanged(); OpenSessionCommand.NotifyCanExecuteChanged(); LoadOlderCommand.NotifyCanExecuteChanged();
    }
    private void SavePreferences()
    {
        if (!stateLoaded || loadingPreferences || disposed) return;
        try { Persist(state with { Provider = SelectedModel?.Provider ?? state.Provider, Model = SelectedModel?.Model ?? state.Model, Style = Style, IncludeExpression = IncludeExpression }); }
        catch (ChatConnectionException error) { Error = error.Message; IsConnected = false; }
    }
    private void Persist(ChatClientState value) { store.Save(value); state = value; RefreshCommands(); }
    private void Failed(Exception error)
    {
        Error = error is ChatConnectionException chat ? chat.Message : "对话操作失败，请重新连接后检查。";
        if (error is not ChatConnectionException { Code: not ("CONNECTION" or "PROTOCOL") }) IsConnected = false;
    }
    [RelayCommand(CanExecute = nameof(CanConnect))]
    public async Task ConnectAsync()
    {
        if (!CanConnect) return;
        IsBusy = true; Error = ""; ConnectionStatus = "正在连接 Harness…";
        try
        {
            if (!stateLoaded)
            {
                store.AcquireWriter();
                state = store.Load(); stateLoaded = true; sessionId = state.SessionId;
                loadingPreferences = true;
                try { IncludeExpression = state.IncludeExpression; StyleIndex = state.Style switch { "gentle" => 1, "concise" => 2, _ => 0 }; Draft = state.Pending?.Text ?? Draft; }
                finally { loadingPreferences = false; }
                currentRequestId = state.Pending?.RequestId;
            }
            var status = await client.CallAsync<ChatStatus>(new { action = "status" }, lifetime.Token);
            if (status.Service != "toboo-chat" || status.Protocol != 1 || status.Version != "1.0.2" || !status.Compatible || status.Tools || status.Isolation != "owned-transcripts-direct-llm")
                throw new ChatConnectionException("桥接版本或隔离能力不兼容。安装更新后请完全退出并重启 Harness，再重新连接。", "PROTOCOL");
            var catalog = await client.CallAsync<ChatCatalog>(new { action = "models" }, lifetime.Token);
            loadingPreferences = true;
            try
            {
                Models.Clear();
                foreach (var group in catalog.Groups) foreach (var model in group.Models)
                    Models.Add(new(group.Id, model.Id, model.Name + " · " + group.Name));
                SelectedModel = state.Provider is not null && state.Model is not null
                    ? Models.FirstOrDefault(m => m.Provider == state.Provider && m.Model == state.Model)
                    : Models.FirstOrDefault(m => m.Model.Contains("flash", StringComparison.OrdinalIgnoreCase)) ?? Models.FirstOrDefault();
            }
            finally { loadingPreferences = false; }
            await RefreshSessionsAsync(); IsConnected = true;
            if (sessionId is not null)
            {
                try { await RefreshAsync(reset: true); }
                catch (ChatConnectionException error) when (error.Code == "NOT_FOUND" && HasPending) { RemoteRunning = false; }
            }
            ConnectionStatus = Models.Count == 0 ? "已连接 · 请在 Harness 配置模型" : "Harness 已连接";
            if (catalog.Failures > 0) Error = "部分模型目录暂不可用，可使用当前列表中的模型。";
            if (Models.Count > 0 && SelectedModel is null) Error = "上次选择的模型已不可用，请手动选择；没有自动切换模型。";
            if (HasPending) Error = "上次发送结果尚未确认。可重试原请求或停止；不会自动重复发送。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Failed(error); ConnectionStatus = "Harness 未连接"; }
        finally { IsBusy = false; }
    }
    private async Task RefreshSessionsAsync()
    {
        var list = await client.CallAsync<ChatSessions>(new { action = "list" }, lifetime.Token);
        Sessions.Clear(); foreach (var item in list.Items) Sessions.Add(item);
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == sessionId);
        OnPropertyChanged(nameof(SessionTitle));
    }
    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task SendAsync()
    {
        if (!CanSend) return;
        sessionId ??= Guid.NewGuid().ToString();
        var request = new ChatSendRequest("send", sessionId, Guid.NewGuid().ToString(), Draft,
            SelectedModel!.Provider, SelectedModel.Model, Style, IncludeExpression ? expression() : null);
        try { Persist(state with { SessionId = sessionId, Pending = request, Provider = SelectedModel.Provider, Model = SelectedModel.Model, Style = Style, IncludeExpression = IncludeExpression }); }
        catch (ChatConnectionException error) { Failed(error); return; }
        currentRequestId = request.RequestId;
        await SubmitPendingAsync();
    }
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => SubmitPendingAsync();
    private async Task SubmitPendingAsync()
    {
        if (state.Pending is not { } request || closing) return;
        IsBusy = true; Error = "";
        try
        {
            await client.CallAsync<System.Text.Json.JsonElement>(new { action = "new", sessionId = request.SessionId }, lifetime.Token);
            var accepted = await client.CallAsync<ChatAcknowledgement>(request, lifetime.Token);
            if (!accepted.Accepted || accepted.RequestId != request.RequestId) throw new ChatConnectionException("发送确认不匹配，请重新连接检查。", "PROTOCOL");
            Persist(state with { Pending = null });
            if (Draft == request.Text) Draft = "";
            RemoteRunning = true;
            await RefreshAsync(); await RefreshSessionsAsync();
            Updated?.Invoke(true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (error is ChatConnectionException { Code: "CANCELLED" }) Persist(state with { Pending = null });
            Failed(error);
        }
        finally { IsBusy = false; }
    }
    private async void Tick(object? sender, EventArgs args)
    {
        if (disposed || closing || polling || IsBusy || !IsConnected || !RemoteRunning) return;
        polling = true;
        try { await RefreshAsync(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Failed(error); ConnectionStatus = "连接中断 · 可重新连接核对"; }
        finally { polling = false; }
    }
    internal async Task RefreshAsync(bool reset = false)
    {
        if (sessionId is null) return;
        var id = sessionId;
        var view = await client.CallAsync<ChatView>(new { action = "view", sessionId = id, revision = reset ? -1 : revision }, lifetime.Token);
        if (closing || id != sessionId || view.NotModified) return;
        if (view.SessionId != id || view.Turns is null) throw new ChatConnectionException("会话回复不匹配。", "PROTOCOL");
        if (reset) { turns.Clear(); turnOrder.Clear(); Messages.Clear(); before = view.Before; }
        else before = turns.Count == 0 ? view.Before : Math.Min(before, view.Before);
        revision = view.Revision; HasMore = before > 0;
        for (var i = 0; i < view.Turns.Length; i++) { var turn = view.Turns[i]; turns[turn.RequestId] = turn; turnOrder[turn.RequestId] = view.Before + i; }
        var active = view.Turns.LastOrDefault(t => t.State is "running" or "cancelling");
        RemoteRunning = active is not null; currentRequestId = active?.RequestId ?? state.Pending?.RequestId;
        if (state.Pending is { } pending && turns.TryGetValue(pending.RequestId, out var admitted))
        {
            if (admitted.Text != pending.Text || admitted.Provider != pending.Provider || admitted.Model != pending.Model)
                throw new ChatConnectionException("上次发送记录不匹配，未继续发送。", "PROTOCOL");
            Persist(state with { Pending = null });
            if (Draft == pending.Text) Draft = "";
            currentRequestId = active?.RequestId;
        }
        RenderMessages();
    }
    private void RenderMessages()
    {
        var rows = Messages.ToDictionary(m => m.Id);
        foreach (var turn in turns.Values.OrderBy(t => turnOrder[t.RequestId]))
        {
            foreach (var user in new[] { true, false })
            {
                var id = turn.RequestId + (user ? "-user" : "-reply");
                if (!rows.TryGetValue(id, out var row)) { row = new(id, user); Messages.Add(row); }
                row.Text = user ? turn.Text : turn.Reply.Length > 0 ? turn.Reply : turn.State is "running" or "cancelling" ? "正在组织回复…" : "本次没有生成回复。";
                row.Status = user ? "" : turn.Error?.Message ?? turn.State switch { "running" => "正在回复", "cancelling" => "正在停止", "truncated" => "回复达到长度上限", _ => "" };
            }
        }
        OnPropertyChanged(nameof(HasMessages)); Updated?.Invoke(false);
    }
    [RelayCommand(CanExecute = nameof(CanStop))]
    public async Task StopAsync()
    {
        if (!CanStop || sessionId is null || currentRequestId is null) return;
        IsStopping = true; Error = "";
        try
        {
            if (HasPending) await client.CallAsync<System.Text.Json.JsonElement>(new { action = "new", sessionId }, lifetime.Token);
            await client.CallAsync<System.Text.Json.JsonElement>(new { action = "stop", sessionId, requestId = currentRequestId }, lifetime.Token);
            if (HasPending) Persist(state with { Pending = null });
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Failed(error); }
        finally { IsStopping = false; }
    }
    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void NewChat()
    {
        try
        {
            Persist(state with { SessionId = null }); sessionId = null; currentRequestId = null; revision = -1;
            turns.Clear(); turnOrder.Clear(); Messages.Clear(); HasMore = false; Error = ""; SelectedSession = null;
            OnPropertyChanged(nameof(HasMessages)); RefreshCommands();
        }
        catch (ChatConnectionException error) { Failed(error); }
    }
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenSessionAsync()
    {
        if (!CanOpen) return;
        IsBusy = true; Error = "";
        try { sessionId = SelectedSession!.Id; Persist(state with { SessionId = sessionId }); await RefreshAsync(reset: true); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Failed(error); }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanLoadOlder))]
    private async Task LoadOlderAsync()
    {
        if (!CanLoadOlder || sessionId is null) return;
        IsBusy = true;
        try
        {
            var view = await client.CallAsync<ChatView>(new { action = "view", sessionId, before }, lifetime.Token);
            if (view.SessionId != sessionId || view.Turns is null) throw new ChatConnectionException("历史回复不匹配。", "PROTOCOL");
            before = view.Before; HasMore = view.HasMore;
            for (var i = 0; i < view.Turns.Length; i++) { var turn = view.Turns[i]; turns[turn.RequestId] = turn; turnOrder[turn.RequestId] = view.Before + i; }
            Messages.Clear(); RenderMessages();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Failed(error); }
        finally { IsBusy = false; }
    }
    public async Task ShutdownAsync()
    {
        if (closing) return;
        closing = true; timer.Stop(); RefreshCommands(); lifetime.Cancel();
        if (sessionId is not null && currentRequestId is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await client.CallAsync<System.Text.Json.JsonElement>(new { action = "stop", sessionId, requestId = currentRequestId }, timeout.Token); }
            catch (Exception) { /* Durable request ID and the bridge lease cover uncertain shutdown. */ }
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; closing = true; timer.Stop(); timer.Tick -= Tick;
        lifetime.Cancel(); client.Dispose(); store.Dispose(); lifetime.Dispose(); Updated = null;
    }
}
