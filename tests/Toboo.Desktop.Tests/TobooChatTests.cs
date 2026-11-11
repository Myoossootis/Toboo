using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Toboo.Desktop.Services;
using Toboo.Desktop.ViewModels;
using Toboo.Core;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class TobooChatTests
{
    private static string StateFile() => Path.Combine(Path.GetTempPath(), "toboo-chat-tests", Guid.NewGuid().ToString("N"), "client.json");
    private sealed class FakeClient : ITobooChatClient
    {
        public int Sends;
        public bool LoseAcknowledgement, Incompatible, MissingModel;
        public bool StreamReply;
        public readonly List<JsonElement> Calls = [];
        public readonly List<ChatTurn> Turns = [];
        public Action? BeforeStop { get; set; }
        public string? Session;
        public Task<T> CallAsync<T>(object request, CancellationToken cancellation = default)
        {
            var json = JsonSerializer.SerializeToElement(request, TobooChatClient.Json); Calls.Add(json);
            object result = json.GetProperty("action").GetString() switch {
                "status" => new ChatStatus("toboo-chat", 1, true, Incompatible, "owned-transcripts-direct-llm", "1.0.2"),
                "models" => new ChatCatalog([new("provider", "DeepSeek", [new(MissingModel ? "other" : "flash", "Flash")])], 0),
                "list" => new ChatSessions(Session is null ? [] : [new(Session, "测试会话", 1)]),
                "new" => New(),
                "send" => Send(),
                "view" => new ChatView(Session, Calls.Count, false, false, 0, [.. Turns]),
                "stop" => Stop(),
                _ => throw new InvalidOperationException(),
            };
            return Task.FromResult(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result, TobooChatClient.Json), TobooChatClient.Json)!);
            object New() { Session = json.GetProperty("sessionId").GetString(); return new { sessionId = Session }; }
            object Send()
            {
                var body = json.Deserialize<ChatSendRequest>(TobooChatClient.Json)!;
                var duplicate = Turns.Any(t => t.RequestId == body.RequestId);
                if (!duplicate)
                {
                    Sends++;
                    Turns.Add(new(body.RequestId, body.Text, StreamReply ? "" : "你好，我是 toboo。", body.Provider,
                        body.Model, StreamReply ? "running" : "completed", Sends, null));
                }
                if (LoseAcknowledgement) { LoseAcknowledgement = false; throw new ChatConnectionException("连接超时", "CONNECTION"); }
                return new ChatAcknowledgement(true, duplicate, body.RequestId);
            }
            object Stop()
            {
                BeforeStop?.Invoke();
                var id = json.GetProperty("requestId").GetString();
                for (var i = 0; i < Turns.Count; i++) if (Turns[i].RequestId == id) Turns[i] = Turns[i] with { State = "cancelled" };
                return new { stopped = true };
            }
        }
        public void Dispose() { }
    }
    [AvaloniaFact]
    public async Task LcdReactsToTypingThinkingStreamingAndCompletedReply()
    {
        var client = new FakeClient { StreamReply = true };
        using var chat = new ChatViewModel(client, new(StateFile()));
        await chat.ConnectAsync();
        Assert.Null(chat.CaptureLcdRole());
        chat.Draft = "你好";
        Assert.Equal(LcdRole.Attentive, chat.CaptureLcdRole());
        await chat.SendAsync();
        Assert.Equal(LcdRole.Thinking, chat.CaptureLcdRole());
        client.Turns[0] = client.Turns[0] with { Reply = "你好，我是 toboo。" };
        await chat.RefreshAsync();
        Assert.Equal(LcdRole.Responding, chat.CaptureLcdRole());
        client.Turns[0] = client.Turns[0] with { State = "completed" };
        await chat.RefreshAsync();
        Assert.False(chat.RemoteRunning);
        Assert.Equal(LcdRole.Responding, chat.CaptureLcdRole());
        chat.NewChatCommand.Execute(null);
        Assert.Null(chat.CaptureLcdRole());
    }

    [AvaloniaFact]
    public async Task ChatIsExplicitSendOnlyAndUsesTheSelectedRouteWithoutGlobalModelCalls()
    {
        var client = new FakeClient(); var file = StateFile();
        using var chat = new ChatViewModel(client, new(file));
        await chat.ConnectAsync(); Assert.Equal(0, client.Sends); Assert.True(chat.IsConnected);
        chat.Draft = "今天项目完成了"; Assert.True(chat.CanSend); await chat.SendAsync();
        Assert.Equal(1, client.Sends); Assert.Equal(2, chat.Messages.Count); Assert.Equal("", chat.Draft);
        Assert.False(chat.RemoteRunning); Assert.False(chat.HasPending);
        Assert.DoesNotContain(client.Calls, c => c.GetProperty("action").GetString() == "model");
        var send = client.Calls.Single(c => c.GetProperty("action").GetString() == "send");
        Assert.Equal("provider", send.GetProperty("provider").GetString()); Assert.Equal(JsonValueKind.Null, send.GetProperty("expression").ValueKind);
        Assert.Null(new ChatStateStore(file).Load().Pending);
    }
    [AvaloniaFact]
    public async Task LostAcknowledgementPersistsRequestAndReconnectReconcilesWithoutSendingAgain()
    {
        var client = new FakeClient { LoseAcknowledgement = true }; var file = StateFile();
        using (var chat = new ChatViewModel(client, new(file)))
        {
            await chat.ConnectAsync(); chat.Draft = "不要重复这条消息"; await chat.SendAsync();
            Assert.True(chat.HasPending); Assert.Equal("不要重复这条消息", chat.Draft); Assert.False(chat.IsConnected);
        }
        using var restored = new ChatViewModel(client, new(file));
        await restored.ConnectAsync(); Assert.False(restored.HasPending); Assert.Equal(1, client.Sends); Assert.Equal(2, restored.Messages.Count);
        Assert.Equal("", restored.Draft);
    }
    [AvaloniaFact]
    public async Task ExplicitRetryPreservesRequestIdentityAndOriginalExpressionPayload()
    {
        var client = new FakeClient { LoseAcknowledgement = true }; var count = 0;
        using var chat = new ChatViewModel(client, new(StateFile()), () => { count++; return new("happy", .9, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); });
        await chat.ConnectAsync(); chat.IncludeExpression = true; chat.Draft = "测试"; await chat.SendAsync();
        Assert.True(chat.HasPending);
        // Restore the connection flag without reconciliation, as if status recovery preceded a view read.
        chat.IsConnected = true; await chat.RetryCommand.ExecuteAsync(null);
        var sends = client.Calls.Where(c => c.GetProperty("action").GetString() == "send").ToArray();
        Assert.Equal(2, sends.Length); Assert.Equal(sends[0].GetRawText(), sends[1].GetRawText());
        Assert.Equal(1, client.Sends); Assert.Equal(1, count);
    }
    [AvaloniaFact]
    public async Task MissingSelectedModelDoesNotSilentlySwitchAndIncompatibleBridgeCannotSend()
    {
        var store = new ChatStateStore(StateFile()); store.Save(new(Provider: "provider", Model: "removed"));
        using var chat = new ChatViewModel(new FakeClient { MissingModel = true }, store);
        await chat.ConnectAsync(); chat.Draft = "测试"; Assert.Null(chat.SelectedModel); Assert.False(chat.CanSend);
        using var incompatible = new ChatViewModel(new FakeClient { Incompatible = true }, new(StateFile()));
        await incompatible.ConnectAsync(); Assert.False(incompatible.IsConnected); Assert.Contains("不兼容", incompatible.Error);
    }
    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(980, 700)]
    public async Task ConnectedConversationRendersAndNavigationDoesNotResend(int width, int height)
    {
        var client = new FakeClient(); using var chat = new ChatViewModel(client, new(StateFile()));
        using var tracking = new MainViewModel(); var window = new MainWindow(tracking, chat) { Width = width, Height = height };
        try
        {
            window.Show(); await chat.ConnectAsync(); chat.Draft = "这是一条确定性界面测试消息。"; await chat.SendAsync();
            await Task.Delay(60);
            var directory = Path.Combine(Workspace(), "artifacts", "harness-chat-preview"); Directory.CreateDirectory(directory);
            using (var screenshot = window.CaptureRenderedFrame()) { Assert.NotNull(screenshot); screenshot.Save(Path.Combine(directory, $"chat-fixture-{width}.png")); }
            foreach (var page in new[] { "1", "2", "0" }) window.Shell.NavigateCommand.Execute(page);
            Assert.Equal(1, client.Sends); Assert.Equal(2, chat.Messages.Count); Assert.False(tracking.IsRunning); Assert.False(tracking.Servo.Ready);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task StoppedImageAndExpiredFaceResultsCannotBecomeChatContext()
    {
        using var tracking = new MainViewModel { SourceIndex = 1, TargetIndex = 1 };
        Assert.Null(tracking.CaptureExpressionCue());
        tracking.SourceIndex = 3; tracking.IsRunning = true;
        Assert.Null(tracking.CaptureExpressionCue());
        // Injected frame metadata only: no camera is opened or actuator attached.
        var estimate = new ExpressionEstimate(FacialExpression.Happy, .9, .8, "stable", TimeSpan.Zero, [.02,.01,.01,.9,.03,.01,.02]);
        tracking.Present(new(new byte[640*480*4],640,480,new(0,TimeSpan.Zero,640,480,320,240),
            new(103,100,TrackingState.Centered,0,0),TimeSpan.Zero,1,VisionStatus:"face_locked",Expression:new(estimate)));
        Assert.Equal("happy",tracking.CaptureExpressionCue()!.Label);
        await Task.Delay(650); Assert.Null(tracking.CaptureExpressionCue()); tracking.IsRunning = false;
    }
    [LiveTobooTheory]
    [InlineData(1440, 900)]
    [InlineData(980, 700)]
    public async Task RealOwnedTestConversationLoadsThroughCSharpAndRendersWithoutSending(int width, int height)
    {
        var session = Environment.GetEnvironmentVariable("TOBOO_HARNESS_TEST_SESSION")!;
        var store = new ChatStateStore(StateFile()); store.Save(new(SessionId: session));
        using var chat = new ChatViewModel(store: store);
        using var tracking = new MainViewModel();
        var window = new MainWindow(tracking, chat) { Width = width, Height = height };
        try
        {
            window.Show(); await chat.ConnectAsync();
            Assert.True(chat.IsConnected, chat.Error); Assert.False(chat.HasError, chat.Error);
            Assert.True(chat.Messages.Count >= 4); Assert.Contains(chat.Messages,m=>m.IsUser && m.Text.Contains("独立桥接联调"));
            Assert.False(chat.RemoteRunning); Assert.False(tracking.IsRunning); Assert.False(tracking.Servo.Ready);
            await Task.Delay(80);
            var directory = Path.Combine(Workspace(), "artifacts", "harness-chat-preview"); Directory.CreateDirectory(directory);
            using var screenshot = window.CaptureRenderedFrame(); Assert.NotNull(screenshot);
            screenshot.Save(Path.Combine(directory, $"chat-real-service-{width}.png"));
        }
        finally { window.Close(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(respond(request)); }
    }
    [Fact]
    public async Task TransportUsesOnlyFixedLoopbackAndRefusesRedirectsOrForgedConnectionMetadata()
    {
        var file = StateFile(); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { service = "toboo-chat", protocol = 1, port = 37848, token = new string('a', 64) }));
        var handler = new Handler(request => {
            Assert.Equal("http://127.0.0.1:37848/rpc", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("Origin"));
            return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.invalid/") } };
        });
        using var client = new TobooChatClient(file, handler);
        await Assert.ThrowsAsync<ChatConnectionException>(() => client.CallAsync<ChatStatus>(new { action = "status" })); Assert.Equal(1, handler.Calls);
        await File.WriteAllTextAsync(file, "{\"service\":\"other-service\",\"protocol\":1,\"port\":9999,\"token\":\"sk-never-print-this\"}");
        var failure = await Assert.ThrowsAsync<ChatConnectionException>(() => client.CallAsync<ChatStatus>(new { action = "status" }));
        Assert.DoesNotContain("sk-never", failure.Message); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public void CorruptPendingStateIsNotSilentlyDropped()
    {
        var file = StateFile(); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "{broken");
        Assert.Throws<ChatConnectionException>(() => new ChatStateStore(file).Load()); Assert.Equal("{broken", File.ReadAllText(file));
    }
    [Fact]
    public void TwoClientsCannotOverwriteEachOthersPendingState()
    {
        var path = StateFile(); using var first = new ChatStateStore(path); using var second = new ChatStateStore(path);
        first.AcquireWriter(); Assert.Throws<ChatConnectionException>(() => second.AcquireWriter());
        first.Dispose(); second.AcquireWriter();
    }
    [AvaloniaFact]
    public async Task ClosingTheProductStopsVisualControlBeforeWaitingForChatCancellation()
    {
        var client = new FakeClient(); using var chat = new ChatViewModel(client, new(StateFile()));
        using var tracking = new MainViewModel(); var window = new MainWindow(tracking, chat);
        window.Show(); await chat.ConnectAsync(); chat.Draft = "关闭流程测试"; await chat.SendAsync();
        client.Turns[0] = client.Turns[0] with { State = "running" }; await chat.RefreshAsync();
        var running = tracking.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            while (tracking.ProcessedFrames < 3) await Task.Delay(20, deadline.Token);
            bool? visualStoppedBeforeChat = null;
            client.BeforeStop = () => visualStoppedBeforeChat = !tracking.IsRunning;
            window.Close();
            while (window.IsVisible) await Task.Delay(20, deadline.Token);
            await running;
            Assert.True(visualStoppedBeforeChat);
            Assert.Contains(client.Calls, call => call.GetProperty("action").GetString() == "stop"
                && call.GetProperty("requestId").GetString() == client.Turns[0].RequestId);
        }
        finally { await tracking.StopAsync(); await running; window.Close(); }
    }
    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Toboo.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}

[Xunit.Sdk.XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaTheoryDiscoverer", "Avalonia.Headless.XUnit")]
public sealed class LiveTobooTheoryAttribute : TheoryAttribute
{
    public LiveTobooTheoryAttribute()
    {
        if (!Guid.TryParse(Environment.GetEnvironmentVariable("TOBOO_HARNESS_TEST_SESSION"), out _))
            Skip = "Set TOBOO_HARNESS_TEST_SESSION to a generated, owned integration-test session; never sends prompts.";
    }
}
