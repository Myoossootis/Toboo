# ICS43432 麦克风

同一块 ESP32-S3 同时负责 I²S 采音与 UART 舵机输出。电脑端由一个 USB 串口连接接收音频并转发云台工具的文本命令。Uno 继续驱动 LCD。

## 语音唤醒

在「设备 → 语音」选择麦克风。电脑麦克风使用 Windows 当前的默认输入，设备页会显示它的名称。选用 `ICS43432 · ESP32-S3` 时，选择装有合并固件的 ESP32 串口。

在「陪伴」点「开启语音唤醒」，看到「等待唤醒」后，可以说「你好」「hey」「hello」「hi toboo」或单喊 toboo。toboo 支持 toe-boo、too-boo 和 toe-bow 等读法。

看到「正在听」后开口，说完停一下。连接 Harness 时，普通对话的转写文字会自动发送。回复结束后可直接说下一句，开启朗读时等朗读结束。每轮约有 12 秒时间开口，本地模型每段最多收听 15 秒。没有听清或超时会回到等待唤醒。

连续对话期间，输入框保持可见。打字时暂停收音，发送或清空后接回原来的语音会话。离线时识别结果留在输入框。发送状态待确认时，使用「重试原请求」。音量条在语音唤醒开启期间留在原位，暂停和识别时归零。

- 说「结束语音对话」回到等待唤醒。约 12 秒没开口时也会回到待机。
- 说「关闭语音唤醒」完全关闭监听，重新开启时点输入栏的按钮。
- 可以说「打开摄像头跟踪」「关闭摄像机跟随」或「停止跟随」。开启仍需满足设备就绪条件。
- 连续语音识别到本地指令后直接处理。DeepSeek 修正后的指令也会核对原始转写；原话与指令不够接近时，会提示重说。按住说话的云端模式仍可开启「先核对文字」。

本地模型可用时，两种麦克风都通过 Silero 检测人声，用 Whisper 复核唤醒词并转写后续中文。本地模型缺失时可使用已安装的 Windows 识别。语音唤醒每次启动默认关闭。ICS43432 待机监听每段采音约 30 秒，段间自动续接。

在「设备 → 语音 → 语音识别与纠错」保存 API Key，并开启「连续语音使用 DeepSeek 纠错」，可以在本地转写后校正同音错字。连续语音仅上传转写文字，录音在内存中处理。纠错超时或建议改动超出允许范围时保留原文。该面板可对比最近一轮的识别原文和校正后文字。模块的云端音频识别仍可在按住说话时单独使用。

## v0.3.3 语音改进

在「设备 → 语音 → 模块识别与纠错」提供 DeepSeek ASR。选择 ICS43432、ESP32 串口和云端识别，输入自己的 API Key 后保存。密钥通过当前 Windows 账户加密，保存在本机 `voice.local.json`；不会写入聊天记录或发布包。更换电脑需要重新配置密钥。

录音按 16kHz / 16位 / 单声道 WAV 发送到 [DeepSeek 官方 ASR](https://api-docs.deepseek.com/guides/asr/)，语言由服务自动检测。选择云端即表示本次录音会上传并按服务计费；本地模式不上传录音。没有云端失败自动重发或自动切换服务。

可选文字纠错使用 DeepSeek `deepseek-flash`。界面保留识别原文与校正结果；改变数字、否定词或改动过大的结果会保留原文。默认先核对再发送，经过纠错的句子不会自动触发云台命令。本地 Whisper 增加三路束搜索与中文术语提示，语音详情同时显示原始音量和过载提示。

接口与错误处理已通过模拟请求测试，本地引擎已通过合成中文录音测试。系统麦克风按住说话也使用本地 Whisper，模型不可用时可用 Windows 听写。

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
./scripts/build-firmware.ps1 -Target single -EnableMicrophone -LockAllServoOutput
```

初次只接入语音或舵机类型尚未核对时，使用上面的全输出锁定构建，输出位于 `artifacts/esp32-mic-servo-motionlocked/build/`，身份为 `0.5.3-motion-locked`，保留采音与只读状态并拒绝执行器指令。采音在独立 FreeRTOS 任务中进行，通过有界队列送回主循环。麦克风命令不改变舵机使能、角度或标定。

只有已核对为角度定位型且完成机构标定的装置，才应考虑不带 `-LockAllServoOutput` 的构建；它输出到 `artifacts/esp32-mic-servo/build/`，沿用云台协议、会话校验、默认未使能状态及超时停发。连续旋转型不能依靠这套协议实现角度限位，停发不等于实际停转。完整限制见 [硬件接口](hardware.md)。

烧录前退出占用 ESP32 的程序，确认实际串口。以下 COM6 是示例。

```powershell
$settings = Get-Content firmware/toolchain.json -Raw | ConvertFrom-Json
./tmp/esp32-tools/cli/arduino-cli.exe upload --config-file tmp/esp32-tools/arduino-cli.yaml --fqbn $settings.fqbn --port COM6 --input-dir artifacts/esp32-mic-servo-motionlocked/build --verify
```

重新连接 toboo 后，在设备页读取状态。发现锁定固件时，应用会保存锁定状态；读取或保存边界不会恢复运动。

Release 的 `toboo-0.2.4-esp32s3-n16r8-ics43432-motion-locked.bin` 为完整合并镜像，仅适用于上述 ESP32-S3 N16R8 配置，使用 Espressif 烧录工具时写入偏移为 `0x0`。整片镜像会覆盖目标闪存中的原有内容；其他板型应按固定工具链自行构建。烧录不自动执行机械标定。

## 使用

在「设备 → 语音」选择 `ICS43432 · ESP32-S3`，选择该板的串口。云台与麦克风可以选择相同串口。陪伴页按住说话，看到“正在听”后开口，松开后由本地 Whisper base 模型转为中文，并发送给当前对话。使用本地 Whisper 时，麦克风转写无需 Windows 中文听写语言包。音量条按对数刻度显示相对峰值，便于观察较轻的声音。识别前会去除直流偏移并做有上限的增益调整。

完整识别为「打开摄像机跟随」时会执行本地跟随命令，条件不足会显示准备提示。本地命令可在 Harness 离线时使用，跟随仍要求 ESP32 串口、供电、工作边界和方向确认。

便携包附带约 142 MiB 的识别模型及不到 1 MiB 的 Silero VAD 人声检测模型。先检测人声片段，再转写；未检测到人声或转写片段不确定时，不发送对话。源码运行前执行 `./scripts/prepare-speech-model.ps1`，下载固定模型并校验大小及 SHA256。权重留在本地忽略目录，模型来源见 `models/speech/model.json`，许可证见 `licenses/Whisper-MIT.txt` 和 `licenses/Silero-VAD-MIT.txt`。本机 CPU 需支持 AVX2、FMA 与 F16C。依赖的上游原生运行库要求 Windows 11 / Windows Server 2022 或更新系统；较旧系统需要自行验证兼容性。

按 Esc 或停止语音会丢弃本次识别。采音最长 30 秒，CRC、序号、结束标记或设备连接异常时本次语音会失败。待机时只保留本地串口连接；原始音频在内存中处理。

显式运行以下命令可采集约三秒并检查采样数量和峰值，诊断仅写统计信息。

```powershell
./toboo.exe --mic-self-test --mic-port COM6
```

该自检同时通过共享串口读取云台状态。需要核对真人语音识别时，可加 `--recognize`，开始后对模块说话约八秒，诊断会附带识别文字、片段概率和无语音概率。没有有效识别时自检返回失败。

只有显式加 `--save-audio` 才会在本机诊断目录的 `temporary-audio` 子目录保存 WAV，路径见本次诊断报告。临时录音保留 1 小时，程序运行时每分钟检查一次，关闭期间过期的文件在下次启动时清理。可以用 `./toboo.exe --recognize-audio <录音路径>` 离线复查 16kHz / 16 位单声道 WAV。测试录音及个人识别文字不应提交到公共仓库。

## 串口格式

通过 `TOBOO_MIC?` 核对 `TOBOO_MIC,1,ICS43432,16000,16,1,4,5,6,LEFT`，随后使用 `MIC_START,<八位十六进制ID>` 和 `MIC_STOP,<ID>`。开始确认是 `MIC_BEGIN,<ID>`。

每个二进制帧包含 16 字节头、PCM 载荷与 CRC32。头依次为魔数 `A5 5A C3 3C`、uint32 会话 ID、uint32 序号、uint16 载荷长度、uint8 类型、uint8 保留位。多字节整数与 PCM 均为小端序。类型 1 为最多 256 字节的 16kHz / 16 位 / 单声道 PCM，类型 2 为正常结束，类型 3 为采音错误，结束和错误帧载荷均为空。CRC32 使用 IEEE 多项式，覆盖头与载荷。

语音和云台文本只在完整帧边界交错。桌面程序为自有 Python 云台工具提供带随机凭据的本机代理，单个工具独占文本命令通道。旧版桌面或其他程序直接占用相同串口时，连接会失败，需要先结束原连接。

接口与电气依据见 [TDK ICS43432 手册](https://product.tdk.com/system/files/dam/doc/product/sw_piezo/mic/mems-mic/data_sheet/ics-43432-data-sheet-v1.3.pdf) 和 [Espressif I²S API](https://docs.espressif.com/projects/arduino-esp32/en/latest/api/i2s.html)。
