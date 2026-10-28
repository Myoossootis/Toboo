# Git 管理约定

## 仓库范围

项目根目录是 E:\Microsensor。追踪源代码、配置模板、包锁文件、文档、脚本及前期 Markdown。忽略 bin/obj、临时资料、实验录像、原始图像、本机标定和生成结果。现有 output/ 保留并入库；tmp/ 保留在磁盘，忽略而不删除。

初始仓库使用 main，完成验证后创建基线提交及 `v0.1.0-prep` 标签。没有配置远端或上传任何内容。后续确认 GitHub 仓库地址及可见性后，再绑定 origin；保留本地提交历史即可。

## 日常流程

```powershell
git switch main
git switch -c feat/single-axis-control
# 实现一个阶段，运行 scripts/verify.ps1
git diff
git status --short
git add <本次修改的具体文件>
git commit -m "feat: add single-axis command transport"
git switch main
git merge --ff-only feat/single-axis-control
```

已有分支时用 `git switch 分支名`，不重复创建。建议一次提交只处理一个可说明的问题，标题使用 `feat:`、`fix:`、`docs:`、`test:` 或 `chore:`。硬件测量与算法变更分别提交，便于追溯实验版本。

## 版本里程碑

- v0.1.0-prep：离线软件链路及验证。
- v0.2.0：新摄像头真实采集验收。
- v0.3.0：STM32 与水平单轴闭环。
- v0.4.0：双轴及相机标定。
- v1.0.0：课程实验和交付材料完成。

版本号代表已完成的证据，不提前创建后续里程碑标签。共享后不强推重写历史，不将本机数据或旧眼动工程混入仓库。

## 远端待办

记录仓库 URL、可见性、归属账号和许可证选择。当前未给项目整体添加开源许可证；依赖许可与本项目发布许可分开处理。第三方源码若以后需要复制，先核对授权并保留出处。
