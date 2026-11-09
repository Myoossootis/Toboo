using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using Toboo.Vision;

namespace Toboo.Desktop.Services;

internal static class Diagnostics
{
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
