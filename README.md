# toboo · 桌面陪伴机器人

EESM 5840 期末项目：以本地视觉、双轴云台和 Labkit 多传感器融合构建桌面陪伴机器人。

当前已实现本地正面/侧面人脸检测、空间锁定、双轴跟随、平滑回中心、矩形与交叉对角线检查、工作区限位，以及串口异常时保留预览的降级处理。现役 S1 工作请求 30–175°，S2 50–150°，中心为 (103°,100°)。所有角度均为请求，不是实测轴位。响应速度改进安排在新 S1 舵机到货后。

新增本地七类表情线索识别：YuNet五点对齐、MobileFaceNet模型、质量拒判、时间平滑与连续确认；结果可为未知，不代表真实心理情绪。界面“表情”页显示分数与颜文字，表情不控制舵机。开源比较、参数和验证见 [表情识别说明](docs/20-facial-expression.md)。

已加入 Keyes UNO 加速度/超声波采集固件、桌面遥测入口、融合判断预览和确定性回放；软件及固件编译通过，尚未接线或烧录。融合判断尚未控制舵机。传感器唤醒、近手互动和环境模式仍待开发；详见 [遥测阶段说明](docs/19-uno-sensor-telemetry.md) 与 [多传感器方案](docs/18-toboo-multisensor-plan.md)。

## 启动

主界面现为“陪伴、设备、设置”三页，按已选商业化概念图还原。摄像头和表情入口在设备页；原有采集、舵机、对准、录像与日志工具仍可从“高级调试”打开。未接入的对话和语音功能明确显示待接入。详见 [三页界面说明](docs/21-commercial-shell.md)。

需要 Windows x64 与固定的 .NET 8 SDK，不依赖 Visual Studio 或 C++/CLI。

```powershell
./scripts/start-toboo.ps1
# 直接进入舵机页
./scripts/start-toboo.ps1 -ServoCalibration
# 打开传感器遥测页（仅预览，不控制舵机）
./scripts/start-toboo.ps1 -Sensors
# 直接进入设备页查看表情识别（默认不启动相机或舵机）
./scripts/start-toboo.ps1 -Expression
```

开发编译后的程序为 `artifacts/toboo/bin/PanTiltTracker.Desktop/release/toboo.exe`。默认只打开界面；动作需要读状态、确认供电并明确点击回位、扫动或跟随。

本地调试也可运行：

```powershell
dotnet run --project src/PanTiltTracker.Desktop -- --face-preview
./scripts/verify.ps1
```

相机仍采用本地 YuNet 模型，支持正面和侧面人脸；不识别身份，不隐式翻转算法坐标。相机 FOV 目前为估算，跟随请求不能当作机械角度精度报告。

## 产品边界与 Git

无人机识别、重捕获、模型准备和评价代码封存在 `codex/archive-drone-recognition-20261109`，完整快照为 `9c98b66`。toboo 主线不再包含该功能，不从归档分支整体合并。通用标记检测、视频读取、CSRT、帧差、日志和控制器继续复用。

`verify.ps1` 包含产品边界检查，阻止无人机专用入口/集成重新混入 toboo。原 GitHub 仓库地址及共享历史保留，后续产品更名不改写既有提交；本次不发布 Release。见 [Git 约定](docs/05-git-workflow.md)。

## 开发与资料

- [多传感器实施方案](docs/18-toboo-multisensor-plan.md)：模块清单、融合场景、采集架构、量化实验和课程时间表。
- [人脸对准与跟随](docs/17-face-aiming.md)：限位、跟随、视觉/串口故障处理及验证。
- [舵机调试](docs/16-single-servo-bringup.md)、[固件协议](firmware/protocol-single-s1.md)。
- [架构](docs/01-architecture.md)、[开发](docs/02-development.md)、[本机标定](calibration/README.md)。

内部解决方案、命名空间、原本地配置目录和串口协议继续保持兼容名称 `PanTiltTracker` / `UAVTRACK_PANTILT`，以复用已有固件与已验证校准；用户可见产品和可执行文件名称为 toboo。

原始图像、视频、本机配置与标定结果均放在忽略目录，不提交。第三方许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。原 EyeTracking 项目保持独立。
