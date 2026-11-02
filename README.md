# UAVTrack / PanTiltTracker：CMOS 视觉双轴云台跟踪

EESM 5840 期末项目。室内带标记模型 → USB 摄像头 → C# / OpenCvSharp → 云台位置控制。正式目标是完成相机标定、受控目标跟踪及误差实验。

当前版本为 **v0.1.1-preview.1：Windows 便携版摄像头测试**。支持生成 ArUco 标记、合成图像闭环、图片检测、视频回放与实时摄像头采集。所有模式均只记录角度指令，尚未连接串口或驱动舵机；新摄像头的实际采集验收需在连接设备的笔记本上完成。

**直接使用程序：** 从 [GitHub Releases](https://github.com/Myoossootis/UAVTrack/releases) 下载 `win-x64-portable.zip`，完整解压后双击 `PanTiltTracker.Desktop.exe`。无需安装 .NET 或 Visual Studio。摄像头选择、远程操作和故障定位见 [便携版说明](docs/09-portable-camera.md)。

桌面实验台已加入，启动命令：

```powershell
dotnet run --project src/PanTiltTracker.Desktop
```

默认选择合成场景，点击“开始实验”。支持参数设置、模拟遮挡、实时画面、误差曲线、停止和 CSV 导出；图片／视频可从文件选择，摄像头入口待实物验证。操作说明见 [桌面界面](docs/06-desktop-interface.md)。

开发时使用的真实无人机素材为 `data/processed/drone-11204831-1080p30.mp4`，1920×1080 / 30fps，约 6 秒。录像未上传仓库，也不包含在便携包中；可选择自己的视频。界面支持人工首框 + CSRT 以及帧差对照，操作与初步结果见 [无人机跟踪基线](docs/08-drone-tracking-baseline.md)，来源和规格见 [真实录像说明](docs/07-real-drone-video-and-algorithms.md)。

## 从这里开始

需要 Windows x64、.NET 8 SDK。无需 Visual Studio、MSVC 或原眼动项目的 C++/CLI 组件。可用任意文本编辑器和 PowerShell；本机已有 SDK，直接运行即可。

```powershell
Set-Location E:\Microsensor
dotnet restore PanTiltTracker.sln --locked-mode
dotnet build PanTiltTracker.sln --no-restore
dotnet test PanTiltTracker.sln --no-restore
dotnet run --project src/PanTiltTracker.App -- simulate
```

模拟结果在 `artifacts/simulate/`：`telemetry.csv`、`run.json` 和首末帧图片。模拟使用实际 OpenCV 标记检测，但虚拟相机直接跟随命令角度，不包含真实舵机、镜头和 USB 延迟。模拟指标不可当作实物性能。

一键检查与生成示例：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
```

此命令的执行策略只作用于该 PowerShell 进程，不修改系统设置。也可以在 PowerShell 7 中直接运行 `./scripts/verify.ps1`。

## 已有命令

```powershell
# 带白边的可打印标记：固定字典 DICT_4X4_50，默认 ID=7
dotnet run --project src/PanTiltTracker.App -- marker --out artifacts/marker-7.png

# 单张图片：输出中心位置、CSV 与标注图片
dotnet run --project src/PanTiltTracker.App -- image --input artifacts/marker-7.png

# 文件回放，使用视频标称帧率；不是实际闭环实验
dotnet run --project src/PanTiltTracker.App -- video --input data/raw/example.mp4 --frames 300

# 到货后用；只检测、显示和记录，不驱动舵机
dotnet run --project src/PanTiltTracker.App -- camera --index 0 --frames 300 --preview

# 全部参数
dotnet run --project src/PanTiltTracker.App -- help
```

运行目录中同名输出会覆盖；需要保留对照实验时用 `--out artifacts/实验名称` 指定不同目录。摄像头参数请求为 640×480、30fps，设备是否接受及实际延迟待到货后验证。Ctrl+C 停止，预览窗口按 Esc 停止。

## 分级目录

```text
Microsensor/
├─ README.md / AGENTS.md             项目入口与维护约定
├─ PanTiltTracker.sln                SDK 命令行解决方案
├─ global.json                      固定 .NET 8 SDK 系列
├─ config/tracker.json              控制参数；当前为模拟初值
├─ src/
│  ├─ PanTiltTracker.Core/           观测模型、比例控制、限位与超时状态
│  ├─ PanTiltTracker.Vision/         ArUco、合成场景、最新帧交接
│  └─ PanTiltTracker.App/            命令行、采集入口、预览与日志
│  ├─ PanTiltTracker.Desktop/        Avalonia 桌面实验台
│  └─ Shared/                       命令行与桌面共享的日志写入
├─ tests/PanTiltTracker.Tests/       控制逻辑与实际 OpenCV 合成图像测试
├─ scripts/verify.ps1                还原、编译、测试和离线冒烟检查
├─ docs/                            分阶段计划、架构、开发和实验规范
├─ hardware/                        物料与接线记录
├─ firmware/                        STM32 工作边界与待实现通信协议
├─ calibration/local/               本机标定文件，不入 Git
├─ data/raw/ / data/processed/       原始和处理后实验数据，不入 Git
├─ artifacts/                       程序生成输出，不入 Git
└─ output/                          前期提案与算法分析，保留原文件
```

## 阅读顺序

1. [项目阶段与验收](docs/00-project-plan.md)：现在能做什么，到货后先做什么。
2. [架构与算法边界](docs/01-architecture.md)：数据流、坐标、计时和状态。
3. [开发方式](docs/02-development.md)：不用 Visual Studio 的构建与调试。
4. [到货检查](docs/03-hardware-checklist.md)：供电、接口、机械方向的记录。
5. [实验与数据](docs/04-experiments.md)：指标分母、模拟与实测的区别。
6. [Git 管理](docs/05-git-workflow.md)：分支、提交、数据与远端。

前期资料：[预 proposal](output/vision_tracking_preproposal.md)、[算法与旧工程复用分析](output/pc_vision_algorithm_review.md)。它们保留讨论时的版本；当前实现路线以本 README 和 `docs/` 为准，PC 端采用 C#。

## 无人机录像实验

已加入人工首帧框选 + CSRT，以及相邻帧差对照。桌面程序选择“视频回放”→“载入无人机示例”→拖框→“开始实验”。不需要 ArUco 标记，也不自动识别无人机类别。

命令行对照可运行 `scripts/run-drone-baseline.ps1`；使用方法、输出字段和首次真实录像结果见 [无人机跟踪基线](docs/08-drone-tracking-baseline.md)。报告有效比例不等于定位准确率；目前没有参考框真值。

## 当前边界

- 原眼动工程只作为参考，未修改、复制其设备专用采集代码。
- 标定校正、串口输出和 STM32 固件属于后续阶段。
- 初始方向、90°中位、限位和增益均不是实物校准结果。
- 正式仓库为 [Myoossootis/UAVTrack](https://github.com/Myoossootis/UAVTrack)，主分支 `main`；便携二进制通过 Release 分发。

参考库及许可见 [参考资料](docs/references.md)。
