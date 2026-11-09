# 架构与程序入口

桌面应用启动链路：`start-toboo.cmd` → `scripts/start-toboo.ps1` → `src/Toboo.Desktop/Program.cs` → `App.axaml.cs` → `Views/Windows/MainWindow.axaml`。`CompanionShellViewModel` 管理陪伴、设备和设置页；`ChatViewModel` 管理 toboo 内的对话。

| 项目 | 职责 | 依赖方向 |
| --- | --- | --- |
| Toboo.Core | 跟踪、表情时间状态机、传感器融合等纯算法 | 不引用 UI、OpenCV 或串口 |
| Toboo.Vision | 图像缓冲、检测、对齐、网络推理、录像处理 | Core、OpenCV |
| Toboo.Runtime | 会话日志、合成传感器数据与回放、Python 串口工具调用 | Core、Vision |
| Toboo.Desktop | Avalonia 页面、视图模型、采集与显示协调 | Core、Vision、Runtime |
| Toboo.Cli | 命令行视觉实验与回放 | Core、Vision、Runtime |

`Toboo.Runtime` 替代旧的链接式 Shared 源码，统一编译共用代码。桌面内部按 `Companion`、`Vision`、`Hardware` 和基础服务归类，命名空间保持项目内统一。高级工作台的 `MainViewModel` 属于视觉工作台；产品主窗口使用 `CompanionShellViewModel`。

## 数据边界

- Vision 拥有 OpenCV 原生图像缓冲；UI 通过界面服务接收可显示的数据，不把帧所有权交给 Core。
- 控制和新鲜度判断使用单调时间。缺帧、旧帧和长间隔不能补造观测或累积大步长。
- 角度是请求值，不是实际轴位测量。UI、日志及固件协议均须保留这个区别。
- 表情识别与聊天不发舵机指令。实际运动依赖独立的显式使能和串口故障处理。
- 聊天通过 `127.0.0.1:37848` 的独立桥访问 Harness 模型 API，不声明工具，不使用其他应用的聊天历史。

## 兼容性

产品及源码项目名统一为 Toboo，桌面可执行文件保留 `toboo.exe`。旧的本地配置目录名 `PanTiltTracker`、环境变量 `UAVTRACK_FACE_TEST_IMAGE` 和固件线上标识保留，以免升级误丢设置或破坏已有协议。它们不是额外产品入口。
