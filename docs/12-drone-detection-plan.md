# 无人机自动检测与类型识别：分阶段计划

日期：2026-11-04。延续 .NET 8 / Avalonia / OpenCvSharp，训练与运行分开。第一阶段已完成；第二阶段实现与质量结果见 [第二阶段报告](13-drone-phase2.md)。不发布新 Release，不增加实体舵机输出。原有 CSRT、帧差、人脸及 ArUco 保留。

## 阶段及验收

| 阶段 | 交付 | 验收与边界 | 当前状态 |
| --- | --- | --- | --- |
| 1：自动检测基线 | 现成单类无人机 ONNX，图片/录像/摄像头入口，绘框、分数、CSV、模型指纹，明确处理空场景与旧帧 | 不需要人工 ROI 即可在已核验的正例中产生框；空场景不沿用旧坐标；原图坐标、输入所有权、NMS 与原功能回归通过；真实录像生成逐帧报告。完成软件链路不代表检测质量已验收 | 已实现；候选模型的录像检出能力弱，作为实验选项 |
| 2：质量评价、模型改进与连续跟踪 | 独立标注、场景划分、负例；比较权重/微调；评估逐帧检测或检测结合 CSRT；目标关联与重新捕获 | 按视频/场景分训练验证测试，避免相邻帧泄漏；测 precision/recall、IoU、中心误差、误报/分钟、丢失和重捕获延迟、P95 端到端延迟。与阶段 1 和人工首框 CSRT 对照。质量不合格就不进入舵机实验 | 评价、候选对照和组合跟踪已实现；飞机误报严重，质量未通过；未训练自有模型 |
| 3：机型分类（可选） | 检测后裁原图，用专门分类器区分有限的已知机型，保留“未知” | 先核查 VisioDECT / Cranfield 的标注与许可；固定可识别机型集合，测试跨场景、未知机型及小/模糊目标；输出混淆矩阵，低质量输入拒判。检测和分类分别评价 | 待评估，不作为第一阶段完成条件 |

相机内参、云台方向/限位标定、MCU 独立超时保护与实物闭环按 [硬件计划](00-project-plan.md) 推进（2026-11-06 已改选 ESP32-S3）；先用 ArUco 验证硬件，无人机检测质量未通过时不用于驱动舵机。视觉检测不会自动授权串口或舵机输出。机型是 Mavic/Phantom 等类别，不是个体身份、合法性判断或三维姿态。

## 第一阶段实现

`DroneTargetDetector` 位于 Vision，使用固定 Microsoft.ML.OnnxRuntime 1.23.2 CPU 包，Core 没有新依赖。输入可为 Gray8 / BGR8 / BGRA8，输入 Mat 不被修改。RGB 归一化、416×416 letterbox、输出分数合成、NMS、裁边与还原均在 Vision 内完成。模型通过 .NET 读取字节，支持中文路径；会先检查固定 SHA256。模型来源与未分发原因见 [模型说明](../models/drone/README.md)。

桌面目标新增“无人机（实验模型）”，图片、摄像头及录像的“目标检测”模式可用；合成场景仍固定 ArUco，CSRT 和帧差仍是独立录像算法。不会将人脸模型用于无人机分类。所有候选绘框及分数，最高分为中心观测。多人/多机交错时可能切换目标，尚未建立身份连续性。

摄像头复用后台最新帧缓冲和单调时钟，处理慢时不堆积旧画面；没有检测就输出缺失，不沿用上一帧框。原有最大帧龄 0.2 秒、最大更新间隔 0.15 秒不放宽，超时保持请求角度。录像逐帧处理，以录像帧序号/标称 FPS 作为离线时间轴，不能认为录像受到了控制。

CSV 追加 `target_score,target_class`，旧列顺序保留。无人机的 `box_*` 和分数对应最高分候选，`candidate_count` 为 NMS 后数量；无目标时坐标、分数和类别为空。状态为 `drone_detected` / `no_drone_detected`。`run.json` 使用 `yolov5_drone`，记录模型 SHA256、实际输入宽高、阈值和选择规则；ArUco 字典与 ID 为空。分数不是经过校准的正确概率。

## 使用

```powershell
# 项目根目录；首次显式准备本地模型，不进入 Git 或便携包
./scripts/prepare-drone-model.ps1
dotnet run --project src/PanTiltTracker.Desktop

# 全帧录像检测，不需要 --roi
dotnet run --project src/PanTiltTracker.App -- detect-drone --input data/processed/drone-11204831-1080p30.mp4 --model models/drone/best.onnx --out artifacts/drone-detection-phase1/recorded
```

界面选择“USB 摄像头 → 无人机（实验模型）”，模型框选 `models/drone/best.onnx`，再开始。录像选择“视频回放 → 目标检测 → 无人机（实验模型）”，选择自己的录像直接开始。旧“载入无人机示例”按钮仍会进入人工首框 CSRT 对照；要试自动检测需切回目标检测。示例录像和模型均不随源码或便携包上传。

CLI 会保存标注视频、抽样图片、CSV、run.json 和 summary.json 到指定的忽略目录。桌面摄像头模式只写数值/元数据，不新增原始画面落盘。输出不是相机标定、三维测距或实际轴位置。

## 首轮实测与下一步决策

本机 CPU，已下载的 Pexels 11204831 副本（1920×1080、30fps、179 帧、约 5.97 秒），没有人工初始化：首轮输出 21/179 帧候选，平均算法耗时 34.38 ms，P95 39.52 ms。耗时包括图像预处理和推理，不含解码、绘框、显示和编码；同机其他任务可能影响结果，不承诺 30fps。原报告保存在忽略目录 `artifacts/drone-detection-phase1/recorded/`，预测是否准确尚未逐帧标注。

首帧有可见无人机候选，但框没有完整覆盖机臂，部分帧仅覆盖局部；后续大量帧未检出。该结果显著弱于原 CSRT 在此短片中的框连续性。21/179 是“产生候选框的比例”，不能写作 recall 或准确率。不能用降低阈值就宣称解决漏检，需在独立正例和负例上比较；也不能直接以这段录像调好参数后把同一片段作为泛化测试。

阶段 2 的优先事项由此明确：先标注并量化漏检/框不完整，核验更适合本摄像头和小目标的权重/训练数据及 640 输入方案，再决定结合 CSRT 的频率及恢复策略。高分误检、相机转动、遮挡和出画仍需独立素材。固定模型的 hash 限制有意不接受任意同名 ONNX，换模型必须显式记录和验证其输入输出约定。

## 可复现验证

默认 `scripts/verify.ps1` 无需下载权重：运行时加载、letterbox/RGB、缩放裁边、分数组合、去重、无效预测、错误模型和 UI 模式约束有自动测试；两项真实权重/图像集成检查会明确跳过。

在本机开启可选集成测试（先生成未标注的实际输入首帧）：

```powershell
dotnet run --project src/PanTiltTracker.App -- inspect --input data/processed/drone-11204831-1080p30.mp4 --out artifacts/drone-detection-phase1/input-samples
$env:UAVTRACK_DRONE_MODEL = (Resolve-Path models/drone/best.onnx).Path
$env:UAVTRACK_DRONE_TEST_IMAGE = (Resolve-Path artifacts/drone-detection-phase1/input-samples/frame-00000.png).Path
./scripts/verify.ps1
```

可选检查覆盖真实 ONNX、中文路径、Gray/BGR/BGRA 空场景、输入不变、真实图片经过桌面处理链路、BGRA 输出、分数/模型日志以及无目标清空。指定的正例必须是存在且可被当前模型检出的独立输入；失败不应通过伪造检测或放宽控制超时来掩盖。

维护者可用 `PanTiltTracker.Desktop.exe --smoke-test-ui --camera-index 0 --drone-model E:/Microsensor/models/drone/best.onnx` 在当前机器执行有限帧真实相机检查。此次已通过本机 camera 0 的 13 帧原生桌面采集/推理/停止检查，记录 `target=drone`、`physicalCameraTested=true`。这是本机采集链路检查，不是用户笔记本上的无人机识别实测，没有据此宣称识别率。没有舵机实物验证。

## 调研来源

- [上游 YOLOv5 实验模型及限制](https://github.com/eahmeddarwish/drone-detection-yolov5/tree/ca515c165600a776a04d9f98db83efe829f18603)：只参考输入输出约定，未复制源码。
- [ONNX Runtime C#](https://onnxruntime.ai/docs/get-started/with-csharp.html)：CPU 部署和资源生命周期；依赖固定 1.23.2。
- [DUT Anti-UAV](https://github.com/wangdongdut/DUT-Anti-UAV)：检测与跟踪数据。
- [Drone-vs-Bird](https://github.com/wosdetc/challenge)：远距离干扰对照，数据需申请。
- [机型分类研究](https://pmc.ncbi.nlm.nih.gov/articles/PMC9410072/)：使用真值裁图测试，不能替代整条检测链路的指标。
- [VisioDECT](https://pmc.ncbi.nlm.nih.gov/articles/PMC12877808/)：六机型数据候选，不是本项目已训练模型。
