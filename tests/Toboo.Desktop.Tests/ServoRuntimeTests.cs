using Toboo.Runtime;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class ServoRuntimeTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("toboo-serial-test-").FullName;

    private void Add(string path)
    {
        var target = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "");
    }

    [Fact]
    public void PortableUsesOnlyBundledRuntimeAndPreservesArgumentsWithoutAShell()
    {
        Add("app with spaces/portable.mode");
        Add("app with spaces/runtime/python/python.exe");
        Add("app with spaces/firmware/tools/single_servo.py");
        var app = Path.Combine(root, "app with spaces");
        var start = ServoTool.CreateStartInfo(app, "single_servo.py", ["--port", "COM6", "--log", "log with spaces.jsonl"]);
        Assert.Equal(Path.GetFullPath(Path.Combine(app, "runtime/python/python.exe")), start.FileName);
        Assert.Equal(app, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(["-I", "-B", "-X", "utf8", Path.GetFullPath(Path.Combine(app, "firmware/tools/single_servo.py")),
            "--port", "COM6", "--log", "log with spaces.jsonl"], start.ArgumentList);
        Assert.False(start.Environment.ContainsKey("PYTHONHOME"));
        Assert.False(start.Environment.ContainsKey("PYTHONPATH"));
    }

    [Fact]
    public void IncompletePortableCannotFallBackToAParentCheckout()
    {
        Add("Toboo.sln"); Add("tmp/esp32-tools/host/Scripts/python.exe");
        Add("firmware/tools/single_servo.py"); Add("app/portable.mode");
        var error = Assert.Throws<InvalidOperationException>(() => ServoTool.CreateStartInfo(Path.Combine(root, "app"), "single_servo.py", []));
        Assert.Contains("runtime", error.Message);
    }

    [Fact]
    public void MissingPortableScriptDoesNotUseParentScripts()
    {
        Add("portable.mode"); Add("runtime/python/python.exe");
        Assert.Throws<InvalidOperationException>(() => ServoTool.CreateStartInfo(root, "face_follow.py", []));
    }

    [Fact]
    public void SourceUsesItsOwnVenvAndAnExplicitSiblingImportPath()
    {
        Add("Toboo.sln"); Add("tmp/esp32-tools/host/Scripts/python.exe"); Add("firmware/tools/face_follow.py");
        Directory.CreateDirectory(Path.Combine(root, "build"));
        var start = ServoTool.CreateStartInfo(Path.Combine(root, "build"), "face_follow.py", ["--help"]);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "tmp/esp32-tools/host/Scripts/python.exe")), start.FileName);
        Assert.Contains("-c", start.ArgumentList);
        Assert.Contains(Path.GetFullPath(Path.Combine(root, "firmware/tools")), start.ArgumentList);
        Assert.Equal("--help", start.ArgumentList[^1]);
    }

    [Theory]
    [InlineData("../single_servo.py")]
    [InlineData("other.py")]
    public void OnlyKnownSerialHelpersAreExecutable(string script) =>
        Assert.Throws<ArgumentException>(() => ServoTool.CreateStartInfo(root, script, []));

    public void Dispose() => Directory.Delete(root, true);
}
