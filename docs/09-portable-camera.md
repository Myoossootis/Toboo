# UAVTrack Windows 便携版：摄像头测试

本文便携包操作说明对应 v0.1.3，包含 YuNet 正面/侧面检测，见 [当前人脸检测](10-face-detection.md)。v0.1.2 用户可使用对应覆盖包升级；旧版 ZIP 不会因源码更新而自动改变。

适用于 Windows 10/11 **x64**。ZIP 包含 .NET 与本机图像运行库，无需 Visual Studio、.NET SDK 或单独安装 .NET。必须完整解压，不能只复制 EXE 或直接在压缩包里运行。

## 远程测试怎么用

1. 在**连接 USB 摄像头的笔记本**上下载并解压 `UAVTrack-…-win-x64-portable.zip`，放在桌面或下载等可写目录。远程桌面只用来操作该笔记本，程序读取的是运行电脑上的摄像头。
2. 先关闭微软“相机”、会议软件等可能占用摄像头的程序。双击 `PanTiltTracker.Desktop.exe`。
3. 左侧“采集设置”中将图像来源改成“USB 摄像头”，点击设备旁的“刷新”，选中外置摄像头。笔记本自带摄像头也可能排在列表中。
4. 在“检测目标”中选择 **ArUco 标记** 或 **人脸（正面 / 侧面）**，点击“开始实验”。默认请求 **640×480 / 30 fps**、使用 **DirectShow**。即使没有目标，也应该出现实时预览；“未检测到人脸”不代表相机连接失败。
5. 停止后可展开“采集参数”，调整分辨率请求、后端或手动编号。设备可能回退，界面收到图像后显示的尺寸才是实际尺寸；驱动报告 fps 及“处理更新”都不等于镜头曝光帧率。
6. “控制参数”标签保留增益、速度、死区与方向设置；右下“曲线与运行记录”可展开查看分析，展开时收起重复的指标条以留出画面空间。参数在下次开始时生效。

如果没有画面：检查 Windows 设置 → 隐私和安全性 → 相机 → 允许桌面应用访问相机；确认微软相机已经退出。可以换 **MSMF** 后端，并手动尝试编号 0、1、2。设备列表按 DirectShow 枚举，MSMF 的编号顺序可能不同。摄像头被拔出或驱动长时间阻塞时，请停止并重启程序。

## 当前能测试什么

- USB 摄像头实时预览、实际画面尺寸、采集格式和运行日志。
- DICT_4X4_50 / ID 7 的 ArUco 检测与像素中心误差。可先在“合成场景”验证软件链路，再用标记测试真实图像。
- 正面/侧面人脸检测与绘框；多人时全部绘框，最大框作为中心目标。YuNet 模型已随包附带，无需联网。极端姿态、小脸、遮挡或暗光下仍可能漏检或误检，不识别身份。
- 本地图片、视频回放；无人机录像可用人工首框 + CSRT，或帧差作为对照。选择自己的视频，载入首帧后拖框。源码仓库与便携包**不包含之前下载的无人机视频**，“载入无人机示例”需要本机另有对应文件。

当前摄像头模式支持 ArUco 和正面/侧面人脸，不提供实时 YOLO 或实时 CSRT；相机内参标定尚未实现。软件不会控制云台，界面的角度是计算请求，不能当作实际轴角度。

## 日志与故障定位

每次运行结果保存在解压目录下 `artifacts/desktop/<时间>/`：

- `telemetry.csv`：逐帧检测与控制计算结果。
- `run.json`：输入类型与实验参数。
- `capture-info.json`：摄像头首次成功读帧后的请求参数、实际尺寸、后端及驱动报告信息。这里的时间是 PC 收帧时间，不是曝光时间。

默认不保存摄像头原始画面，也不上传图像或日志。分享日志前可检查其中的本地文件路径。

无法启动时，在解压目录打开 PowerShell，执行：

```powershell
.\PanTiltTracker.Desktop.exe --self-test
```

检查同目录 `diagnostics/self-test.json` 或 `startup-error.json`。自检验证本机 OpenCV、合成标记检测、人脸模型加载和空场景检测，不会打开真实摄像头。内部 `--smoke-test-ui` 参数会自动运行合成场景并退出，供发布验证使用。

维护者需要自动验证实际相机时，可执行 `PanTiltTracker.Desktop.exe --smoke-test-ui --camera-index 0`。它会打开指定相机，采集至少 12 帧后停止并退出，结果在 `diagnostics/ui-smoke-test.json`。编号不对时先在正常界面中确认；此操作会访问实际摄像头。

发布前已验证的软件测试与本机便携包自检，不等于这台笔记本上的 USB 摄像头实测。请先确认：能选到外置相机、预览连续更新、停止后可以再次连接。若失败，提供完整错误文字和对应运行目录中的 JSON 即可。

## 从源码打包

开发者在 Windows 上安装 .NET 8 SDK 后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-portable.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-portable.ps1 -ZipPath artifacts/release/UAVTrack-0.1.3-win-x64-portable.zip
```

打包脚本固定 win-x64、自包含 .NET 8.0.33；不开启裁剪和单文件合并。VC++ 14.44.35211.0 从微软签名安装包提取应用目录内 DLL，不运行安装器。下载有 SHA256 校验，地址内容变更会终止打包，需要维护者核查后更新固定值。发布物在忽略的 `artifacts/release/`，不提交二进制、个人录像、标定数据或测试日志。`build-info.json` 记录源码提交与依赖版本，旁边的 `.sha256` 用于核对 ZIP。测试脚本在单独目录解压，验证应用目录内运行库、自检和桌面合成场景，不污染发布 ZIP，也不打开真实摄像头。
