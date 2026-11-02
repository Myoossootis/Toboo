# Git 管理约定

## 仓库范围

项目根目录是 E:\Microsensor。追踪源代码、配置模板、包锁文件、文档、脚本及前期 Markdown。忽略 bin/obj、临时资料、实验录像、原始图像、本机标定和生成结果。现有 output/ 保留并入库；tmp/ 保留在磁盘，忽略而不删除。

正式远端 `origin` 为 https://github.com/Myoossootis/UAVTrack.git ，公开仓库，归属 Myoossootis。保留已有本地提交历史，完成验证后快进合入并上传 `main`。源码、配置和文档入 Git；Windows 便携 ZIP 只作为 Release 附件上传。首次便携版为 `v0.1.1-preview.1`，摄像头实测尚未完成，因此作为预发布。

## 日常流程

```powershell
git switch main
git switch -c codex/single-axis-control
# 实现一个阶段，运行 scripts/verify.ps1
git diff
git status --short
git add <本次修改的具体文件>
git commit -m "feat: add single-axis command transport"
git switch main
git merge --ff-only codex/single-axis-control
git push origin main
```

已有分支时用 `git switch 分支名`，不重复创建。建议一次提交只处理一个可说明的问题，标题使用 `feat:`、`fix:`、`docs:`、`test:` 或 `chore:`。硬件测量与算法变更分别提交，便于追溯实验版本。

## 版本里程碑

- v0.1.0-prep：离线软件链路及验证。
- v0.1.1-preview.1：便携分发、摄像头选择和采集诊断；等待用户笔记本实测。
- v0.1.2：用户已确认基础相机预览可用；整合画面优先 UI、正面人脸及覆盖更新包。
- v0.2.0：新摄像头真实采集验收。
- v0.3.0：STM32 与水平单轴闭环。
- v0.4.0：双轴及相机标定。
- v1.0.0：课程实验和交付材料完成。

版本号代表已完成的证据，不提前创建后续里程碑标签。共享后不强推重写历史，不将本机数据或旧眼动工程混入仓库。

## 发布约定

**用户约定：小更新完成验证后只推送代码，不自动创建或更新 GitHub Release。只有用户明确同意某个版本发布时，才整合便携包并发布；旧版本附件与标签保持不变。** 本地编译和验证不等于对外发布。

先运行 `scripts/verify.ps1`，提交源码，再运行 `scripts/publish-portable.ps1`。用打包后的 EXE 执行 `--self-test` 和 `--smoke-test-ui`，记录与真实摄像头测试的区别。Git 标签必须对应 `build-info.json` 的源码提交；Release 附 ZIP 和 SHA256 文件，不上传自检后的相机列表、运行日志或个人录像。共享后不重写历史或移动已发布标签。

项目整体的开源许可证尚未选定；公开可读不代表自动采用依赖的开源许可证。第三方许可独立保留在 `licenses/`、`THIRD-PARTY-NOTICES.md` 和发布包中。
