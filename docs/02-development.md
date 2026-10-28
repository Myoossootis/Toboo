# 开发环境：不依赖 Visual Studio

本项目只需要 .NET 8 SDK、Git 和文本编辑器。OpenCV 的本机 DLL 由 NuGet 预编译包提供，不需要 MSVC、CMake 或原眼动工程的 C++/CLI。

`global.json` 选择本机已有 8.0.401 或同一 .NET 8 系列的较新 SDK。此前未固定版本时，机器的 .NET 9 `--info` 出现工作负载查询异常；本项目固定 .NET 8 后，CLI 还原和编译正常。没有修改系统 SDK 安装。

## 日常命令

```powershell
dotnet restore PanTiltTracker.sln --locked-mode
dotnet build PanTiltTracker.sln --no-restore
dotnet test PanTiltTracker.sln --no-restore
dotnet run --project src/PanTiltTracker.App -- simulate --out artifacts/my-test
```

只运行控制逻辑测试：

```powershell
dotnet test tests/PanTiltTracker.Tests --filter FullyQualifiedName~ControllerTests
```

完整验证运行 `scripts/verify.ps1`，使用 Release 配置，并检查生成标记、图片检测和模拟日志。第一次从 NuGet 下载依赖占用少量时间，后续可用 `--no-restore`。

## 编辑与调试

目前可以直接编辑 `.cs`，在终端查看错误和控制台输出。不要求安装特定编辑器或扩展。需要断点时，可自行选用已有轻量编辑器的 C# 调试器；CLI 构建始终作为基准。

## 依赖约定

OpenCvSharp 固定为旧项目已有的 4.10.0.20240616，托管包与 Windows 运行时包使用相同版本；新增版本升级单独提交并重新验证。`packages.lock.json` 全部入库。

若未来在另一台机器出现 OpenCvSharpExtern 加载失败，应先检查 x64、运行时依赖及 DLL 实际路径；仅补缺失运行库，不据此安装完整 Visual Studio。本机是否可用以 validation.md 的实际运行结果为准。

## 参数

默认从应用输出目录中的 tracker.json 读取，可用 `--config` 指定替代文件。未知字段、非有限增益及不合法限位会报错。把个人 COM 口或设备配置放入 `*.local.json`，不要写死在源码。
