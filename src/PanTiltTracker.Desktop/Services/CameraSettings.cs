using OpenCvSharp;

namespace PanTiltTracker.Desktop.Services;

public sealed record CameraSettings(int Width = 640, int Height = 480, int Fps = 30,
    VideoCaptureAPIs Backend = VideoCaptureAPIs.DSHOW)
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Fps <= 0 || Backend is not (VideoCaptureAPIs.DSHOW or VideoCaptureAPIs.MSMF))
            throw new ArgumentException("相机参数无效。");
    }
}
