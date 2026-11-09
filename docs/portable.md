# toboo Windows x64

完整解压后，双击本目录的 **toboo.exe**。运行时、DLL 和 models 文件夹均需保留；不需要另装 .NET SDK。建议解压到可写目录。

聊天、设备和设置界面都在 toboo 内。AI 对话需要已配置模型服务的 DeepSeek Harness，安装步骤见 [聊天接入](https://github.com/Myoossootis/Toboo/blob/main/docs/harness.md)（离线文档：`docs/harness.md`）。本包包含 `scripts/install-toboo-harness.ps1` 和独立桥插件；首次安装后从托盘完全退出并重启 Harness。

摄像头和表情分析在本地运行。语音输入、语音输出和 LCD 发送仍待接入。实际舵机调试需要源码环境中的 Python 串口工具，本包不自带该运行时。

诊断信息写入本目录的 `diagnostics/`。原有设置保留在 `%LOCALAPPDATA%\PanTiltTracker\`；聊天私有数据在 `%USERPROFILE%\.toboo-harness\`。不要公开个人数据与连接文件。

版本及构建提交见 `build-info.json`，第三方声明见 `THIRD-PARTY-NOTICES.md` 和 `licenses/`。

[项目主页](https://github.com/Myoossootis/Toboo) · [问题反馈](https://github.com/Myoossootis/Toboo/issues)
