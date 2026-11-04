# 第二阶段：评价、模型对照与连续跟踪

2026-11-04。软件功能已实现；**识别质量验收未通过**，不进入舵机实验或具体机型分类。未训练自有模型，未改变控制超时，未发布 Release。

## 数据与指标

[官方 Halmstad 数据](https://github.com/DroneDetectionThesis/Drone-detection-dataset/tree/442d0c6750708cc221636f7a1fd31cbd8cb0aeb8/Data/Video_V) 的固定提交、文件哈希见 [清单](samples/halmstad-phase2.json)。全部为 640×512 / 30fps，不是亚博摄像头实测。V_DRONE_001（301 帧）用于开发对照；V_DRONE_002（301 帧）、V_DRONE_017（328 帧）用于固定参数后的其他录像评价；V_BIRD_001（305 帧）、V_AIRPLANE_001（327 帧）为无人机负例。

MAT 参考按 groundTruth 表的 `DRONE` 字段读取，严格校验行数与视频帧数。通用解码器的固定深度/字节大小捷径在部分可见光片段多读一个空项，因此本实现不使用该捷径，不静默删项或插值。MATLAB x/y 减 1、宽高不变，帧号从 0 开始。已抽查参考/预测叠加图。只有人工首框 CSRT 接收第一帧参考框，自动模式不接收参考答案。数据、转换结果和实验输出不进 Git。

没有用这些标签训练模型或逐视频调阈值；但上游训练来源未完整披露，不能保证这些录像未出现在其训练来源中，不能声称严格未知场景成绩。

评价为**选定目标的 recall@IoU≥0.5**，不是所有候选的 AP/mAP。定位不合格同时计 FP/FN，缺失帧 IoU 计 0。中心误差只对参考可见且算法报框的帧计算，需连同缺失率看。无正例时 recall 为 null，不适用的分母不伪造 100%。连续的目标缺席误报帧计一个误报事件，事件/分钟按录像标称时长计算。恢复时间依参考匹配恢复，不依跟踪器成功标志。

## 结果

| 录像 | 原模型 | best2 | best2＋CSRT | 人工首框 CSRT |
| --- | ---: | ---: | ---: | ---: |
| V_DRONE_001（开发） | 75.7% | 79.7% | 87.0% | 49.5% |
| V_DRONE_002 | 96.3% | 100.0% | 100.0% | 100.0% |
| V_DRONE_017 | 65.9% | 91.8% | 86.6% | 86.0% |

组合模式在 001 补回部分缺失，但在 017 比逐帧检测更差。人工首框 CSRT 在 001 始终报框却发生漂移，说明有框不等于跟对。更关键的是：

| 无人机负例 | 原模型误报帧 | best2 误报帧 | best2＋CSRT 误报帧 |
| --- | ---: | ---: | ---: |
| 鸟，305 帧 | 0 | 0 | 0 |
| 飞机，327 帧 | 165 | 327 | 327 |

此处按数据集官方 DRONE / AIRPLANE 类别计算，不能据此判断飞机是否有人驾驶。

额外 YOLO11 候选的开发正例召回率为 71.1%，飞机误报 325/327 帧，未解决混淆。周期检测可能重复确认错误类别，跟踪无法修复系统性分类误报。因此**默认保留原模型及逐帧方式，候选和组合模式均需显式选择**。

原 Pexels 179 帧中，原模型、best2、YOLO11 分别只产生 21、7、9 帧候选；无独立真值，只作域差异检查，不报准确率。有限短片不能外推到其他光照、机型或摄像头。

本机单次离线示例：V_DRONE_001 的 best2 算法 P95 为 18.69ms，组合模式 54.63ms；含解码、标注、编码及抽样输出的离线 P95 分别为 21.53ms、56.52ms。组合模式并非必然更快。该计时不含显示、USB、曝光和编码器最终 flush，不保证稳定 30fps。

## 组合跟踪

`DroneTargetTracker` 借用检测器、拥有 CSRT，每次使用当前图像，不用运动预测伪造观测。未锁定时自动检测/初始化，锁定后逐帧更新 CSRT，每 0.2 秒检测复核，仅关联与当前框 IoU≥0.1 的候选，不因远处更高分而跳转。1 秒未获得匹配确认即解除锁定，即使 CSRT 仍报成功；CSRT 明确失败立即重新检测；帧间隔超过 0.2 秒也清除旧状态。参数在其他评价录像前固定，仍是实验参数。

重捕获增加 `track_epoch`，它是软件锁定代次，不是物理身份。CSV 追加 `target_source,track_epoch`：detector 帧可有当前类别/分数；csrt 帧有当前视觉定位，但类别/分数为空，未检测时候选数为空。搜索时坐标、分数、来源和代次为空。界面提示“本帧未重新分类”。控制器原有 0.2 秒帧龄、0.15 秒更新间隔及无目标保持规则不变。

## 操作与复现

Python 仅用于可选 MAT 转换；应用仍为 .NET 8，无 Visual Studio 依赖：

```powershell
python -m venv tmp/drone-eval-venv
./tmp/drone-eval-venv/Scripts/python.exe -m pip install -r scripts/requirements-drone-eval.txt
./scripts/run-drone-phase2.ps1
```

脚本下载并校验数据/模型、构建独立程序快照，生成参考、CSV、summary.json、evaluation.json、标注视频及 index.json。输出必须用新目录。完整结果在 `artifacts/drone-phase2/official-run/`；YOLO11 飞机追加结果在 `artifacts/drone-phase2/airplane-yolo11/`，以 `-Variant yolo11` 准备后按相同 detect/evaluate 命令复现。

```powershell
./scripts/prepare-drone-model.ps1 -Variant candidate
dotnet run --project src/PanTiltTracker.Desktop
dotnet run --project src/PanTiltTracker.App -- detect-drone --input data/raw/drone-detection/V_DRONE_017.mp4 --model models/drone/best2.onnx --tracking --out artifacts/drone-phase2/my-test
dotnet run --project src/PanTiltTracker.App -- evaluate-drone --reference artifacts/drone-phase2/official-run/references/V_DRONE_017.csv --telemetry artifacts/drone-phase2/my-test/telemetry.csv --fps 30 --out artifacts/drone-phase2/my-test
```

界面选“录像 / USB 摄像头 → 无人机（实验模型）”，手动选 best2.onnx，按需勾选“连续跟踪与重新捕获”；图片不提供开关。不勾选就是逐帧检测。

本机 75 项测试通过，包括可选真实模型/图片/录像链路；CI 不下载数据，三项外部检查明确跳过。测试覆盖指标分母、错位、误报事件、恢复时间、所有权、超时释放、重捕获、关联、单调帧号、模式约束及跟踪帧无分类分数。可选录像检查设置 `UAVTRACK_DRONE_TEST_VIDEO`，其他两个环境变量沿用阶段 1。本机 camera 0 组合模式通过 13 帧原生采集/推理/停止检查，硬件输出关闭；不是用户笔记本无人机识别准确率测试，没有新增摄像头原图保存。

下一项必要质量工作是收集演示摄像头对应的数据，加入飞机、鸟和室内背景负例，训练/微调检测器，再在完全独立录像上重跑评价。不得用本轮飞机评价片段调好后又宣称未知测试成功。当前软件开发和实验已完成，识别质量仍待改进。
