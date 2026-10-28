# ADR 0001：先完成 C# 命令行链路

状态：采用，2026-10-29。

已有 EyeTracking 项目使用 .NET 8、OpenCvSharp4 和 Avalonia，但实时采集是设备专用 libusbK / C++/CLI 路径。新 USB 摄像头不沿用该入口。用户已删除 Visual Studio，希望控制开发环境体积。

决策：新建纯 C# SDK 工程，复用技术路线而不引入旧工程引用。先提供命令行、OpenCV 检测、合成验证和 CSV，采用预编译 Windows NuGet 运行库。界面和 FFmpeg 实时入口在确有需要时再添加。

结果：无需安装 Visual Studio 和原 C++ 工具链，可以在到货前验证算法。当前不提供完整桌面 UI，也未解决硬件通信和机械性能；这些均有单独阶段和验收。
