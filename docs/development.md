# 开发与验证

## 环境

- Windows x64、.NET 8 SDK（SDK 策略见 `global.json`）。不依赖 Visual Studio 或 C++/CLI。
- 完整验证需要 Node.js 20+，用于执行独立 Harness 桥的原生 Node 测试；普通桌面构建无需 Node。
- NuGet 版本和锁文件纳入版本控制。常规构建使用 `--locked-mode`，只有明确更新依赖或项目结构时才重新生成锁文件。

```powershell
dotnet restore Toboo.sln --locked-mode
dotnet build Toboo.sln -c Release --no-restore
./scripts/start-toboo.ps1
./scripts/verify.ps1
```

`verify.ps1` 检查公开文件边界、桥的并发/取消/持久化行为、.NET 测试、合成视觉、ArUco 标记和传感器故障场景，并比较合成与回放决策的哈希。不打开相机或物理串口。

## 可选集成检查

`UAVTRACK_FACE_TEST_IMAGE` 指向本地公开人脸样本时，启用原生人脸样本录像测试；未设置时明确跳过该样本测试。样本和生成录像放在忽略的目录中。

`TOBOO_HARNESS_TEST_SESSION` 指定预先创建的测试会话时，启用真实桥只读集成测试。测试会话应使用专门的合成对话，避免触碰个人会话。真实模型调用的完整验证脚本为 `scripts/test-toboo-harness.ps1`，会发送测试文本并消耗模型额度；不属于默认验证。

执行 `scripts/prepare-speech-model.ps1` 后，`TOBOO_SPEECH_TEST_PCM` 可指定忽略目录中的 16kHz / 16 位单声道原始人声 PCM，启用原生 Whisper 识别中途取消及再次识别检查。音频需短于 31 秒，只在本机处理；未提供时明确跳过。模型准备脚本优先使用上游地址，下载不可用时尝试镜像，所有来源均须通过相同的固定哈希校验。

无界面 CLI 示例：

```powershell
dotnet run --project src/Toboo.Cli -- simulate --frames 300 --out artifacts/demo
dotnet run --project src/Toboo.Cli -- sensors demo --out artifacts/sensors-demo
dotnet run --project src/Toboo.Cli -- sensors replay --input artifacts/sensors-demo/events.jsonl --out artifacts/sensors-replay
```

## 输出与隐私

`artifacts/`、`tmp/`、`data/`、`calibration/`、`local/` 和本机设置均不纳入 Git。只提交项目代码、通用文档、固定模型及合法的第三方声明。不要提交个人图像、录音、对话、凭据或机器标定结果。

## 发布

在干净提交上更新 `Directory.Build.props` 的版本，运行完整验证，然后：

```powershell
./scripts/publish-portable.ps1
./scripts/test-portable.ps1 -ZipPath artifacts/release/toboo-0.2.3-win-x64-portable.zip
```

发布脚本固定 .NET 运行时版本、核对 VC 运行库签名并附带许可证、模型和独立聊天插件；输出完整 ZIP、SHA256 文件和带提交号的 `build-info.json`。`config/serial-runtime.json` 固定应用内串口运行环境及下载哈希；准备脚本从官方嵌入式 Python 包和固定 pyserial 包构建，不复制开发者环境。解压测试屏蔽系统 Python 和外部 Python 环境变量，检查三个串口工具的导入，不打开端口，也不代替硬件验收。

便携包检查包含 Whisper / Silero 权重大小及 SHA256、四个 Windows x64 原生语音库和模型修复脚本。测试机已配置 Windows 中文语音组件时，可添加 `-SpeechSelfTest`，用内存中生成的中文语音验证 Windows 识别、Whisper PCM 入口及合成事件，不打开麦克风、音响或串口，不向模型服务发消息。

LCD 协议与固件的独立检查：准备固定 Arduino 工具链后运行 `scripts/build-uno-lcd.ps1`，它包含 C++ 超时/拆包测试和 Python 主机模拟测试，不自动烧录。便携包可加 `-LcdPort COM7` 进行显式串口检查；默认验证不接硬件。
