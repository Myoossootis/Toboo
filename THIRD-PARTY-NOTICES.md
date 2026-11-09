# Third-party components in the Windows portable distribution

The source project's publication does not grant an additional license to third-party components. Original license and notice texts are retained in `licenses/`, including texts extracted from the exact locked NuGet packages. Dependency versions and hashes are recorded in `licenses/packages.lock.json`.

| Component | License / source |
| --- | --- |
| .NET runtime 8.0.33 | MIT and third-party notices; `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` in the runtime distribution; https://github.com/dotnet/runtime |
| Avalonia 11.3.22 | MIT; https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm 8.4.0 | MIT; https://github.com/CommunityToolkit/dotnet |
| OpenCvSharp 4.10.0.20240616 | Apache-2.0; https://github.com/shimat/opencvsharp |
| OpenCV 4.10.0 | Apache-2.0 plus bundled third-party components; https://github.com/opencv/opencv/tree/4.10.0 |
| YuNet 2023mar face-detection model | MIT, `licenses/YuNet-MIT.txt`; https://github.com/opencv/opencv_zoo/tree/f12e12798e8314f7c074a6656816c048dcc95b7a/models/face_detection_yunet |
| Progressive Teacher / MobileFaceNet expression ONNX model and reference alignment | Apache-2.0, `licenses/OpenCV-Zoo-Apache-2.0.txt`; Copyright (C) 2022, Shenzhen Institute of Artificial Intelligence and Robotics for Society; https://github.com/opencv/opencv_zoo/tree/25f423d0e04c31a17254620e58febd7386da523b/models/facial_expression_recognition |
| OpenCV frontal-face Haar cascade | Original Intel permissive terms retained in `models/face/haarcascade_frontalface_default.xml` and `licenses/OpenCV-Haar-model.txt`; https://github.com/opencv/opencv/tree/4.10.0/data/haarcascades |
| OpenCV FFmpeg video plugin | FFmpeg LGPL; https://github.com/opencv/opencv_3rdparty/tree/ffmpeg/4.x and https://ffmpeg.org/legal.html |
| SkiaSharp 2.88.9 / Skia and native dependencies | MIT / BSD and original third-party notices included in package; https://github.com/mono/SkiaSharp |
| HarfBuzzSharp 8.3.1.1 / HarfBuzz | MIT and original third-party notices included in package; https://github.com/harfbuzz/harfbuzz |
| MicroCom.Runtime 0.11.0 | MIT; https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol 0.21.3 | MIT; https://github.com/tmds/Tmds.DBus |
| ANGLE | BSD and third-party notices included in native NuGet package; https://github.com/google/angle |
| Microsoft Visual C++ runtime 14.44.35211.0 | Microsoft redistributable terms; English and Chinese license RTFs extracted from the official signed x64 redistributable |

Other Microsoft support libraries are distributed under their NuGet package terms. Libraries remain separate files; no static linking or modification of these libraries was performed by this project. No camera vendor SDK, proprietary eye-tracking binary or personal image dataset is included.

The portable application uses the OpenCV FFmpeg DLL supplied by the pinned `OpenCvSharp4.runtime.win` package. It can be replaced with a compatible plugin in the application directory. Upstream OpenCV build scripts and third-party source references are available at https://github.com/opencv/opencv/blob/4.10.0/3rdparty/ffmpeg/ffmpeg.cmake .
