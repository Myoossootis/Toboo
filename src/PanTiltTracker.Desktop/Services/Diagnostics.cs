using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using PanTiltTracker.Vision;

namespace PanTiltTracker.Desktop.Services;

internal static class Diagnostics
{
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
        using var blank = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black);
        if (faceDetector.Detect(blank, 0, TimeSpan.Zero).Boxes.Length != 0)
            throw new InvalidOperationException("Native face detector blank-frame smoke test failed.");
        var onnxRuntime = Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString();
        using var process = Process.GetCurrentProcess();
        Write("self-test.json", new
        {
            success = true, version = typeof(Diagnostics).Assembly.GetName().Version?.ToString(),
            framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
            nativeLibraries = process.Modules.Cast<ProcessModule>()
                .Where(m => m.ModuleName is "OpenCvSharpExtern.dll" or "msvcp140.dll" or "vcruntime140.dll" or "onnxruntime.dll")
                .Select(m => new { name = m.ModuleName, path = m.FileName }).ToArray(),
            openCv = Cv2.GetVersionString(), syntheticMarkerDetected = true, faceModelLoaded = true, faceDetector = "yunet_2023mar",
            faceBlankFramePassed = true, physicalCameraTested = false,
            onnxRuntime, droneWeightsBundled = false,
            hardwareOutput = "disabled", cameraDevices = CameraDevices.List()
        });
    }
}
