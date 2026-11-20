# toboo Windows x64 便携版使用方法

## 另一台笔记本首次启动

1. 在 Release 附件中下载完整的 `toboo-0.3.2-win-x64-portable.zip`。
2. 使用 Windows 10/11 x64，解压到用户可写目录，例如桌面上的 `Toboo` 文件夹。
3. 双击解压目录内的 **toboo.exe**。保留整个文件夹，包含 DLL、`models`、`runtime` 和 `firmware`。
4. 左侧「视觉标定」可打开照片或选择 USB 摄像头，查看相对深度和彩色点云。拖拽旋转，滚轮缩放。

便携包包含 .NET 运行时、图像依赖、深度与语音模型、独立串口 Python 环境。首次使用本地视觉功能可以离线运行。中文系统麦克风识别和朗读仍依赖 Windows 中文语音组件。

## 摄像头与空间感知

将 USB 摄像头连接电脑，在「视觉标定」点击刷新，选择正确设备并开启。若无图像，检查 Windows 桌面应用相机权限及其他相机应用的占用情况。普通预览和点云可以直接开始，二维地图需要打印并采集棋盘格，之后固定 ArUco 7 标记。页面提供保存打印图功能。

点云显示单帧估计的相对形状，空间比例需验证。超声波实测距离单独显示。标定步骤见 `docs/aruco-sonar-map.md`。

## Uno 表情屏与超声波

已有合并固件时连接 Uno USB，在「设备 → 表情屏幕」刷新并选择其 COM 口。新电脑的端口编号可能改变，请在设备管理器中确认。Uno 与 ESP32 使用不同端口。

LCD 的 VCC/GND/SDA/SCL 对应 Uno 5V/GND/A4/A5，超声波 VCC/GND/TRIG/ECHO 对应 5V/GND/D7/D8。先断开 USB 后接线。首次需要烧录 Release 的 `uno-lcd-distance.hex`，具体方法见 `docs/lcd.md` 和 `docs/aruco-sonar-map.md`。

## 云台与本地语音命令

1. 在「设备 → 云台」选择 ESP32 的串口，点击连接并读状态。
2. 首次装机完成工作边界、相机参数、两轴方向检查，保存配置。
3. 确认外部 5V 已接、机构可动，再点击回到中心并检查姿态。
4. 选中 USB 摄像头后，点击开启实物跟随。
5. 也可以回到陪伴页按住语音按钮，说「打开摄像机跟随」后松开。设备就绪时启动，缺项时显示提示。此命令可在 Harness 离线时使用。

已有工作边界和方向确认时，无需重走三步准备。若提示缺少相机角度模型，在云台卡片点击「使用视场角估算并保存」，即可建立并保存水平 60°、垂直 45° 的近似模型。已打开摄像头预览时取实际画面尺寸，否则采用当前采集规格（默认 640×480）。该模型未经实测标定，需要更准确的角度修正时，在高级设置中填入相机视场角或实测内参。此操作保留舵机边界、中心和方向确认，且不会发送舵机运动指令。

相同且安装未改动的机器人，可在两台电脑都关闭 toboo 后，将旧电脑的 `%LOCALAPPDATA%\PanTiltTracker\servo.json` 和 `face-aim.local.json` 复制到新电脑同目录。只复制这两个配置文件即可恢复保存的中心、工作区和相机角度设置。核对新电脑摄像头与原设备一致，重新选择 COM 口并确认供电。原标定失效或更换相机时，重新标定。聊天凭据和个人实验记录保存在其他目录。

跟随要求角度定位型舵机，360°连续旋转型号需要其他控制方式。固件附件提供定位版与全输出锁定版，使用方法见 `docs/hardware.md`。软件启动为待机，进入视觉标定页会结束主视觉会话。

## 语音输入和朗读

电脑麦克风使用 Windows 中文识别，ICS43432 模块通过 ESP32-S3 采音并使用包内 Whisper 转写。在设备页选择输入设备和模块串口。模块识别 CPU 需要 AVX2、FMA、F16C，缺少时界面会显示原因。系统麦克风缺少中文识别、朗读组件时，在 Windows 语言设置中为简体中文添加对应组件，然后重新检查本机语音。

按住说话、松开发送，Esc 取消。蓝牙音响可设为 Windows 默认声音输出。接线及模型说明见 `docs/microphone.md`。

## AI 对话

1. 在新电脑安装 DeepSeek Harness，启动一次并配置模型服务。
2. 在便携包根目录打开 PowerShell，执行下面的命令。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/install-toboo-harness.ps1
```

3. 从系统托盘完全退出 Harness，再重新启动。
4. 在 toboo 设置页刷新连接、选择模型，回到陪伴页聊天。

桥插件引用解压目录里的文件，因此保持文件夹路径稳定。更新前如存在旧桥路径，按 `docs/harness.md` 迁移。模型服务账户在 Harness 中配置。

## 诊断和升级

已有 v0.3.0 或 v0.3.1 完整便携版时，下载与当前版本对应的 `toboo-0.3.2-update-from-0.3.0-win-x64.zip` 或 `toboo-0.3.2-update-from-0.3.1-win-x64.zip`。关闭 toboo，将 ZIP 内全部内容解压到原 `toboo.exe` 所在目录，选择替换同名文件，然后双击原来的 `toboo.exe`。增量包不能单独运行。可运行包内的 `verify-update.ps1` 检查覆盖结果。此次升级无需重刷 Uno 或 ESP32 固件，本机保存的设备标定保持在原位置。

完整解压新版本到新文件夹，关闭旧程序，再启动新版。设备设置位于 `%LOCALAPPDATA%\PanTiltTracker\`，本地诊断写入包目录 `diagnostics/`。启动失败时先查看 `diagnostics/startup-error.json`。

可在 PowerShell 中运行以下离线检查，结果写入 `diagnostics/`。

```powershell
./toboo.exe --self-test
./toboo.exe --serial-runtime-self-test
```

版本和源码提交见 `build-info.json`，依赖声明见 `THIRD-PARTY-NOTICES.md` 和 `licenses/`。
