using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Toboo.Desktop.Services;

public sealed record ChatErrorInfo(string Code, string Message);
public sealed record ChatExpression(string Label, double Score, double AgeMs, double ObservedAtUnixMs);
public sealed record ChatSendRequest(string Action, string SessionId, string RequestId, string Text,
    string Provider, string Model, string Style, ChatExpression? Expression);
public sealed record ChatAcknowledgement(bool Accepted, bool Duplicate, string RequestId);
public sealed record ChatStatus(string Service, int Protocol, bool Compatible, bool Tools, string Isolation, string? Version = null);
public sealed record ChatCatalogModel(string Id, string Name);
public sealed record ChatCatalogGroup(string Id, string Name, ChatCatalogModel[] Models);
public sealed record ChatCatalog(ChatCatalogGroup[] Groups, int Failures);
public sealed record ChatModelChoice(string Provider, string Model, string Display)
{
    public override string ToString() => Display;
}
public sealed record ChatSessionChoice(string Id, string Title, long UpdatedAt)
{
    public override string ToString() => Title;
}
public sealed record ChatSessions(ChatSessionChoice[] Items);
public sealed record ChatTurn(string RequestId, string Text, string Reply, string Provider, string Model, string State,
    long CreatedAt, ChatErrorInfo? Error);
public sealed record ChatView(string? SessionId, long Revision, bool NotModified, bool HasMore, int Before, ChatTurn[]? Turns);
public sealed class ChatConnectionException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}
public interface ITobooChatClient : IDisposable
{
    Task<T> CallAsync<T>(object request, CancellationToken cancellation = default);
}

/// <summary>Fixed loopback-only RPC; credentials never enter UI state, diagnostics or request bodies.</summary>
public sealed class TobooChatClient : ITobooChatClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".toboo-harness");
    private readonly string connectionFile;
    private readonly HttpClient http;
    public TobooChatClient() : this(Path.Combine(DirectoryPath, "connection.json")) { }
    internal TobooChatClient(string connectionFile, HttpMessageHandler? handler = null)
    {
        this.connectionFile = connectionFile;
        http = new(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public async Task<T> CallAsync<T>(object request, CancellationToken cancellation = default)
    {
        string token;
        try
        {
            var info = new FileInfo(connectionFile);
            if (!info.Exists || info.Length > 4096) throw new IOException();
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(connectionFile, cancellation));
            var root = config.RootElement;
            if (root.GetProperty("service").GetString() != "toboo-chat" || root.GetProperty("protocol").GetInt32() != 1
                || root.GetProperty("port").GetInt32() != 37848) throw new InvalidDataException();
            token = root.GetProperty("token").GetString() ?? "";
            if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new ChatConnectionException("尚未连接 toboo 桥接，请安装插件并启动 DeepSeek Harness。", "CONNECTION"); }
        var body = JsonSerializer.Serialize(request, Json);
        if (Encoding.UTF8.GetByteCount(body) > 65536) throw new ChatConnectionException("发送内容超过支持大小。", "TOO_LARGE");
        using var message = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:37848/rpc");
        message.Headers.TryAddWithoutValidation("Authorization", token);
        message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new ChatConnectionException("桥接凭据已更新，请重新连接。", "CONNECTION");
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                throw new ChatConnectionException("桥接返回了不支持的重定向。", "PROTOCOL");
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(bytes, deadline.Token)) > 0)
            {
                if (buffer.Length + count > 1048576) throw new ChatConnectionException("桥接返回内容过大，请检查版本。", "PROTOCOL");
                buffer.Write(bytes, 0, count);
            }
            using var value = JsonDocument.Parse(buffer.ToArray());
            if (value.RootElement.TryGetProperty("error", out var errorValue))
            {
                var error = errorValue.Deserialize<ChatErrorInfo>(Json);
                throw new ChatConnectionException(error?.Message ?? "桥接拒绝了本次请求。", error?.Code);
            }
            if (!response.IsSuccessStatusCode || !value.RootElement.TryGetProperty("result", out var result))
                throw new ChatConnectionException("桥接回复格式不兼容，请检查版本。", "PROTOCOL");
            return result.Deserialize<T>(Json) ?? throw new ChatConnectionException("桥接返回了空结果。", "PROTOCOL");
        }
        catch (ChatConnectionException) { throw; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new ChatConnectionException("连接超时。发送状态可能尚未确认，请重新连接核对。", "CONNECTION"); }
        catch (HttpRequestException) { throw new ChatConnectionException("无法连接 toboo 桥接，请确认 Harness 已启动。", "CONNECTION"); }
        catch (JsonException) { throw new ChatConnectionException("桥接回复格式不兼容，请检查版本。", "PROTOCOL"); }
    }
    public void Dispose() => http.Dispose();
}

public sealed record ChatClientState(string? SessionId = null, string? Provider = null, string? Model = null,
    string Style = "natural", bool IncludeExpression = false, ChatSendRequest? Pending = null);
public sealed class ChatStateStore(string? path = null) : IDisposable
{
    private readonly string filename = path ?? Path.Combine(TobooChatClient.DirectoryPath, "client.json");
    private FileStream? writer;
    public void AcquireWriter()
    {
        if (writer is not null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filename))!);
            writer = new FileStream(filename + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ChatConnectionException("另一个 toboo 窗口正在使用聊天状态，或目录不可写。请关闭另一窗口后重新连接。", "LOCAL_STATE"); }
    }
    public ChatClientState Load()
    {
        if (!File.Exists(filename)) return new();
        try
        {
            if (new FileInfo(filename).Length > 65536) throw new InvalidDataException();
            var value = JsonSerializer.Deserialize<ChatClientState>(File.ReadAllText(filename), TobooChatClient.Json) ?? throw new InvalidDataException();
            if (value.SessionId is { } id && !Guid.TryParse(id, out _) || value.Style is not ("natural" or "gentle" or "concise")
                || value.Pending is { } pending && (!Guid.TryParse(pending.SessionId, out _) || !Guid.TryParse(pending.RequestId, out _)
                    || string.IsNullOrWhiteSpace(pending.Text) || pending.Text.Length > 4000 || pending.Action != "send" || pending.SessionId != value.SessionId
                    || string.IsNullOrEmpty(pending.Provider) || string.IsNullOrEmpty(pending.Model)))
                throw new InvalidDataException();
            return value;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { throw new ChatConnectionException("客户端会话记录损坏。为避免重复发送，请先检查 .toboo-harness/client.json。", "LOCAL_STATE"); }
    }
    public void Save(ChatClientState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filename))!);
            File.WriteAllText(filename + ".tmp", JsonSerializer.Serialize(state, TobooChatClient.Json));
            File.Move(filename + ".tmp", filename, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ChatConnectionException("无法保存发送状态，本次操作未继续。", "LOCAL_STATE"); }
    }
    public void Dispose() { writer?.Dispose(); writer = null; }
}
