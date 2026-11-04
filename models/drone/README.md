# 本地实验无人机模型

第一阶段固定使用 Ahmed Darwish 的单类 YOLOv5 `best.onnx`，模型只输出 `drone`，不能区分鸟类或具体无人机机型。

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

脚本校验哈希，不覆盖内容不同的现有文件。它下载真正的 LFS 二进制，而不是 133 字节的 Git LFS 指针。程序运行时不会自动联网下载。默认开发环境从本目录读取，也可在界面选择模型文件；其他 ONNX 模型、不同哈希或 YOLO11 模型会明确拒绝，而不是误用输出解析。

## 分发状态

权重被 `.gitignore` 排除，也没有 `CopyToPublishDirectory` 项，因此不进 Git、构建输出或便携分发。首次在其他电脑测试时需要单独准备并选择这个文件。

上游推理项目标 MIT，但权重由 Ultralytics YOLOv5 微调；其关于权重许可的解释与 [Ultralytics 官方许可说明](https://www.ultralytics.com/license) 存在需要厘清的差异。本阶段只实现独立推理代码并在本机核验模型，不推断该权重可按 MIT 再分发，不改变 UAVTrack 尚未确定的整体许可证。随未来 Release 分发权重前需完成来源、训练数据和许可核对；当前用户指令没有授权新增 Release。

上游验证集分数不是本项目实测准确率。本项目首轮录像结果及局限见 [阶段计划](../../docs/12-drone-detection-plan.md)。没有复制上游的 Python 推理、搜索或云台控制代码。
