# 真实无人机录像与算法讨论

日期：2026-10-29。本文保留素材准备与选型讨论；后续已实现人工首框 + CSRT 及帧差实验，操作与结果见 [第一版无人机视频跟踪](08-drone-tracking-baseline.md)。

## 已准备的录像

采用 Traveling on the Go 发布的 [Drone Flying on Grass Field](https://www.pexels.com/video/drone-flying-on-grass-field-11204831/)。画面确实包含外部视角的四旋翼，目标由近变远，背景为草地和树木。许可来源：[Pexels License](https://www.pexels.com/license/)。

| 文件 | 解码核对结果 |
|---|---|
| data/raw/pexels/11204831-original.mp4 | 2560×1440，标称 30fps，179 帧，约 5.97 秒，H.264 |
| data/processed/drone-11204831-1080p30.mp4 | 1920×1080，标称 30fps，179 帧，MPEG-4 Part 2 二次编码 |

下载页面和文件名声称的规格并不完全一致，以上使用实际下载文件的解码结果。测试副本逐帧降采样到 1080p，保持帧数和标称帧率，没有补帧、裁剪或拉伸。元数据、来源和 SHA256 见 samples/pexels-11204831.json。源文件及衍生视频均不入 Git。

它接近亚博 200 万像素摄像头的 1080p30 工作模式，但原摄像头视场、曝光、畸变、压缩与噪声未知。不能由此标定我们尚未到货的相机，也不能把运动命令当作对录像产生了控制。

另检查了 DroneDetectionThesis 的 V_DRONE_001 与 V_DRONE_017，解码均为 640×512 / 30fps，保留为低分辨率参考，不用上采样冒充 200 万像素。USC 数据集有人工框标注，但官方说明为 1080p / 15fps，可在后续定量评价时考虑。

## 我建议的算法顺序

### A. 数字图像处理基线：帧差或背景建模

灰度图 → 小尺度降噪 → 帧差／MOG2 前景 → 阈值与形态学处理 → 连通区域 → 候选关联 → 中心位置。

这与数字图像处理课程直接相关，便于展示差分图、二值掩膜、候选框及阈值影响。它检测变化区域，不识别无人机类别：树叶、阴影、鸟也可能成为候选；悬停或缓慢移动会影响差分响应，停留目标也可能融入背景。对很小目标，过强平滑或腐蚀会把目标消掉。

固定相机下适合先验证。以后云台转动，背景也会移动；这时需考虑背景运动补偿、暂停背景学习或转向目标外观跟踪，不能直接沿用固定背景假设。

### B. 首版实用跟踪：人工首帧框定 + CSRT

人为确定首帧目标框，CSRT 用目标外观持续定位。无需准备离线训练数据集，但跟踪器内部会更新外观模型。它知道“继续跟踪初始化的这个对象”，不具备自主判断“这是一架无人机”的语义识别能力。

该路线适合先完成单目标、无标记视频的连续定位。尺度变化、遮挡和出画仍可能导致漂移；丢失后不能把旧坐标记成检测成功，需要人工重新初始化或额外检测器恢复。

### C. 后续自动发现与重新捕获：无人机专用检测模型

YOLO 可用于自动找目标，但常见 COCO 80 类模型没有独立 drone 类；airplane 类的偶然响应不能当作可靠无人机检测。需要核对现成权重的训练类别和适用条件，或另准备标注数据微调。届时可通过 ONNX 接入 C#，无需更换整个项目语言。

优先比较 A 与 B，实际发现自动初始化／恢复是主要短板时，再加入 C。选型依据是重复实验结果，而不是模型名称或“高级程度”。

## 视频阶段应测什么

- 逐帧目标位置、框、算法是否报告有效、实际处理耗时。
- 人工参考框与预测框的中心像素误差、IoU、丢失和漂移次数。当前片段没有官方真值，需另做明确标注；不能把预测自身当作参考答案。
- 与画面中心的偏差用于未来控制输入，不等于跟踪器的定位误差。
- 同一视频比较不同方法时，初始化、图像尺寸、处理帧和有效区间保持一致。
- 本片段较短，适合功能联调；悬停、出画、遮挡、相机转动和其他场景仍需补充素材。

## 素材检查命令

```powershell
dotnet run --project src/PanTiltTracker.App -- inspect --input data/raw/pexels/11204831-original.mp4
dotnet run --project src/PanTiltTracker.App -- prepare-video --input data/raw/pexels/11204831-original.mp4 --out data/processed/drone-11204831-1080p30.mp4
```

prepare-video 只接受至少 1080p 的 16:9 输入，保留帧数和原标称 FPS，并拒绝覆盖原文件。本轮没有训练模型，也没有给出尚未测量的跟踪准确率。

## 技术参考

- [OpenCV 背景减除](https://docs.opencv.org/4.x/d1/dc5/tutorial_background_subtraction.html)
- [OpenCV CSRT：已知框初始化及后续更新](https://docs.opencv.org/4.x/d2/da2/classcv_1_1TrackerCSRT.html)
- [Ultralytics COCO 类别表](https://docs.ultralytics.com/datasets/detect/coco/)
- [USC 官方数据说明](https://mcl.usc.edu/mcl-drone-dataset/)
- [DroneDetectionThesis 数据集](https://github.com/DroneDetectionThesis/Drone-detection-dataset)
