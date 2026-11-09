# 阶段一升级：UNO 加速度与超声波遥测

2026-11-09。软件和固件编译验收；**尚未接线、烧录或实物采样**。使用用户选定的 Keyes UNO 独立采集，ESP32-S3 舵机程序、限位、增益、速度和本机校准保持原样。

## 已完成的可运行链路

UNO 采集固件 → 115200 USB 串口 → 桌面遥测窗口 → 有效性/时间检查 → 融合判断预览 → JSONL 记录与离线回放。

- 加速度：MMA8452Q 可选驱动，校验器件 ID，±2g、50Hz 内部输出，约 20Hz 主机观测。仅在数据就绪时读取；总线错误和未启用均上报无效。
- 超声波：可选 Trigger/Echo 驱动，约 10Hz，等待回波最多 25ms；超时、示例量程以外上报无效。距离按名义声速换算，尚未校准。
- 两个通道**默认都关闭**，默认固件不初始化 I²C、不驱动测距引脚，持续上报无效，便于先验证空板链路。
- 固定缓冲区、整数单位、XOR 校验、全局递增序号、设备 `millis()` 和电脑接收单调时间。重复/乱序不刷新数据年龄；设备重启需新握手。
- 缺失、无效、超龄、倾斜、运动加速度异常和近物均撤销运动候选；恢复需要稳定条件持续 2 秒及显式恢复请求。保护清除旧人脸证据，新鲜 USB 人脸确认才产生陪伴候选。
- 桌面“传感器”窗口与现有人脸页面共存，日志写入忽略目录 `artifacts/sensors/`。关闭/停止采集会记录断开事件。实物模式只接受主窗口 USB 摄像头的连续锁定人脸；录像/图片/合成画面不作为实物在场证据。

**融合尚未接入舵机控制许可。** 这里的“姿态保护”“距离保护”是判断预览，不会停止主窗口独立运行的人脸跟随。实物验证之前不得把此页当作机械保护。固件无执行器协议，不烧录到现有 ESP32。

PIR、光感/接近、声音和 DHT 仍属后续工作；没有伪造这些模块的实物遥测。手靠近互动与夜间模式尚未实现。

## 直接运行

```powershell
./scripts/start-toboo.ps1 -Sensors
# 或在主窗口右上角点击“传感器”
```

“合成演示”运行 18 秒脚本：正常→陪伴→近物→恢复→倾斜→丢数据→无回波→恢复→无新鲜人脸。脚本中包含模拟的恢复操作；界面和 `run.json` 均标明合成来源，无硬件输出。实物模式的“确认恢复候选”只在当时稳定条件满足后生效，不会预先预约恢复。

```powershell
dotnet run --project src/PanTiltTracker.App -c Release -- sensors demo --out artifacts/sensors-demo
dotnet run --project src/PanTiltTracker.App -c Release -- sensors replay --input artifacts/sensors-demo/events.jsonl --out artifacts/sensors-replay
```

输出 `events.jsonl`、`fusion.jsonl` 和 `run.json`。回放保留记录时间、无数据间隔、人脸确认和恢复/断开事件；不使用播放电脑的墙钟。`recorded-replay` 表示从文件读取，不证明文件中的数据已做物理标定或确实来自硬件。`verify.ps1` 比对合成与回放的完整决策文件哈希。

## UNO 构建环境

沿用隔离工具目录，Arduino CLI **1.5.1**、Arduino AVR core **1.8.6**，不新增 NuGet/Python 依赖。`firmware/uno-toolchain.json` 锁定目标 `arduino:avr:uno`；主机串口复用 `firmware/requirements-host.lock.txt` 中的 pyserial。

如果工具尚未安装，先按 `firmware/README.md` 的隔离环境说明准备 Arduino CLI 与主机 Python，再安装 AVR core：

```powershell
./tmp/esp32-tools/cli/arduino-cli.exe core install arduino:avr@1.8.6 --config-file tmp/esp32-tools/arduino-cli.yaml
./scripts/build-uno-sensors.ps1
```

仅在**对应模块接线、电平和型号已确认**后编译启用版：

```powershell
./scripts/build-uno-sensors.ps1 -EnableUltrasonic
./scripts/build-uno-sensors.ps1 -EnableUltrasonic -EnableMmaAfterVoltageCheck
```

脚本只编译，不含烧录。每个开关组合写入独立的 `artifacts/uno-sensors/` 子目录，避免把默认禁用版和启用版混淆。先断开所有模块验证空板串口；接线与刷写步骤在确认实物后单独安排。

本机双通道编译结果：7426 / 32256 字节 Flash（23%），468 / 2048 字节静态 RAM（22%）。这不是运行时栈峰值或实物可靠性测量。编译器在 Arduino AVR core `new.cpp` 中有既有未使用参数警告；项目固件无该警告。

## 先核对实物，再安排接线

| 对象 | 软件预留 | 接线前仍需确认 |
| --- | --- | --- |
| Keyes UNO | USB 115200，独立于 ESP32 | 实物板型/串口；打开串口可能复位该板，不能选舵机端口 |
| 超声波 | Trigger D7 / Echo D8，共地 | 模块型号、引脚顺序、额定供电、Echo 电平与接线；当前只按 Trigger/Echo 型准备，不能套用到其他模块 |
| MMA8452Q | A4 SDA / A5 SCL，地址 0x1C 或 0x1D，WHO_AM_I=0x2A | 模块稳压/双向电平转换/上拉电压、丝印与传感器方向。裸芯片不是 5V 逻辑器件；UNO 的 I²C 上拉及模块电路必须一起核对 |
| TMD 光感/接近 | 暂不接入 | 手册型号与示例库不一致，先识别芯片；避免与 MMA 共总线时引入未知上拉 |

加速度预览当前假定**静止时 Z 轴 +1g 向上**；安装方向不一致时先转换坐标或标定，不能隐式取绝对值而掩盖倒置。倾斜由瞬时加速度估计，移动时混有运动加速度，不是陀螺仪姿态或水平舵机角度，也尚未实现拿起分类器/重力低通估计。

MMA 初始化失败后保持无效，需要排查并复位采集板重新初始化，不自动对未知 I²C 器件持续写寄存器。总线读写最多约 3ms 超时，超声波为有界阻塞，二者均不进入 ESP32 舵机循环。

## 预览阈值与时间边界

以下是开发参数，**不是实验结论或硬件安全限值**：

| 项目 | 当前值 |
| --- | --- |
| 加速度最大年龄 | 200ms |
| 距离最大年龄 | 500ms |
| 人脸确认最大年龄 | 500ms |
| 姿态异常 | 倾斜 ≥25°，或加速度模长不在 750–1250mg |
| 近物 | <150mm |
| 恢复稳定区 | 倾斜 ≤15°、模长 900–1100mg、距离 ≥200mm，连续 2 秒 |
| 融合更新断档 | >200ms 重新累计稳定时间，不积分大间隔 |
| 传输积压 | 相对最紧设备/主机时钟偏移延迟 >250ms 拒收 |

初始、重连、设备新会话均需要稳定和恢复确认。新鲜度按各通道分别计算，不能用距离包给旧加速度续期。设备毫秒与序号支持 uint32 回绕；小于半个周期的正向差才接受。首次数据的绝对传输延迟无法仅靠单向时间戳测得，故先清空串口缓冲并完成握手；设备时间标记接近读取完成时刻，不是曝光时间或独立同步时钟。

## 协议 v1

主机发送 `TOBOO?\n`，UNO 返回 `TOBOO_SENSOR,1,UNO\r\n` 并从序号 0 开始遥测。未握手时不推送数据；没有动作/参数写入指令。发生重启后主机需断开重连。

```text
T1,<seq_uint32>,<device_ms_uint32>,<A|D>,<0|1>,<x>,<y>,<z>*<XOR_HEX2>
```

- `A`：x/y/z 为整数 mg；`D`：x 为整数 mm、y/z 为 0。
- 无效位 0 时三值必须为 0；这不表示实测 0g 或 0mm。
- XOR 覆盖 `T1` 到 z 的 ASCII 字节，不含 `*` 和换行。它用于发现常见传输损坏，不是身份认证。
- 固定包长上限 96 字符，非法范围、未知版本、错误校验和格式全部拒收。无效样本替换上次有效观测。
- JSONL 外层的 `AtMilliseconds` 是电脑会话单调时间；`Line` 可空，空行事件推进新鲜度；另有 `FaceConfirmed`、`Resume` 和 `Disconnected`。

## 验证和下一次实物工作

运行 `scripts/verify.ps1`：原有视觉/舵机主机测试，加上协议损坏、序号/时间回绕、重启、重复/乱序/延迟、无效回波、失联锁存、稳定恢复和更新断档测试。桌面渲染在 720×720、480×640 验证，窄窗支持纵向滚动。

UNO 的禁用版和双通道版均编译；未连接任何串口，未刷写任何板卡，未输出舵机指令。真实采样率、I²C 故障恢复、距离误差/盲区、姿态噪声、供电和保护延迟均待实测。

本次 `verify.ps1`：**122 项通过，1 项跳过**。跳过项为既有人脸录像集成测试，需要通过 `UAVTRACK_FACE_TEST_IMAGE` 指定公开样本；本次未设置。传感器合成脚本 874 条事件、六种状态，拒收/丢包均 0，回放决策文件与生成文件哈希一致。这些均是软件/合成验证，不是硬件成绩。

下一步先核对 UNO、MMA、超声波实物正反面与丝印，然后空板握手→超声波多距离记录→MMA 静止/六面记录→故障注入→确认标定阈值。通过后再把融合许可接到现有跟随输出，并独立验证停发与显式恢复；最后加 PIR 和光感/接近。

依据：[NXP MMA8452Q 数据手册](https://www.nxp.com/docs/en/data-sheet/MMA8452Q.pdf)（器件 ID、寄存器、量程、数据就绪、电平）；[Arduino Wire 超时说明](https://docs.arduino.cc/language-reference/en/functions/communication/wire/setWireTimeout/)；本机 Labkit 手册与[上一阶段方案](18-toboo-multisensor-plan.md)。
