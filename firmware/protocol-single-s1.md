# 台架舵机协议

## v0.5.3 新 S2 逐度校准与独立保存

- S2 手动协议范围为 0–180°，这是请求范围，不是装机后的安全机械范围。首次仍须显式 `CENTER_S2 ... 45`。在已观察的 45–90°外，单轴 `MOVE_S2` 每条请求最多改变 1°，否则 `ERR,CALIBRATION_STEP`；运动间隔仍至少 200ms。主机也在 ARM 前执行同一检查。
- `reference --axis S2 --execute` 只为 S2 发 45°定位请求；`nudge` 的 `--tilt-floor` 和 `--tilt-ceiling` 限制单步边界。inspect 返回固件允许的各通道请求范围。启动和读取不发动作。
- 新应用用独立的 S2 舵机配置标识，旧 S2 下限、上限、中心和对准确认不再沿用，S1 有效边界与相机参数保留。初始校准区间 45–90°来自用户实物观察，不视为机械极限。
- 校准时只使用 S2 ±1°，观察后记录上下边界，留出碰撞和卡住前的余量。保存后中心取新边界中点，退出校准模式，手动按钮受保存边界约束。新 S2 方向需要重新确认。
- 双轴及跟随的协议上限相应支持 S2 0–180°；应用仅在新 S2 边界已保存时允许这些模式，跟随 ARM 仍必须显式传入收窄后的边界，并要求两轴已有范围内的请求参考。S1 范围不变。独立超时、会话、序号和默认停发保持不变。

## v0.5.2 新 S2 舵机单轴校准

- 仅单轴 S2 的手动请求上限从 65°扩至 90°，下限仍为 35°；首次显式定位仍为 45°。后续按 1°/200ms 移动，不循环、不自动回位。
- S1、S3 范围不变。双轴回位/扫动及人脸跟随的 S2 上限仍为 65°；若单轴校准后 S2 请求高于 65°，拒绝启用双轴模式，须先人工确认并移回原范围。
- 新蓝色 SG90 的请求角度与旧舵机机构姿态不同，旧边界和中心不能当作新舵机校准结果。当前实物只确认 45°、50°姿态可用；90°待用户观察验证。UART ACK/发送计数不代表实际轴位。
- 原有开机/ARM 无动作、500ms 失联关闭 UART、会话/序号检查保持不变。

## v0.5.0 可更新目标的人脸跟随

- 保留 v0.4.0 的回位、扫动和单轴微调。`HELLO` 版本变为 `0.5.0`，启动仍为 DISARMED、两轴请求参考未知、GPIO17/18 输入态，没有上电动作。
- `ARM_FOLLOW,session,seq,panMin,panMax,tiltMin,tiltMax` 单独进入 `ARMED_FOLLOW`。限位只能收窄 S1 30–175°、S2 35–65°，两轴已有末次请求且在新边界内才允许启用。ARM 不发送角度，不替代回位。
- `FOLLOW_BOTH,session,seq,panCentiDegrees,tiltCentiDegrees` 接受百分之一度的主机目标表示，运动中可覆盖目标。实际驱动板 UART 仍只有整数度。独立 20ms 更新，误差比例系数 6/s，内部名义速度上限 20°/s、加速度上限 80°/s²；边界和显式 HOLD 优先于加速度渐变。
- `HOLD_BOTH,session,seq` 丢弃目标和未发出的亚度累积，停在两轴末次整数请求，无 UART 帧。不是实际轴位或断电确认。
- FOLLOW 目标有效期 **150ms**。`KEEP_FOLLOW,session,seq` 只续期通信许可，不能延长旧目标；目标过期自动 HOLD。500ms 无有效控制命令、超过 100ms 固件调度停顿、UART 失败均关闭输出。停发更换 session，旧指令失效。
- 跟随与原双轴/单轴模式互斥；越界、旧序号、旧会话及格式错误不续期。双轴整条目标先校验后接受，部分 UART 输出失败转 FAULT。
- `face_follow.py --execute` 使用逐行 JSON 管道，打开只读检查，显式 `start` 才启用；无自动初始化。`start` 包含 `bounds:[panMin,panMax,tiltMin,tiltMax]`、`reference:[pan,tilt]` 和 `axes:S1|S2|both`。`frame` 包含 `permit:boolean,age_ms:number,pan:number,tilt:number`。未许可或帧龄 >200ms 只 HOLD。250ms 无主机输入、EOF、停止及异常均尝试 DISARM。进程被杀仍有 MCU 独立超时。
- 状态 `reference` 是已发 UART 请求，不是编码器反馈。发生 HOLD 后主机同步回该参考，放弃未执行的请求超前量。主机仅允许实时摄像头人脸输入驱动实物，不允许录像、合成或图片接输出。

## v0.4.0 双轴 S 曲线插补

- 硬件指令范围为 S1 30–175°、S2 35–65°。`ARM_BOTH,session,seq` 启用双轴 UART，`KEEP_BOTH,session,seq` 续期 500ms 许可。`DISARM`、超时、UART 失败和超过 100ms 的调度停顿取消两轴未完成目标。旧单轴微调入口继续保留。
- `INIT_BOTH,session,seq,pan,tilt` 只在两轴指令参考均未知时接受，显式发送一次中心请求。该初始化没有实测位置，实际首段运动无法保证限速。启动和 ARM 不自行发出角度。
- `MOVE_BOTH,session,seq,pan,tilt` 要求两轴已有参考。每段使用相同的五次平滑进度 `10u³−15u⁴+6u⁵`，每 20ms 调度，目标为整数度。持续时间按最大轴跨度与曲线峰值导数 1.875 计算，名义最高速度为 30°/s，最短 400ms。两轴分时发送 UART 帧，同一时间参数使对角线在指令空间内同步。
- `MOTION` 返回 `MOTION,MOVING` 或 `MOTION,IDLE`。主机必须等本段 IDLE，避免量化角度提前到终点时覆盖仍未结束的曲线。
- 桌面中心从边界计算，半度向上取整。默认边界下为 (103,50)，数学中心为 (102.5,50)。坐标约定为 S1 小值左、大值右，S2 大值上、小值下。用户修改并保存边界后，下次操作取新的几何中心。
- `pattern` 主机依次发中心、左上、左下、右下、右上、左上、右下、右上、左下、左上、中心。停止按钮结束上位机后续发送，固件独立看门狗停发。没有电源切断或轴位反馈。

## v0.3.4 根据现场反馈调整工作范围

- 用户报告 S1 极限为 25–180°，据此设置指令工作范围 30–175°，两端各留 5°余量。桌面默认开放完整工作范围，越界按钮禁用。
- S2 下限暂放宽至 35°，上限保持 65°，首次参考和默认回正保持 45°。35–40° 需用户逐度进行实物确认。俯仰扫动仍为已设置的 40–65°，新下探段通过微调验证。
- 固件目标限制、主机版本限位和桌面回位输入同步更新。旧校准的 S1 回位值约束到新范围，原本较窄的试调边界不再限制新工作范围。实际轴角与机械极限仍依赖现场测量。

## v0.3.3 俯仰保护与水平范围校准

- S2 指令范围限制为 40–65°。首次参考只接受 45°，无角度的旧式 90° 首次请求会被拒绝。回位主机从 45° 建立参考，避免原来的 88° 初始化请求。实际位置未知时，首次动作的机械路径仍须由现场确认。
- S1 固件允许 0–180° 指令，桌面从 80–100° 开始，到达边界后可分别每次向左或向右开放 5°。开放边界不会发送舵机帧。首次 S1 参考仍要求不低于 80°。
- S2 的超范围目标及从超范围指令参考返回的请求均被拒绝。俯仰扫动改为 40°→65°，保持逐度限速与 500ms 超时保护。
- 桌面会在本地 `%LOCALAPPDATA%\PanTiltTracker\servo-logs` 保存逐次串口记录。反向微调测试覆盖发送、停发、重新启用及反向首帧。

## v0.3.2 单轴扫动

- S1 固件指令范围为 45–100°，S2 为 45–90°。首次 `CENTER` 仍限定在 S1 80–100°、S2 88–90°，S1 低于首次范围时返回 `FIRST_PAN_RANGE`。桌面的日常 S1 微调下限仍为 80°。
- 主机 `sweep` 要求显式指定 S1 或 S2，只对该轴发送请求。先到 45°，停约 0.5 秒，再按固件 1°/200ms 扫到 90° 后停发。上行段核对 45 帧。取消时上位机停止发送，独立的 500ms 失联保护关闭 UART 输出。驱动板动力保持独立供电。

## v0.3.1 可校准回位

- `HELLO` 版本为 `0.3.1`。S1 指令限位 80–100°，S2 指令限位 45–90°，仍只允许一次启用一轴。桌面默认只开放俯仰 85–90°，用户按实物观察分段扩展。
- `CENTER_S1,session,seq,angle` 和 `CENTER_S2,session,seq,angle` 可显式指定首次角度。保留无角度的 90° 调用。S2 首次角度限定 88–90°，越界返回 `FIRST_TILT_RANGE` 或 `RANGE`。回位目标低于 88° 时，主机先从 88° 建立参考，再按 200ms/° 的节奏移动。固件的 500ms 失联停发保护保持生效。
- 上位机通过 `inspect` 读取两轴最后发出的角度，通过 `nudge` 相对这一指令值增减 1°。轴位未知时先回位。状态不包含实测轴角。

## v0.3.0 双轴台架版

- 与 v0.2.1 使用相同接线和串口。`HELLO` 返回 `HELLO,UAVTRACK_PANTILT,0.3.0,UART_CAPABLE,DEFAULT_DISARMED`。
- S1 对应水平轴，输出 `$A080#` 至 `$A100#`。S2 对应俯仰轴，输出 `$B088#` 至 `$B092#`。通道编码依据 [Yahboom 的 STM32 串口例程](https://www.yahboom.net/study/16-servo-ctrl/)。
- 同一时间仅能启用一个轴。S1 保留 `ARM_S1`、`CENTER_S1`、`MOVE_S1`、`KEEP_S1`。S2 使用对应 `_S2` 命令。请求轴与启用轴不一致时返回 `ERR,AXIS,S1_S2`，不消耗序号、不续期。`DISARM` 为全局操作。`CENTER_S1` 和 `CENTER_S2` 分别发送 90° 指令来建立请求参考。该值对应的装配姿态需实测，首次请求引起的实际运动不受逐度限速约束。
- `STATUS` 查询 S1，`STATUS_S2` 查询 S2。两轴分别记录 `last`（末次已发送请求）和 `target`（当前计划目标），均来自固件内部状态。状态、会话、序号和发送计数全局共享。启用时状态指明 `ARMED_S1` 或 `ARMED_S2`。每次发完单次请求后关掉 UART 发送，驱动板仍可能保持舵机供电和 PWM。
- `firmware/tools/single_servo.py --port COM6 --axis S2` 默认只读状态。实物试验仅限人工在场。动力断电后重新评估机械初始位置并重启 ESP32，清空两轴的指令参考。

## v0.2 单舵机 S1 版

这是 `esp32_single_servo` 的**实际 UART 输出固件**，区别于永久无输出的 `esp32_bench`。PC 视觉应用仍没有串口传输。只用于人工在场的 S1 小范围试验，不做自动扫描，不控制其他通道。

## 接口与输出

- PC：原生 USB Serial/JTAG，终端 115200，ASCII + LF（支持 CRLF）。
- 舵机板：GPIO17/TX → 驱动板 RX，GND 共地；UART1 / 9600 / 8N1。GPIO18/RX 配置为输入接收但通常留空；其数据丢弃，不参与命令处理。不接驱动板 TX 或通信 VCC。
- v0.2.1 只允许发送 `$A080#` 至 `$A100#`，正好 6 字节，无换行。旧版 v0.2.0 的范围为 `$A088#` 至 `$A092#`。格式依据用户归档的厂家 STM32 UART_Servo 函数，A 对应 S1；传输层再次检查通道与范围。
- 启动/复位时 GPIO17、18 为输入，不启动 UART1；ARM_S1 只初始化 UART，不发角度。DISARM、超时或故障关闭 UART 并将两脚设为输入。这不是物理电源开关，也不能保证驱动板不自行输出 PWM。
- 每条 UART 帧写入后以最大 20ms 等待外设发送完成（6 字节在 9600/8N1 下约 6.25ms）；失败进入 FAULT，不再追加命令。发送完成只说明 MCU UART 状态，不是驱动板接收确认或轴位反馈。

## 状态与会话

状态为 DISARMED、ARMED_S1、TIMED_OUT、FAULT。重启的 last / target 为 -1（未知），不保存旧位置或启用状态。

session 是 16 位十六进制启动标识 + 连字符 + 8 位十六进制周期编号。启动时临时启用 ESP32 内部熵源生成标识，之后关闭；不启用 Wi-Fi/蓝牙。每次 DISARM、超时、输出故障都会更换周期，旧会话命令失效。它用于避免旧命令误重放，不是身份认证或联网安全协议。

序号 seq 为无符号 32 位十进制整数，ARM/CENTER/MOVE/KEEP 接受的序号须严格递增，同次启动内不回绕。DISARM 不清空已接受序号。全部命令必须完整、一行少于 96 字符，首字节至 LF 小于 200ms；超长、NUL、非 ASCII、过期残片丢弃至 LF。

| 命令 | 行为 |
|---|---|
| HELLO | HELLO,UAVTRACK_SINGLE_S1,0.2.1,UART_CAPABLE,DEFAULT_DISARMED |
| INFO | 芯片、内存、UART 引脚/波特率、丢弃 USB 响应数 |
| STATUS | STATUS,state,session,lastSeq,last,target,frames,S1_ONLY |
| PING | PONG,S1_ONLY；不延长输出许可 |
| ARM_S1,session,seq | 显式开启 UART，首次仍不发送角度 |
| CENTER_S1,session,seq | 仅 last=-1 时允许，发送一次 S1 的 90°请求 |
| MOVE_S1,session,seq,angle | 有请求参考后才接受，整数 80–100°，逐度推进目标 |
| KEEP_S1,session,seq | 显式延长当前控制许可，不设新目标 |
| DISARM | 取消未完成目标并关闭 UART，随时可用，无序号 |
| REBOOT_SAFE,session | 仅 DISARMED 且会话匹配时软件复位；不用于正常运动控制 |

成功响应 ACK,seq,verb,REQUEST_ONLY，或 ACK,DISARM,TX_DISABLED。错误为 ERR,reason,S1_ONLY，包括 FORMAT、NUMBER、SESSION、SEQUENCE、STATE、REFERENCE、RANGE、UART、FRAMING 等。拒绝不喂超时、不消耗序号；UART 启动/发送故障则已消耗该请求序号并失效会话。心跳每秒一次，包含 TX_ENABLED / TX_DISABLED。USB 缓冲满可丢响应，不阻塞控制。

## 限制与超时

ARM/CENTER/MOVE/KEEP 最后一次有效请求后 500ms 失效，先取消目标再处理新命令。PING、INFO、STATUS、错误输入均不续期；必须读取新 session 显式重启控制。程序无法知道用户已拔掉串口，0.5 秒是未收到有效控制请求的期限。

有请求参考后，最多每 200ms 发一次相差 1°的命令（5°/s）；调度中断超过 100ms 取消待执行目标，不追补。首次 CENTER 只能请求中位，**未知轴位到 90°的实际运动不受该限速约束**。80–100°是本版试验用的软件约束，实际机械安全范围仍待测量。

驱动板动力断电后，必须重新评估初始位置并重启 MCU，不能将 MCU 的 last 当成仍有效的实际轴位。保持、DISARM、失联、MCU 复位都不能切断驱动板的 PWM 或动力；遇到干涉/堵转需人工切断 5V 并支撑机构。

## 构建和人工操作

`scripts/build-firmware.ps1 -Target single` 使用同一固定 N16R8 工具链，先运行独立主机测试，再构建到 artifacts/esp32-single/build；原台架固件及产物保留。不要把该程序作为自动开机回中位固件。

`firmware/tools/single_servo.py --port COM6` 默认只查询状态。它验证固件标识后才操作，动作需显式指定 --action center 或 --action move --angle 92 并带 --execute；只完成这一次请求，不扫描。期间按需发 KEEP，结束/异常后 DISARM，并报告最终状态和“轴位未测量”。人工先检查接线、机械活动空间和动力切断条件，再执行动作命令。

测试脚本 `firmware/tests/serial_single_servo_test.py` 会发实际 UART 字节，仅限舵机板、舵机及外置动力均断开的 MCU 裸板。必须带 --outputs-disconnected；这表示操作前条件，不是程序能检测到外设已断开。

官方 API 依据：[Arduino-ESP32 UART](https://docs.espressif.com/projects/arduino-esp32/en/latest/api/serial.html)、[ESP32-S3 随机数源](https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-reference/system/random.html)。

## v0.5.1: independent S3 replacement-servo bench channel

S3 is a separate manual test channel, restricted to requests 30–90°. It does
not change the S1/S2 pair or face-follow channel mapping. `STATUS_S3` is read
only. `ARM_S3`, `CENTER_S3`, `MOVE_S3`, and `KEEP_S3` use the existing session
and sequence rules; an armed single channel rejects actions on other channels.
The only first `CENTER_S3` request is 60°. Its starting physical position is
unknown, so this first request cannot be slew limited. Later `MOVE_S3` requests
advance by at most 1° per 200 ms. Driver output uses `$Cxxx#` at 9600 baud.
Boot/ARM emit no angle. The 500 ms watchdog, DISARM, replay protection and UART
fault handling apply unchanged. DISARM stops new UART commands; it does not
cut driver power or stop the driver's existing servo PWM.

With external driver 5V disconnected, compile and flash first. After power and
mechanical clearance are checked, initialize S3 explicitly at 60°, observe it,
then use `single_servo.py --port COM6 --axis S3 --action sweep --execute` for
30° → 90° at approximately 5°/s. This is a one-shot test, with no automatic
return or repetition. All reported angles are requests, not measured positions.
