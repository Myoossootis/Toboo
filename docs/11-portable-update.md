# Windows 便携版覆盖更新

> 历史 UAVTrack v0.1.x 资料。旧包及对应脚本请使用 `codex/archive-drone-recognition-20261109`；当前 toboo 的源码启动方式见仓库 README。

完整便携 ZIP 用于首次运行或不确定基础版本时。覆盖包文件名中的 `update-from-X` 指定唯一支持的基础版本；当前 `v0.1.3` 的 `update-from-0.1.2` 覆盖包适用于官方 `v0.1.2` Windows x64 便携版（包括由官方上一版覆盖包升级得到的 v0.1.2）。

## 操作

1. 退出 UAVTrack，确认窗口已经关闭。
2. 如需保留回退副本，先备份原程序目录。原目录中的 `build-info.json` 可以查看版本。
3. 下载对应的 **update-from** ZIP。
4. 将 ZIP 中的文件解压到**原程序目录，即直接含有 `PanTiltTracker.Desktop.exe` 的目录**，允许覆盖同名文件。不要再套一层文件夹。
5. `models` 等新增文件夹也必须一并复制，不能只复制 DLL。然后双击原 EXE 启动。

更新包复用旧目录的 .NET、OpenCV 和 VC++ 运行库。既有日志不在覆盖范围内；未变化的配置文件不会包含在差异包中。更新后 `build-info.json` 应为 `0.1.3`，选择“USB 摄像头 → 人脸（正面 / 侧面）”并开始实验。先测试正脸，再缓慢向左右转头，观察框是否跟随；极端姿态、遮挡和暗光仍可能漏检。新增 `models/face/face_detection_yunet_2023mar.onnx` 必须存在。

覆盖包不是独立程序。基础版本不同、文件曾被自行混换，或覆盖后无法启动时，请将完整便携 ZIP 解压到新目录使用。现阶段没有后台自动更新或自动删除旧文件。

## 开发者验证

`scripts/publish-update.ps1` 对官方基础 ZIP 与当前发布目录逐文件做 SHA256 比较，只打入新增或变化的文件。若新版本需要删除旧文件，脚本会拒绝创建手工覆盖包，改用完整包。包中 `update-manifest.json` 记录基础 ZIP 的哈希、两端版本、变化文件和完整目标文件哈希。

```powershell
./scripts/publish-update.ps1 -BaseZip artifacts/release/UAVTrack-0.1.2-win-x64-portable.zip -TargetDirectory artifacts/release/UAVTrack-0.1.3-win-x64-portable
./scripts/test-portable.ps1 -ZipPath artifacts/release/UAVTrack-0.1.3-update-from-0.1.2-win-x64.zip -BaseZipPath artifacts/release/UAVTrack-0.1.2-win-x64-portable.zip
```

测试会在新的忽略目录中解压基础包、覆盖更新包，核对完整目标文件哈希，再运行应用目录内运行库、自检与桌面合成场景。不会修改用户正在使用的程序目录。
