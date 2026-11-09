# 开始使用 toboo

## Windows 便携版

1. 在 [Releases](https://github.com/Myoossootis/Toboo/releases/latest) 下载完整的 `win-x64-portable.zip`。
2. 将整个文件夹解压到可写目录，保留 DLL、`models`、配置文件和 `licenses`。
3. 双击文件夹中的 **`toboo.exe`**。不要直接在压缩包内运行，也不要只复制 exe。
4. 首先使用陪伴、设备和设置三个页面。视觉调试、舵机和传感器工具位于高级工作台。

便携版自带 .NET 运行时与原生图像依赖，只面向 Windows x64。本版本是原型版本，首次使用无需连接舵机或传感器。

## 从源码启动

安装 .NET 8 SDK，在仓库根目录双击 `start-toboo.cmd`。脚本自动构建桌面项目并打开 toboo；失败时保留控制台错误。也可以运行：

```powershell
./scripts/start-toboo.ps1
# 可选：启动表情、传感器或舵机调试页面
./scripts/start-toboo.ps1 -Expression
./scripts/start-toboo.ps1 -Sensors
./scripts/start-toboo.ps1 -ServoCalibration
```

正常启动不会自动打开摄像头、串口或启动运动。源码构建输出在 `artifacts/toboo/`，无需在 `src` 中查找 exe。

## 配置对话

聊天需要单独配置 DeepSeek Harness 和模型服务。未连接时，toboo 的其他本地功能仍可使用。安装步骤见 [Harness 接入](harness.md)。文本框、流式回复、停止、新建与历史会话均在 toboo 内操作。

## 摄像头和表情

在设备页面选择摄像头并启动预览。高级工作台还支持图片和录像。只查看图像和表情不需要连接或启用舵机。

表情为七类可见线索，光照、模糊、侧脸、遮挡和不确定分数都会导致 Unknown。Unknown 是保守拒判，不是第八种情绪。详见 [表情算法](expressions.md)。

## LCD 表情屏

使用已烧录专用固件的 UNO 和 5V 四针 I²C LCD1602。在设备页填写对应 UNO 端口并连接，开启摄像头后同步有效表情；离开画面或停止识别后恢复 Unknown。便携版内置串口支持，接线及固件步骤见 [LCD 使用说明](lcd.md)。

## 本地文件与故障排查

- 便携版诊断信息写入程序目录的 `diagnostics/`；源码启动生成输出在忽略的 `artifacts/`。
- 为兼容已有安装，摄像头与舵机设置继续保存在 `%LOCALAPPDATA%\PanTiltTracker\`；升级不更改已保存的标定和运动范围。
- 聊天桥的令牌、历史和运行状态在 `%USERPROFILE%\.toboo-harness\`，不在发布包中。不要分享整个目录或连接文件。
- 启动失败：先确认完整解压、系统为 x64、目录可写，再查看 `diagnostics/startup-error.json`。
- 摄像头无画面：检查系统相机权限、设备选择及是否被其他程序占用。
- 对话未连接：核对 Harness 是否正在运行并加载了桥插件；从托盘退出后重启可重新加载插件。

版本升级请完整解压到新文件夹，关闭旧版后运行新目录的 `toboo.exe`。本版不提供跨版本 DLL 覆盖更新。
