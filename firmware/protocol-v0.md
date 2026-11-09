# PC → ESP32-S3 台架协议 v0.1

状态：esp32_bench 已实现的无执行器协议，PC 视觉应用尚未接入。**角度均为内部虚拟命令，不是轴位反馈；固件没有舵机输出。** 实际运动协议需另行审查。

当前链路为原生 USB Serial/JTAG，终端 115200、LF，支持 CRLF。另一 USB 转串口口没有应用协议。实际 UART 输出使用独立的 `protocol-single-s1.md` 协议。

## 命令

| 请求 | 响应 / 用途 |
|---|---|
| HELLO | HELLO,UAVTRACK_BENCH,0.1.0,OUTPUT_DISABLED |
| INFO | 芯片、Flash/PSRAM 字节数、64 KiB PSRAM 抽样自检、丢弃响应数 |
| STATUS | STATUS,state,pan,tilt,targetPan,targetTilt,lastSeq,OUTPUT_DISABLED |
| PING | PONG,OUTPUT_DISABLED；不延长运动超时 |
| ARM_BENCH,1 | ACK,1,ARM_BENCH,SIM_ONLY,OUTPUT_DISABLED；只启用模拟 |
| SET,2,950,850 | ACK,2,SET,SIM_ONLY,OUTPUT_DISABLED；虚拟目标 95.0° / 85.0° |
| DISARM | ACK,DISARM,OUTPUT_DISABLED；取消目标、保留虚拟当前位置 |

每秒 HB,millis,state,OUTPUT_DISABLED。状态为 DISARMED、ARMED_BENCH、TIMED_OUT。重启始终 DISARMED、虚拟角度 900；不表示真实舵机位于 90°。启动未接收序号时 lastSeq 显示 0；以后显示最近接受的 ARM_BENCH/SET 序号。

## 约束

- 数值只接受十进制无符号整数。拒绝空字段、空格、正负号、小数、额外字段。序号 0–4294967295，必须严格增大，不支持回绕；用完需重启。DISARM 不带序号、可重复且不清空序号。
- 两轴 SET 范围均为 850–950（十分之一度），越界拒绝。这是台架模拟边界，不是实物安全限位。
- 只有 ARMED_BENCH 接受 SET；已启用时拒绝再次 ARM_BENCH。拒绝的命令与 PING/INFO/STATUS/HELLO 均不延长超时。
- 一行最多 95 字符（不含 LF，CR 占一位），首字节到 LF 必须小于 200ms。超长、非 ASCII、NUL、超时残片丢弃到下一个 LF 才恢复。
- 错误为 ERR,reason,OUTPUT_DISABLED；reason 包括 FORMAT、NUMBER、RANGE、STATE、SEQUENCE、LENGTH、FRAMING。
- 输出缓冲满时丢弃响应并统计 DROPPED，不阻塞计时。ACK 仅表示接受请求；可读 STATUS 核查，不表示物理到位。

## 时间与恢复

ARM_BENCH 或有效 SET 后 500ms 未再收到有效 SET：取消未完成目标，保持最后虚拟命令，进入 TIMED_OUT。必须以新序号再次 ARM_BENCH 才能 SET。重连串口或恢复旧 SET 不会继续旧目标。

使用 millis 无符号差值，支持回绕；至少间隔 20ms 更新，虚拟变化不快于 5°/s。调度间隔超过 100ms 不追补运动，超时检查先于角度更新。这些数值不能约束真实舵机从未知位置到首条指令的运动。

协议尚无 CRC、会话随机数、认证或实际输出链路，不用于网络控制。接执行器前还需处理启动/重连残留字节及跨会话重放。固件未发命令不保证驱动板不输出 PWM；DISARM/超时也不是动力断电。今晚保持舵机板、舵机与外置电源断开。
