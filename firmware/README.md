# ESP32-S3 台架与舵机固件

**当前板内版本（2026-11-08）**为 esp32_single_servo / v0.5.0，已烧录、写入哈希校验及只读核对，启动两轴 UART 帧计数为 0。新增独立的连续目标跟随模式，保留同步 S 曲线回位及扫动。硬件限位 S1 30–175°、S2 35–65°；默认停发。真实闭环跟随尚待现场验收。细节见 [协议](protocol-single-s1.md)、[人脸对准](../docs/17-face-aiming.md) 和 [实物记录](../docs/16-single-servo-bringup.md)。

## 手动操作

在源码构建的桌面工作台打开「舵机」页。默认串口 COM6，对应本机 ESP32-S3 原生 USB。

1. 驱动板重新接通 5V 后，轻按 ESP32 的 RST，读取状态并确认机构有活动空间。
2. 在「矩形边界设置」填写 S1 左右与 S2 上下边界，点击「保存边界」。中心自动由边界中点计算，半度向上取整。默认中心指令为 S1 103°、S2 50°。
3. 「平滑回中心」让两轴按共享 S 曲线到中心。「开启扫动」依次请求中心、左上、左下、右下、右上、左上、右下、右上、左下、左上、中心，完成一次后停发。对角线期间两轴同时改变请求角。
4. 回位和扫动运行时均可点「停止运动」。它终止上位机发送，固件在失去控制请求后最多 500ms 停发，舵机仍可能保持最后目标。卡住或持续嗡鸣时直接切断驱动板 5V。

首次初始化因实际轴位未知，会直接发中心请求，实际运动无法保证平滑。已有参考后的回位和每段路径使用五次平滑曲线，20ms 调度、名义峰值 30°/s。这里的角度和完成状态均为指令反馈，实际轴角和画面中的几何形状需要另外测量。左右按 S1 小值到大值，上下按 S2 大值到小值定义。

命令行也可使用：

```powershell
# 只读状态
dotnet run --project src/PanTiltTracker.App -- servo --port COM6 --action inspect

# 按指定边界中心同步回位
dotnet run --project src/PanTiltTracker.App -- servo --port COM6 --action home --left 30 --right 175 --bottom 35 --top 65 --execute

# 一次矩形加交叉扫动
dotnet run --project src/PanTiltTracker.App -- servo --port COM6 --action pattern --left 30 --right 175 --bottom 35 --top 65 --execute
```

串口操作记录写入 `%LOCALAPPDATA%\PanTiltTracker\servo-logs`，边界保存于同目录的 `servo.json`。工具链环境与固定依赖安装见下文。

## 原无输出台架版（v0.1）

下文“没有舵机输出”仅指原 esp32_bench。板内当前版本及其输出范围见上文。

目标为用户确认的 ESP32-S3 N16R8 带天线座。裸板读取已确认 ESP32-S3 revision v0.2、16MB Flash、内置 8MB PSRAM；原生 USB Serial/JTAG 枚举到本机 COM6。PCB 修订号和另一 USB 桥型号不由此确定。

`esp32_bench/esp32_bench.ino` 已于 2026-11-06 烧录，写后校验及真实 MCU 的 USB 命令、虚拟限位/限速、超时和串口重连测试通过。用户已结束本次测试，最后状态 DISARMED，串口已关闭。**代码没有 UART1、GPIO17/18 舵机输出、PWM、舵机库或厂商指令，也没有开启执行器的编译选项。** 本版不是舵机运动固件；电脑视觉应用仍只记录命令。

## 连接与查看

只连接电脑 USB → ESP32-S3，舵机板、舵机和外置 5V 电源全部断开。使用已识别为 COM6 的原生 USB 口；按商家 YD 图，正面朝上、天线在上、USB 在下时为左侧口。右侧 USB 转串口仍可作 ROM 下载，但本版应用协议只在原生 USB 上。

串口终端选择当前 ESP32 端口、115200、LF。发送 HELLO、INFO、STATUS 可查看信息，心跳含 OUTPUT_DISABLED。所有角度为内部模拟命令。烧录或自动测试期间关闭其他串口监视器，避免占用端口。

## 固定工具链

- Arduino CLI 1.5.1、Arduino-ESP32 3.3.12；官方地址和 SHA256 见 [toolchain.json](toolchain.json)。不依赖电脑原有 Arduino 1.8.4 图形界面。
- ESP32S3 Dev Module；Flash 16MB / QIO 80MHz；PSRAM OPI；USB Mode = Hardware CDC and JTAG；USB CDC On Boot = Enabled；Upload Mode = UART0 / Hardware CDC；分区 = 16M Flash (3MB APP/9.9MB FATFS)；整片擦除关闭。完整 FQBN 已固定。
- 工具、下载和缓存在 tmp/esp32-tools；生成文件在 artifacts/esp32-bench，均被 Git 忽略。旧 Arduino IDE 与原 EyeTracking 未修改。
- 本机 MinGW g++ 用于纯 C++ 状态机测试，乐鑫工具链编译 MCU。PC 继续用 .NET 8，不增加 Visual Studio / C++/CLI 依赖。

在仓库根目录执行：

```powershell
./scripts/prepare-firmware.ps1  # 首次准备隔离工具链，下载量较大
./scripts/build-firmware.ps1    # 主机逻辑测试 + ESP32 编译，不烧录
./scripts/verify.ps1            # 原有 .NET 项目验证
```

烧录使用独立 bootloader、partitions、boot_app0、app 文件或 Arduino CLI upload；不默认整片覆盖 16MB merged.bin。先核对芯片、端口并断开全部外设。具体结果见 [台架验收记录](../docs/15-esp32-bench-validation.md)。

## 测试与后续

[协议](protocol-v0.md) 定义命令与状态。tests/bench_controller_test.cpp 覆盖启动、限位、严格解析、重复序号、超时、毫秒回绕、停顿不追补及拆包/超长/超时行。

串口测试只对能正确回复台架标识及 OUTPUT_DISABLED 的固件发送模拟指令。隔离 Python 依赖固定在 [requirements-host.lock.txt](requirements-host.lock.txt)：

```powershell
python -m venv tmp/esp32-tools/host
./tmp/esp32-tools/host/Scripts/python.exe -m pip install -r firmware/requirements-host.lock.txt
# 台架固件已烧录并重启，外设断开后执行；COM6 以实际枚举为准。
./tmp/esp32-tools/host/Scripts/python.exe firmware/tests/serial_bench_test.py --port COM6 --out artifacts/esp32-bench/manual-test
```

串口关闭/重新打开不等于物理拔线断电。复位、供电、PWM、机械试验须分别记录，虚拟角度不是轴位反馈。

借表后按 [首次上电复核](../docs/14-first-power-on-review.md) 验证适配器、极性和空板。仍需另写/审查实际 UART 输出固件、验证驱动板上电行为，再分别接一只舵机小幅测试。未来规划 GPIO17/TX → 板 RX、GND 共地，GPIO18/RX 与通信 VCC 留空；本版不要求接这些线。保持/超时不是动力断电。
