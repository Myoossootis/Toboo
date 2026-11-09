# 源码导航

**运行桌面程序：** 在仓库根目录双击 `start-toboo.cmd`，或执行 `scripts/start-toboo.ps1`。

**查看入口代码：** `Toboo.Desktop/Program.cs` → `App.axaml.cs` → `Views/Windows/MainWindow.axaml`。

| 目录 | 内容 |
| --- | --- |
| Toboo.Desktop | 用户界面；Views / ViewModels / Services 分层，内部按陪伴、视觉和硬件归类 |
| Toboo.Cli | 命令行入口 Program.cs、视觉实验和传感器命令 |
| Toboo.Core | Tracking、Expressions、Sensors 的纯算法和数据模型 |
| Toboo.Vision | Detection、Expressions、Imaging、Tracking 的 OpenCV 实现 |
| Toboo.Runtime | 共用日志、传感器实验及串口主机工具封装 |

项目关系见 [架构](../docs/architecture.md)。构建输出统一放在忽略目录，`bin/obj` 不属于源码。
