# 参考与依赖

## 官方资料

- [亚博 USB 摄像头](https://www.yahboom.com/study_module/USB-camera)
- [亚博 PC 预览与帧率测试](https://www.yahboom.net/study/USB-camera/10916/)
- [亚博云台及示例](https://github.com/YahboomTechnology/Camera-PTZ)
- [OpenCV ArUco 检测](https://docs.opencv.org/4.x/d5/dae/tutorial_aruco_detection.html)
- [OpenCV VideoCapture](https://docs.opencv.org/4.x/d8/dfe/classcv_1_1VideoCapture.html)
- [OpenCV 相机标定](https://docs.opencv.org/4.x/dc/dbb/tutorial_py_calibration.html)
- [FFmpeg DirectShow](https://ffmpeg.org/ffmpeg-devices.html#dshow)
- [.NET SDK 选择与 global.json](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)

## 依赖选择

[OpenCvSharp](https://github.com/shimat/opencvsharp) 为持续维护的 .NET OpenCV 包装，仓库采用 Apache-2.0。当前固定旧项目使用的 4.10.0.20240616，避免引入大版本迁移；运行库还包含其自身的第三方组件，分发时应保留实际包内许可说明。本项目通过包引用使用库，没有复制该库源码。

测试使用 xUnit、Microsoft.NET.Test.Sdk 与 Visual Studio 测试适配器 NuGet 包。适配器名称中的 Visual Studio 不代表需要安装 Visual Studio；测试通过 dotnet test 运行。

亚博公开仓库本次元数据未提供明确仓库许可证；仅参考接口资料，没有把厂商示例源码复制入项目。

用户 EyeTracking 本地项目只读参考，未引入源码、二进制或个人实验数据。课程日期依据用户提供的 EESM5840_Final_Project_Guidelines.pdf。
