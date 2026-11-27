using System.Diagnostics;

namespace Toboo.Desktop.Services;

internal static class TemporaryMicrophoneRecording
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private const string FolderName = "temporary-audio";

    internal static string Save(string diagnostics, byte[] pcm)
    {
        var folder = Path.Combine(diagnostics, FolderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"mic-{Guid.NewGuid():N}.wav");
        var wave = DeepSeekSpeechClient.ToWave(pcm);
        var created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            stream.Write(wave);
            return path;
        }
        catch
        {
            if (created) TryDelete(path);
            throw;
        }
        finally { Array.Clear(wave); }
    }

    internal static void RemoveExpired(string diagnostics, DateTime utcNow)
    {
        try
        {
            var cutoff = utcNow - Lifetime;
            RemoveIfExpired(new FileInfo(Path.Combine(diagnostics, "mic-debug.wav")), cutoff);
            var folder = new DirectoryInfo(Path.Combine(diagnostics, FolderName));
            if (!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in folder.EnumerateFiles("mic-*.wav", SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(file.Name);
                if (stem.Length != 36 || !Guid.TryParseExact(stem.AsSpan(4), "N", out _)) continue;
                RemoveIfExpired(file, cutoff);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Debug.WriteLine(error); }
    }

    private static void RemoveIfExpired(FileInfo file, DateTime cutoff)
    {
        if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0 && file.LastWriteTimeUtc <= cutoff)
            TryDelete(file.FullName);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Debug.WriteLine(error); }
    }
}
