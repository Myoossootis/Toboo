# 第一版无人机视频跟踪

日期：2026-10-29。使用 C# / .NET 8 和原有 OpenCvSharp 4.10，无新增依赖，没有训练或接入 YOLO。本阶段实现录像中的单目标定位实验，未实现相机内参标定、三维测距或舵机控制。

## 在界面中使用

启动 `src/PanTiltTracker.Desktop/bin/Release/net8.0/PanTiltTracker.Desktop.exe`，或运行：

```powershell
dotnet run --project src/PanTiltTracker.Desktop
```

1. 图像来源选“视频回放”，点击“载入无人机示例”。它会选择本机已准备的 1080p30 录像，并切换到 CSRT；其他电脑需自行准备该文件。
2. 在首帧上拖框，覆盖机身与机臂，少含背景。示例首框可参考原图像素 `(770,435,590,150)`，顺序为左上角 x、y、宽、高。
3. 点击“开始实验”。程序从第 0 帧重新读取录像，用人工框初始化，随后绘制跟踪框、中心与相对画面的偏差。
4. 停止后可导出 CSV。重新开始 CSRT 前点击“载入首帧 / 重新框选”。切换文件或算法会清空旧框，避免沿用其他输入的坐标。
5. 比较帧差时，把视频算法切换为“帧差 · 运动候选”，直接开始。它不使用人工首框。

框选会扣除预览留白，支持任意拖动方向并限制在图像范围内；坐标没有隐式镜像。新增鼠标交互通过 Avalonia 无界面输入事件验证，尚未记录原生桌面的鼠标实测。

## 两种算法的具体行为

**CSRT**：人工首框 → 外观初始化 → 每帧更新相关滤波器 → 返回框及中心。只在录像模式启用。最长工作宽度为 960 像素，高度按比例取整；输出框分别按宽、高比例映射回原始像素。首框至少 8×8 原图像素，降采样后至少 4×4。跟踪器一旦返回失败，后续帧都报告缺失，直到人工重新初始化；不会继续发送旧中心。它仍可能在漂移时报告成功，不能用返回布尔值代替准确率或置信度。

**帧差对照**：相同工作尺寸 → 灰度 → 5×5 高斯滤波 → 相邻帧绝对差 → 阈值 25 → 5×5 闭运算、3×3 开运算 → 外轮廓 → 取面积最大的合格区域。轮廓面积门限为工作图面积的 0.05%–35%。第一帧只初始化前一帧；静止区域不输出位置。此版没有跨帧候选关联、背景补偿或类别判断，可能框到局部机臂、旧位置残影、树木或阴影。它用于展示传统图像处理的能力和局限，不承担自动发现无人机的结论。

录像逐帧处理，不丢帧、不补帧；界面按标称帧率尝试回放，处理不及时则落后于录像时长。控制器使用单调递增的帧序号时间轴，命令不会改变录像中的相机。角度依然只是离线控制请求，不是实测轴角或目标方位角。

## 复现实验

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-drone-baseline.ps1

# 或分别运行；--roi 使用输入录像的原始像素
dotnet run --project src/PanTiltTracker.App -- track-video --input data/processed/drone-11204831-1080p30.mp4 --algorithm csrt --roi 770,435,590,150 --out artifacts/drone-experiments/csrt
dotnet run --project src/PanTiltTracker.App -- track-video --input data/processed/drone-11204831-1080p30.mp4 --algorithm frame-diff --out artifacts/drone-experiments/frame-difference
```

输出 `annotated.mp4`、抽样 PNG、`telemetry.csv`、`run.json` 与 `summary.json`；帧差还输出抽样掩膜 PNG。CLI 输出视频为 mp4v 二次编码，仅用于检查。默认目录同名结果会覆盖，需保留不同实验时更改输出目录。原始和生成视频均在 Git 忽略范围内。

CSV 保留原字段并追加 `box_x,box_y,box_width,box_height,vision_status,candidate_count`。缺失坐标与框留空；CSRT 第 0 帧状态为 `manual_initialization`，更新成功为 `tracker_reported_target`，失败为 `lost_reinitialize`。帧差的有效状态为 `motion_candidate_not_classified`。元数据记录算法、首框、输入路径与工作宽度；非 ArUco 实验的字典和 ID 为 null。

## 首次录像实验结果

输入为 [Pexels 11204831](https://www.pexels.com/video/drone-flying-on-grass-field-11204831/) 的 1920×1080 / 30fps 副本，共 179 帧，约 5.97 秒。来源和哈希见 `docs/samples/pexels-11204831.json`。两种方法处理相同的全部帧，CSRT 首框为上文示例。

| 项目 | CSRT | 帧差 |
|---|---:|---:|
| 处理帧数 | 179 | 179 |
| 算法报告有框的帧数 | 179（含人工首框） | 65 |
| 首次运行平均算法耗时 | 22.94 ms | 1.61 ms |
| 首次运行 P95 算法耗时 | 42.46 ms | 2.11 ms |
| 定量定位准确率 | 未测 | 未测 |

耗时是本机单次离线 `Process` 调用，包含缩放与算法；不包含解码、显示、标注、写盘或 USB 延迟。不能据此保证完整程序稳定 30fps。首次 CSRT 输出在 `artifacts/drone-experiments/csrt-initial/`；运行复现脚本默认另写到 `csrt/`，耗时会变化。

已抽查 CSRT 第 0、44、88、132、176、178 帧：目标缩小时框仍覆盖无人机，未在这些样本中发现明显跟错背景。未逐帧人工标注，因此不声称 100% 跟踪准确率。帧差第 44 帧只框住右侧局部机臂，第 132 帧没有候选，说明相邻帧变化弱时这组固定参数会漏检。

## 下一步

先添加独立参考框，测 IoU 与中心误差，比较不同首框和阈值；再加入更长的悬停、遮挡、出画和移动相机片段。只有确认自动初始化或丢失恢复是主要瓶颈后，再评估无人机专用检测模型。目前这段短录像只能支持功能联调及初步比较。

实现参考 [OpenCV CSRT](https://docs.opencv.org/4.x/d2/da2/classcv_1_1TrackerCSRT.html) 与现有 [OpenCvSharp](https://github.com/shimat/opencvsharp) 包接口。后者持续维护、采用 Apache-2.0；本次只调用已固定版本的库，没有复制外部项目源码，也未迁移到当前大版本。背景减除与帧差是不同方法，本版没有实现 MOG2。
