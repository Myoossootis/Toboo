# Uno 表情屏

将 toboo 的本地实时表情线索发送到独立 Uno 的 I²C LCD1602。专用 `firmware/uno_lcd/` 固件不包含舵机控制，也不采集传感器。当前与 `uno_sensors` 是二选一固件，同一 Uno 端口不能同时打开两个连接。

## 接线和使用

适用于 5V、四针 I²C LCD1602。先断开 USB，按模块丝印接线：

| LCD | Uno R3 |
| --- | --- |
| GND | GND |
| VCC | 5V |
| SDA | A4 / SDA |
| SCL | A5 / SCL |

此表不适用于未经确认的 3.3V 显示模块。Uno 通过 USB 供电；按标签识别引脚，不按线色。可缓慢调节转接板电位器改善对比度。

![四线连接示意](images/uno-lcd-wiring.svg)

1. 按下文构建并上传 Uno LCD 固件。上电后应显示 `(?)` 和 `No valid cue`。
2. 启动 toboo（便携版或源码版均可），在“设备 → 表情屏幕”填写设备管理器确认的 **Uno** COM 端口，点击“连接表情屏”。打开 Uno 串口会使板卡复位，连接需要几秒。
3. 选择 USB 摄像头，保持“本地表情识别”开启，点击“开启视觉感知”。每 200ms 最多发送一次最新有效结果；每次等待 Uno 确认后才取下一次结果，不排队重放旧表情。
4. 连续确认的实时结果显示颜文字和英文标签。停止相机、关闭表情、丢脸或结果过期时发送 Unknown。连接异常保留相机预览；Uno 内部独立超时清除旧表情，最后一次接收后最多 500ms。

| 类别 | 第一行 | 第二行 |
| --- | --- | --- |
| 开心 | `(^_^)` | `happy` |
| 中性 | `(._.)` | `neutral` |
| 悲伤 | `(T_T)` | `sad` |
| 愤怒 | `(>_<)` | `angry` |
| 厌恶 | `(-_-)` | `disgust` |
| 恐惧 | `(O_O)` | `fearful` |
| 惊讶 | `(o_o)` | `surprised` |
| 等待 / 无效 | `(?)` | `No valid cue` |

LCD1602 通常没有中文字库，使用 ASCII 颜文字与英文。图片、录像、合成输入不发送为实时硬件线索。模型分数不是心理状态测量；界面屏幕预览代表最近得到固件确认的请求，不是对实际液晶像素的测量。

## 构建和上传

沿用 [隔离串口工具环境](../firmware/README.md)，固定 Arduino CLI 1.5.1、AVR core 1.8.6、pyserial 3.5。新增显示驱动 **hd44780 1.3.2**，仅安装到 `tmp/esp32-tools/user/libraries`，自动探测 I²C 地址和转接板映射。

```powershell
./tmp/esp32-tools/cli/arduino-cli.exe lib install hd44780@1.3.2 --config-file tmp/esp32-tools/arduino-cli.yaml
./scripts/build-uno-lcd.ps1
# COM7 只是示例：上传前确认实际 Uno 端口，并断开 toboo 屏幕连接。
./tmp/esp32-tools/cli/arduino-cli.exe upload --config-file tmp/esp32-tools/arduino-cli.yaml --fqbn arduino:avr:uno --port COM7 --input-dir artifacts/uno-lcd --verify
```

构建脚本运行纯 C++ 协议测试、Python 主机模拟测试并编译固件，不自动烧录。输出在忽略目录 `artifacts/uno-lcd`。实际串口验证会短暂显示七类表情，可显式运行：

```powershell
./tmp/esp32-tools/host/Scripts/python.exe firmware/tests/serial_lcd_test.py --port COM7 --out artifacts/uno-lcd/hardware-validation.json
```

测试记录区分真实 Uno 协议交互与光学显示检查。桌面使用内置 .NET 串口实现，便携包无需 Python 或源码目录。Python 工具仅用于可选的协议调试；源码启动入口为仓库根目录 `start-toboo.cmd`。

便携包的显式实物自检：`toboo.exe --lcd-self-test --lcd-port COM7`（端口以实际 Uno 为准，先断开已有屏幕连接）。自检只向屏幕发送 Unknown / neutral / Unknown 并核对回复，不启动摄像头或云台。

## 协议

115200、8N1、ASCII、LF。启动和重连先清空旧表情。

```text
host: TOBOO_LCD?
UNO:  TOBOO_LCD,1,UNO,16,2
host: E,happy,300
UNO:  OK,happy
host: LCD_STATUS?
UNO:  LCD_STATE,happy
```

`E,<label>,<ttl_ms>` 只接受表中八个英文标签，ttl 为 1–500ms。上位机从结果 500ms 有效期中减去样本年龄和 50ms 传输余量，年龄不足时发送 Unknown；不会周期重发缓存的失效线索。非法行回复 `ERR`，不延长旧结果寿命。输入缓冲固定 40 字节，分片停顿 ≥100ms 丢弃到行尾。时间使用单调计时与 uint32 差值，覆盖 millis 回绕。

只有 LCD 初始化成功才返回握手。I²C 写失败回复 `TOBOO_LCD_ERROR,<code>`，需检查接线和复位；写成功不证明对比度或字符实际可见。PC 握手失败不发送显示命令，ACK 超时断开，屏幕连接不影响 ESP32 串口。

驱动来源：[hd44780](https://github.com/duinoWitchery/hd44780)。显示固件使用 GPL-3.0-or-later，详见 [许可](../licenses/hd44780-GPL-3.0.txt)；该依赖不链接到 .NET 桌面程序。
