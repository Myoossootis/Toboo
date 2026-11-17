# ICS43432 麦克风

同一块 ESP32-S3 同时负责 I²S 采音与 UART 舵机输出。电脑端由一个 USB 串口连接接收音频并转发云台工具的文本命令。Uno 继续驱动 LCD。

## 接线

断开 USB，按模块丝印接线。以下配置使用左声道。

| ICS43432 | ESP32-S3 |
| --- | --- |
| V / VCC / VDD | 3V3 |
| G / GND | GND |
| SCK / BCLK | GPIO4 |
| WS / LRCLK | GPIO5 |
| DIN / SD / DOUT | GPIO6 |
| L/R / SEL（若有） | GND |

本项目使用 3.3V 供电和信号电平。Keyes KE4089 五针模块将音频数据针标作 DIN，官方引脚定义为数据输出，示例使用左声道，接到本项目 GPIO6 即可。其他五针版本的固定声道需按其板载接法核对。[Keyes 模块说明](https://www.keyesrobot.cn/projects/KE4089/zh-cn/latest/docs/KE4089%20Keyes%20STEM%E7%94%B5%E5%AD%90%E7%A7%AF%E6%9C%A8%20ICS%E9%BA%A6%E5%85%8B%E9%A3%8E%E6%A8%A1%E5%9D%97%20%E9%BB%91%E8%89%B2%E7%8E%AF%E4%BF%9D.html)

GPIO17/18 继续用于已有舵机驱动板。ESP32-S3 通过原生 USB 接电脑，音频速率超过普通 115200 波特串口的容量。

## 构建和烧录

使用仓库固定的 Arduino CLI 1.5.1 与 ESP32 Arduino core 3.3.12。

```powershell
./scripts/build-firmware.ps1 -Target single -EnableMicrophone
```

输出位于 `artifacts/esp32-mic-servo/build/`。合并固件沿用云台协议、会话校验、默认未使能状态及独立超时处理。采音在独立 FreeRTOS 任务中进行，通过有界队列送回主循环。麦克风命令不改变舵机使能、角度或标定。

烧录前退出占用 ESP32 的程序，确认实际串口。以下 COM6 是示例。

```powershell
$settings = Get-Content firmware/toolchain.json -Raw | ConvertFrom-Json
./tmp/esp32-tools/cli/arduino-cli.exe upload --config-file tmp/esp32-tools/arduino-cli.yaml --fqbn $settings.fqbn --port COM6 --input-dir artifacts/esp32-mic-servo/build --verify
```

重新连接 toboo 后，在设备页重新读云台状态。

## 使用

在「设备 → 语音」选择 `ICS43432 · ESP32-S3`，选择该板的串口。云台与麦克风可以选择相同串口。陪伴页按住说话，松开后采集数据会交给本地中文引擎识别，并发送给当前对话。音量条按对数刻度显示相对峰值，便于观察较轻的声音；识别前会去除直流偏移并做有上限的增益调整。

按 Esc 或停止语音会丢弃本次识别。采音最长 30 秒，CRC、序号、结束标记或设备连接异常时本次语音会失败。待机时只保留本地串口连接；原始音频在内存中处理。

显式运行以下命令可采集约三秒并检查采样数量和峰值，诊断仅写统计信息。

```powershell
./toboo.exe --mic-self-test --mic-port COM6
```

## 串口格式

通过 `TOBOO_MIC?` 核对 `TOBOO_MIC,1,ICS43432,16000,16,1,4,5,6,LEFT`，随后使用 `MIC_START,<八位十六进制ID>` 和 `MIC_STOP,<ID>`。开始确认是 `MIC_BEGIN,<ID>`。

每个二进制帧包含 16 字节头、PCM 载荷与 CRC32。头依次为魔数 `A5 5A C3 3C`、uint32 会话 ID、uint32 序号、uint16 载荷长度、uint8 类型、uint8 保留位。多字节整数与 PCM 均为小端序。类型 1 为最多 256 字节的 16kHz / 16 位 / 单声道 PCM，类型 2 为正常结束，类型 3 为采音错误，结束和错误帧载荷均为空。CRC32 使用 IEEE 多项式，覆盖头与载荷。

语音和云台文本只在完整帧边界交错。桌面程序为自有 Python 云台工具提供带随机凭据的本机代理，单个工具独占文本命令通道。旧版桌面或其他程序直接占用相同串口时，连接会失败，需要先结束原连接。

接口与电气依据见 [TDK ICS43432 手册](https://product.tdk.com/system/files/dam/doc/product/sw_piezo/mic/mems-mic/data_sheet/ics-43432-data-sheet-v1.3.pdf) 和 [Espressif I²S API](https://docs.espressif.com/projects/arduino-esp32/en/latest/api/i2s.html)。
