using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using Toboo.Vision;
using Toboo.Runtime;

namespace Toboo.Desktop.Services;

internal static class Diagnostics
{
    public static async Task MicSelfTestAsync(string port, bool recognize = false, bool saveAudio = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var mic = new I2sMicrophone();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = mic.RecordAsync(port, () => ready.TrySetResult(), _ => { }, timeout.Token);
        var stop = Task.Run(async () =>
        {
            await ready.Task.WaitAsync(timeout.Token);
            await Task.Delay(recognize ? 8000 : 3000, timeout.Token);
            mic.Finish();
        });
        try
        {
            // Exercise the same shared USB route used by the UI, using read-only servo queries.
            if (await Task.WhenAny(ready.Task, recording) == recording && !ready.Task.IsCompleted)
                await recording;
            await ready.Task.WaitAsync(timeout.Token);
            var servo = await ServoTool.RunAsync(["--port", port, "--action", "inspect"], timeout.Token,
                name => SharedEsp32.Get(name).Proxy);
            if (servo.ExitCode != 0) throw new IOException("采音期间读取云台状态失败：" + servo.Error);
            var pcm = await recording;
            var peak = I2sMicrophone.Peak(pcm);
            string? recognized = null;
            var observations = new List<object>();
            if (saveAudio)
            {
                Directory.CreateDirectory(DirectoryPath);
                using var file = File.Create(Path.Combine(DirectoryPath, "mic-debug.wav"));
                using var wav = new BinaryWriter(file);
                wav.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wav.Write(36 + pcm.Length);
                wav.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wav.Write(16);
                wav.Write((short)1); wav.Write((short)1); wav.Write(16000); wav.Write(32000);
                wav.Write((short)2); wav.Write((short)16);
                wav.Write(System.Text.Encoding.ASCII.GetBytes("data")); wav.Write(pcm.Length); wav.Write(pcm);
            }
            if (recognize)
            {
                I2sMicrophone.Normalize(pcm);
                recognized = await WhisperPcmRecognizer.RecognizeAsync(pcm, timeout.Token,
                    (text, probability, noSpeech, accepted) => observations.Add(new { text, probability, noSpeech, accepted }));
            }
            Write("mic-self-test.json", new { success = pcm.Length >= 32000 && peak > 0 && (!recognize || !string.IsNullOrWhiteSpace(recognized)),
                source = "actual-ics43432-usb", port, samples = pcm.Length / 2, sampleRate = 16000,
                peak, recognized, observations, recognitionEngine = recognize ? "whisper-base-local-zh" : null,
                servoStatusReadDuringCapture = true,
                rawAudioSaved = saveAudio, modelRequestsSent = false, movementRequested = false });
            if (pcm.Length < 32000 || I2sMicrophone.Peak(pcm) == 0)
                throw new IOException("没有采到有效音频，请核对接线、声道和模块。");
            if (recognize && string.IsNullOrWhiteSpace(recognized)) throw new IOException("采音成功，但没有识别出有效语音。");
        }
        finally
        {
            timeout.Cancel();
            try { await stop; } catch (OperationCanceledException) { }
            try { await recording; } catch (Exception) { }
            SharedEsp32.CloseAll();
        }
    }

    public static async Task RecognizeAudioAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var file = File.OpenRead(path);
        if (file.Length > 1024 * 1024) throw new InvalidDataException("诊断录音过长。");
        using var reader = new BinaryReader(file);
        if (reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() + 8 != file.Length || reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("需要 PCM WAV 录音。");
        byte[]? pcm = null;
        var format = false;
        while (file.Position + 8 <= file.Length)
        {
            var tag = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            var next = file.Position + length + (length & 1);
            if (next > file.Length) throw new InvalidDataException("WAV 数据不完整。");
            if (tag == 0x20746D66)
            {
                if (length < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1 || reader.ReadUInt32() != 16000
                    || reader.ReadUInt32() != 32000 || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16)
                    throw new InvalidDataException("需要 16kHz / 16 位 / 单声道 PCM WAV。");
                format = true;
            }
            else if (tag == 0x61746164) pcm = reader.ReadBytes(checked((int)length));
            file.Position = next;
        }
        if (!format || pcm is null) throw new InvalidDataException("WAV 缺少 PCM 数据。");
        var peak = I2sMicrophone.Peak(pcm);
        I2sMicrophone.Normalize(pcm);
        var observations = new List<object>();
        var clock = Stopwatch.StartNew();
        var text = await WhisperPcmRecognizer.RecognizeAsync(pcm, timeout.Token,
            (text, probability, noSpeech, accepted) => observations.Add(new { text, probability, noSpeech, accepted }));
        Write("audio-recognition.json", new { success = !string.IsNullOrWhiteSpace(text),
            source = "local-recorded-audio", engine = "whisper-base-local-zh", recognized = text,
            peak, elapsedSeconds = clock.Elapsed.TotalSeconds, observations,
            microphoneUsed = false, modelRequestsSent = false, movementRequested = false });
    }

    public static async Task SpeechSelfTestAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本机语音需要 Windows。");
        Write("speech-self-test.json", await WindowsSpeechService.SelfTestAsync());
    }

    public static async Task SerialRuntimeSelfTestAsync()
    {
        foreach (var script in new[] { "single_servo.py", "face_follow.py", "sensor_telemetry.py" })
        {
            var result = await ServoTool.RunScriptAsync(script, ["--help"]);
            if (result.ExitCode != 0 || !result.Output.Contains("usage:", StringComparison.Ordinal))
                throw new InvalidOperationException("串口运行环境自检失败：" + script + " " + result.Error);
        }
        var start = ServoTool.CreateStartInfo("single_servo.py", ["--help"]);
        var bundled = start.FileName.StartsWith(Path.Combine(AppContext.BaseDirectory, "runtime"), StringComparison.OrdinalIgnoreCase);
        Write("serial-runtime-self-test.json", new { success = true, bundledRuntime = bundled,
            helpers = new[] { "single_servo", "face_follow", "sensor_telemetry" }, serialOpened = false, hardwareOutput = "disabled" });
    }

    public static async Task ServoSelfTestAsync(string port)
    {
        Directory.CreateDirectory(DirectoryPath);
        var log = Path.Combine(DirectoryPath, "servo-readonly.jsonl");
        var result = await ServoTool.RunAsync(["--port", port, "--action", "inspect", "--log", log]);
        if (result.ExitCode != 0) throw new IOException("舵机只读检查失败：" + result.Error);
        using var report = JsonDocument.Parse(result.Output);
        // HELLO/STATUS only. A mistaken ARM, move, heartbeat or disarm fails the diagnostic.
        foreach (var line in File.ReadLines(log))
        {
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.TryGetProperty("tx", out var tx) &&
                tx.GetString() is not ("b'HELLO\\n'" or "b'STATUS\\n'" or "b'STATUS_S2\\n'" or "b'STATUS_S3\\n'"))
                throw new InvalidOperationException("只读检查发现了非状态查询命令。");
        }
        Write("servo-self-test.json", new { success = true, source = "actual-esp32-readonly-status", port,
            cameraUsed = false, movementRequested = false, report = report.RootElement.Clone() });
    }
    public static async Task LcdSelfTestAsync(string port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = await LcdConnection.OpenAsync(port, timeout.Token);
        foreach (var label in new[] { "unknown", "neutral", "unknown" })
            await connection.SendAsync(label, label == "unknown" ? 1 : 300, timeout.Token);
        Write("lcd-self-test.json", new { success = true, source = "actual-uno-usb-protocol", port,
            nativeSerial = true, pythonRequired = false, cameraUsed = false, actuatorOutput = "none",
            opticalDisplayVerified = false, acknowledged = new[] { "unknown", "neutral", "unknown" } });
    }
    public static async Task ChatSelfTestAsync()
    {
        using var client = new TobooChatClient();
        var status = await client.CallAsync<ChatStatus>(new { action = "status" });
        if (status.Service != "toboo-chat" || status.Protocol != 1 || status.Version != "1.0.2" || !status.Compatible || status.Tools || status.Isolation != "owned-transcripts-direct-llm")
            throw new InvalidDataException("toboo bridge compatibility/isolation failed.");
        var models = await client.CallAsync<ChatCatalog>(new { action = "models" });
        Write("chat-self-test.json", new { success = true, status.Version, status.Protocol, status.Isolation, status.Tools,
            providerGroups = models.Groups.Length, models = models.Groups.Sum(g => g.Models.Length),
            modelRequestsSent = false, cameraUsed = false, hardwareOutput = "disabled" });
    }
    public static string DirectoryPath => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.mode"))
        ? Path.Combine(AppContext.BaseDirectory, "diagnostics")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanTiltTracker", "diagnostics");

    public static void Write(string file, object result)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, file), JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void SelfTest()
    {
        using var scene = new SyntheticScene();
        using var image = scene.RenderAt(400, 200);
        using var detector = new ArucoTargetDetector();
        var observation = detector.Detect(image, 0, TimeSpan.Zero);
        if (!observation.HasTarget) throw new InvalidOperationException("Native OpenCV ArUco smoke test failed.");
        using var faceDetector = new FaceTargetDetector();
        using var expressionRecognizer = new FacialExpressionRecognizer();
        using var blank = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black);
        if (faceDetector.Detect(blank, 0, TimeSpan.Zero).Boxes.Length != 0)
            throw new InvalidOperationException("Native face detector blank-frame smoke test failed.");
        using var process = Process.GetCurrentProcess();
        Write("self-test.json", new
        {
            success = true, product = "toboo", version = typeof(Diagnostics).Assembly.GetName().Version?.ToString(),
            framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
            nativeLibraries = process.Modules.Cast<ProcessModule>()
                .Where(m => m.ModuleName is "OpenCvSharpExtern.dll" or "msvcp140.dll" or "vcruntime140.dll")
                .Select(m => new { name = m.ModuleName, path = m.FileName }).ToArray(),
            openCv = Cv2.GetVersionString(), syntheticMarkerDetected = true, faceModelLoaded = true, faceDetector = "yunet_2023mar",
            faceBlankFramePassed = true, physicalCameraTested = false,
            expressionModelLoaded = true, expressionModelSha256 = FacialExpressionRecognizer.ModelSha256,
            hardwareOutput = "disabled", cameraDevices = CameraDevices.List()
        });
    }
}
