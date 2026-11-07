# ESP32-S3 裸板台架验收记录

日期：2026-11-06。范围仅为 MCU 裸板与内部角度模拟，不含舵机、电源适配器、PWM、机械、相机闭环或完整 PSRAM 稳定性验证。

## 已有证据

- COM6，USB VID 303A / PID 1001，USB Serial/JTAG 原生口。
- esptool 5.3.1 flash-id 成功：ESP32-S3 revision v0.2，16MB Flash、内置 8MB PSRAM；Flash eFuse 为 quad / 3.3V。设备 MAC 不写入公共文档。
- Arduino CLI 1.5.1 / Arduino-ESP32 3.3.12 编译成功：程序统计 304488 字节，约占 3MB 程序分区 9%；全局变量 22960 字节。具体构建参数见 firmware/toolchain.json。
- MinGW g++ 编译同一状态机头文件，启动禁用、数值/限位/序号、500ms 边界、停顿不追补、毫秒回绕、消息分片/超长/超时行检查通过。
- scripts/verify.ps1 完成：.NET 测试 72 通过、3 个外部模型相关测试跳过；300 帧合成、遮挡及 ArUco 图片检查通过。属于 PC 软件验证，不作为硬件证据。

## 下载与实板运行

**台架固件已成功烧录并运行，用户已要求结束测试。** 最后串口状态 DISARMED，测试脚本已关闭串口，没有向执行器发送命令。

下载写入 bootloader @ 0x0、partitions @ 0x8000、boot_app0 @ 0xe000、app @ 0x10000；各段写后哈希校验均通过，没有整片擦除。应用二进制长度 304640 字节，SHA256 为 `18870a52ed78ef74e9857235aa7ce495b1bb8e352b7e5594d86f278aea902f61`。

首次自动复位后未收到应用回报；用户单按 RST 后，真实 MCU 串口检查全部通过：

- HELLO 返回 UAVTRACK_BENCH / 0.1.0 / OUTPUT_DISABLED；INFO 返回 Flash 16777216、PSRAM 8388608 字节，64 KiB PSRAM 抽样自检 PASS。这不是完整内存压力测试。
- 启动处于 DISARMED；未启用的 SET 被拒绝。
- 分片与 CRLF 正常；超长、NUL、超时残片被拒绝，解析可恢复。
- 越界、空数字、整数溢出被拒绝；无效命令不推进序号，重复序号被拒绝。
- 虚拟角度按限速变化；持续 PING 时仍进入 TIMED_OUT，旧目标被取消，后续状态保持。
- 需要显式 ARM_BENCH 才能恢复模拟；旧目标不会重现，DISARM 取消目标。
- 关闭串口 0.8 秒后重开仍为 TIMED_OUT，SET 不自动恢复；测试最后执行 DISARM。

500ms 精确边界由主机状态机测试验证；实板观察到超时状态及保持结果，没有以示波器测量时间精度。串口关闭/重开不是物理拔线；带待执行目标的复位、物理断电重启和真正执行器失联行为尚未验证。

备份限制：整片大块读取失败，ROM 读取一度卡在握手，用户 BOOT + RST 后恢复。只取得 0x000000–0x03FFFF 两个 128 KiB 数据块；0x040000 起的读取仍失败。**这不是完整原固件备份，不能保证完整回滚。** 随后项目固件写入和写后校验成功，失败的读取不计为通过。

原始记录位于被 Git 忽略的 artifacts/esp32-bench/2026-11-06：flash.log、backup-manifest.json、build.log、pc-verify.log、serial-test-after-rst/serial.jsonl 与 result.json。设备信息和局部备份不入库。

## 输出边界

源代码没有 UART1、GPIO17/18 舵机发送、PWM 或厂商舵机命令，也没有开启执行器的构建开关。程序内部的 90°、85–95° 范围和 5°/s 限速都是模拟数据。

没有使用示波器验证引脚波形，没有测试真实舵机超时行为。未来运动固件还需独立验证输出链路、初始位置、实际限位、驱动板上电动作和跨会话残留命令，不能仅凭本记录接上两只舵机通电。
