using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Toboo.Desktop.Services;

internal sealed record SpeechRecognitionOptions(int Engine = 0, bool CorrectText = true, bool ReviewText = true,
    string? ProtectedKey = null, int Source = 0, string? Port = null, bool CorrectWakeText = false);

internal sealed class SpeechRecognitionStore
{
    private readonly string path;
    private readonly Func<byte[], byte[]> protect, unprotect;
    public SpeechRecognitionOptions Current { get; private set; } = new();
    public string? LoadError { get; private set; }
    public SpeechRecognitionStore(string? path = null, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanTiltTracker", "voice.local.json");
        this.protect = protect ?? Protect;
        this.unprotect = unprotect ?? Unprotect;
        try
        {
            if (!File.Exists(this.path)) return;
            if (new FileInfo(this.path).Length > 16384) throw new InvalidDataException("语音设置文件过大。");
            Current = JsonSerializer.Deserialize<SpeechRecognitionOptions>(File.ReadAllText(this.path)) ?? new();
            if (Current.Engine is < 0 or > 1 || Current.Source is < 0 or > 1) throw new InvalidDataException("语音识别引擎设置无效。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Current = new(); LoadError = "语音设置无法读取，已暂用本地识别；重新保存时会备份旧文件。"; }
    }
    public string? ReadKey()
    {
        if (string.IsNullOrEmpty(Current.ProtectedKey)) return null;
        try { return Encoding.UTF8.GetString(unprotect(Convert.FromBase64String(Current.ProtectedKey))); }
        catch (Exception error) when (error is CryptographicException or FormatException or PlatformNotSupportedException)
        { throw new InvalidOperationException("无法解密本机语音密钥，请在当前 Windows 账户重新配置。", error); }
    }
    public void Save(int engine, bool correctText, bool reviewText, string? newKey = null, bool clearKey = false,
        int? source = null, string? port = null, bool? correctWakeText = null)
    {
        if (engine is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(engine));
        if (source is < 0 or > 1 || port is { Length: > 64 } || port?.Any(char.IsControl) == true) throw new ArgumentException("麦克风设置无效。");
        if (newKey is not null && (newKey.Length > 1024 || string.IsNullOrWhiteSpace(newKey) || newKey.Any(char.IsControl)))
            throw new ArgumentException("API Key 格式无效。");
        var key = clearKey ? null : newKey is null ? Current.ProtectedKey : Convert.ToBase64String(protect(Encoding.UTF8.GetBytes(newKey.Trim())));
        var next = new SpeechRecognitionOptions(engine, correctText, reviewText, key, source ?? Current.Source,
            source.HasValue ? port : Current.Port, correctWakeText ?? Current.CorrectWakeText);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (LoadError is not null && File.Exists(path)) File.Copy(path, path + ".before-repair-" + Guid.NewGuid().ToString("N") + ".bak");
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, path, true);
            Current = next;
            LoadError = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static byte[] Protect(byte[] value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥保存需要 Windows。");
        return ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser);
    }
    private static byte[] Unprotect(byte[] value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥读取需要 Windows。");
        return ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser);
    }
}
