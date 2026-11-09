# toboo Windows x64

完整解压后，双击本目录的 **toboo.exe**。运行时、DLL 和 models 文件夹均需保留；不需要另装 .NET SDK。建议解压到可写目录。

聊天、设备和设置界面都在 toboo 内。AI 对话需要已配置模型服务的 DeepSeek Harness，安装步骤见 [聊天接入](https://github.com/Myoossootis/Toboo/blob/main/docs/harness.md)（离线文档：`docs/harness.md`）。本包包含 `scripts/install-toboo-harness.ps1` 和独立桥插件；首次安装后从托盘完全退出并重启 Harness。

摄像头和表情分析在本地运行。UNO + I²C LCD1602 表情发送已接入，串口支持随程序提供，无需另装 Python；使用前需将专用固件写入 Uno，步骤见 `docs/lcd.md`。舵机与传感器工具也使用包内隔离的串口运行环境，请保留 `runtime` 和 `firmware` 文件夹。语音输入和语音输出仍待接入。

诊断信息写入本目录的 `diagnostics/`。原有设置保留在 `%LOCALAPPDATA%\PanTiltTracker\`；聊天私有数据在 `%USERPROFILE%\.toboo-harness\`。不要公开个人数据与连接文件。

版本及构建提交见 `build-info.json`，第三方声明见 `THIRD-PARTY-NOTICES.md` 和 `licenses/`。

[项目主页](https://github.com/Myoossootis/Toboo) · [问题反馈](https://github.com/Myoossootis/Toboo/issues)
