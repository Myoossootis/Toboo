# 代码使用与项目引用

项目原创代码和文档使用自定义 **Toboo Attribution License 1.0**，完整条款见根目录 [LICENSE](../LICENSE)。允许使用、修改、整合、再发布和商用，条件是保留许可及版权声明，并明确注明本项目来源。

该许可证不是标准 MIT，也不宣称获得 OSI 批准或列入 SPDX 标准许可证清单。`LicenseRef-Toboo-Attribution-1.0` 仅为项目自定义标识。

## 如何注明来源

复用本项目原创代码时，在你的项目 README、致谢或第三方声明中加入以下内容，并保留 LICENSE 和相关版权声明：

```text
本项目使用了 toboo 的部分代码。
原项目作者：Myoossootis 及贡献者
原项目仓库：https://github.com/Myoossootis/Toboo
许可证：Toboo Attribution License 1.0
```

英文示例：

```text
This project uses code from toboo by Myoossootis and contributors.
Source: https://github.com/Myoossootis/Toboo
License: Toboo Attribution License 1.0
```

只分发二进制时，把来源声明放入随附文档、许可说明或可访问的“关于”页面。私有项目也应在本地保留来源说明，但不要求公开私有代码或项目信息。

论文、报告、演示或公开项目介绍中，如果描述的工作使用了本项目原创代码，应在参考文献或致谢中列出项目名称和仓库地址。例如：

```text
Myoossootis 及贡献者. toboo [软件]. https://github.com/Myoossootis/Toboo
```

建议追加实际使用的版本、提交号和访问日期以便复现。根目录 [CITATION.cff](../CITATION.cff) 提供 GitHub 可读取的引用信息。

仅运行未修改的程序不要求公开引用；没有复制代码的独立实现不因使用相同想法而受本引用条款约束。法律允许且无需版权许可的使用不受本条款限制。

## 单独许可的部分

| 范围 | 适用许可 |
| --- | --- |
| 未另行标注的项目原创代码和文档 | 根目录 Toboo Attribution License 1.0 |
| `integrations/toboo-harness/` | 保留已有 MIT 授权；不能用新条款收回此前授予的权利，引用本仓库仍值得鼓励 |
| `firmware/uno_lcd/` | 保留 GPL-3.0-or-later；按 GPL 要求提供许可、声明和适用的对应源代码，不能只凭引用替代这些义务 |
| 第三方模型、库与参考实现 | 遵循原有许可和声明，见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) 与 [licenses](../licenses/) |

新条款不追溯改变已获得的其他授权，也不将第三方代码改为本项目许可证。历史 Release 附件保持原样；后续构建会随包附带 LICENSE、CITATION.cff 和本文档。
