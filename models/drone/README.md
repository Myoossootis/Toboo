# 本地实验无人机模型

默认保留第一阶段 Ahmed Darwish 的单类 YOLOv5 `best.onnx`，不能区分鸟类或具体无人机机型。第二阶段另外支持固定指纹候选，不自动替换默认。

- 来源：[上游模型](https://github.com/eahmeddarwish/drone-detection-yolov5/blob/ca515c165600a776a04d9f98db83efe829f18603/models/best.onnx)
- 固定提交：`ca515c165600a776a04d9f98db83efe829f18603`
- 文件大小：28,567,689 字节。
- SHA256：`4d078f4235221dbaa1299796d5a6bd5cc8f2210013b07e8199e5d06e88b43e8a`
- 实际图输入：float32 `[1,3,416,416]`；输出为单类 YOLOv5 `[1,N,6]`，每行 `cx,cy,width,height,objectness,class_score`。
- 推理：ONNX Runtime CPU 1.23.2，不需要 Python 或 GPU。输入 RGB / 255，等比例 resize，居中补灰色 114。使用实际整数 padding 和宽高缩放比还原到原图，不做镜像。
- 分数为 `objectness × class_score`，门限 0.35，IoU NMS 门限 0.45。全部保留框绘出，最高分为当前帧目标；无跨帧身份关联。

在项目根目录显式运行：

```powershell
./scripts/prepare-drone-model.ps1
```

脚本校验哈希，不覆盖不同内容的现有文件；下载实际二进制而非 LFS 指针。程序不会自动联网下载。可在界面选择已核验文件，未核验哈希仍明确拒绝，不猜测输出格式。

## 第二阶段候选

| 参数 | 文件 / SHA256 | 输入输出 |
| --- | --- | --- |
| `-Variant candidate` | best2.onnx，7,257,431 字节；`ad7dbd93bb7da4e20651d49d7dff399af8ffc7a2c69a4214fe0a682179318fed` | 同一上游固定提交，YOLOv5 单类 `[1,3,416,416]` → `[1,10647,6]` |
| `-Variant yolo11` | yolo11-drone-bird.onnx，12,088,461 字节；`f3b238b537fe420f21ce1e2504055a21816b5ad80a8d9346f56ac3bced1afc29` | [固定提交](https://github.com/kanishk083/DRONE-DETECTION/tree/ee6a84ac18f188f9a85363cebc2dabea257f98de)；动态 NCHW，本实验 640×640 → `[1,6,N]`，0=drone、1=bird，无独立 objectness |

YOLO11 仅保留 drone 达门限且分数不低于 bird 的候选，不按 YOLOv5 的 objectness×class 解析。两个候选均实际加载并参与对照；不接受任意同名文件。best2 和 YOLO11 未解决飞机误报，best2 在原 Pexels 片段中也更弱，不改变默认。见 [第二阶段报告](../../docs/13-drone-phase2.md)。

YOLO11 仓库未见明确项目许可，模型元数据声明 AGPL-3.0；与其他权重一样，不进 Git 或发布包。

## 分发状态

权重被 `.gitignore` 排除，也没有 `CopyToPublishDirectory` 项，因此不进 Git、构建输出或便携分发。首次在其他电脑测试时需要单独准备并选择这个文件。

上游推理项目标 MIT，但权重由 Ultralytics YOLOv5 微调；其关于权重许可的解释与 [Ultralytics 官方许可说明](https://www.ultralytics.com/license) 存在需要厘清的差异。本阶段只实现独立推理代码并在本机核验模型，不推断该权重可按 MIT 再分发，不改变 UAVTrack 尚未确定的整体许可证。随未来 Release 分发权重前需完成来源、训练数据和许可核对；当前用户指令没有授权新增 Release。

上游验证集分数不是本项目实测准确率。本项目首轮录像结果及局限见 [阶段计划](../../docs/12-drone-detection-plan.md)。没有复制上游的 Python 推理、搜索或云台控制代码。
