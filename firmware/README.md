# 固件与主机工具

| 目录 | 目标 | 用途 |
| --- | --- | --- |
| esp32_bench | ESP32-S3 | 无执行器输出的协议与状态机验证 |
| esp32_single_servo | ESP32-S3 | 独立 UART 舵机输出、同步回位与跟随 |
| uno_sensors | Arduino UNO | 独立距离、加速度遥测，默认采集通道禁用 |
| uno_lcd | Arduino UNO | I²C LCD1602 角色动画，串口状态超时后恢复待机 |
| tools | Windows / Python | 串口检查、目标发送与传感器记录 |
| tests | 主机 | C++ 逻辑测试与显式串口测试 |

桌面视觉和对话无需这些设备。构建脚本不自动烧录；串口测试、目标发送和烧录需明确选取设备，不能把合成测试当作实物验证。

Uno LCD 的接线、固定依赖、构建和上传见 [表情屏说明](../docs/lcd.md)。`uno_lcd` 与 `uno_sensors` 当前为二选一固件，串口独占。

## 准备环境

使用 `toolchain.json` 固定的 Arduino CLI 1.5.1 和 ESP32 core 3.3.12。工具隔离在忽略目录 `tmp/esp32-tools/`，不修改系统 Arduino IDE。

```powershell
./scripts/prepare-firmware.ps1
python -m venv tmp/esp32-tools/host
./tmp/esp32-tools/host/Scripts/python.exe -m pip install -r firmware/requirements-host.lock.txt
./scripts/build-firmware.ps1 -Target bench
./scripts/build-firmware.ps1 -Target single
```

固件构建还需可用的 `g++`，用于主机状态机测试；.NET 桌面构建不需要它。输出位于 `artifacts/esp32-bench/` 和 `artifacts/esp32-single/`。

`esp32_single_servo` 的输出不是通用 PWM 舵机接口，其 UART 驱动板、电源、逻辑电平、机械方向及范围必须与实际设备匹配。首次目标前实际轴位未知，超时停发不等于动力断电。协议详见 [protocol-single-s1.md](protocol-single-s1.md)，无输出台架协议见 [protocol-v0.md](protocol-v0.md)。

源码环境中的只读串口示例（端口以实际枚举为准）：

```powershell
dotnet run --project src/Toboo.Cli -- servo --port COM6 --action inspect
```

## UNO 遥测

目标和版本见 `uno-toolchain.json`，Arduino AVR core 1.8.6：

```powershell
./tmp/esp32-tools/cli/arduino-cli.exe core install arduino:avr@1.8.6 --config-file tmp/esp32-tools/arduino-cli.yaml
./scripts/build-uno-sensors.ps1
```

默认版不采集传感器。核对实际模块和接线后，使用 `-EnableUltrasonic` 或 `-EnableMmaAfterVoltageCheck` 构建对应通道。超声波预留 Trigger D7 / Echo D8；MMA8452Q 预留 A4 SDA / A5 SCL，地址 0x1C 或 0x1D。MMA 裸芯片不是 5V 逻辑器件，模块稳压、电平转换和上拉电压必须一起核对。

加速度坐标假定静止时 Z 轴 +1g 向上。协议与数据单位见 [硬件接口](../docs/hardware.md)。UNO 采样与 ESP32 运动是独立链路，不会因打开传感器预览而使能实际舵机。
