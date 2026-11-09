# 在 toboo 中连接 DeepSeek Harness

聊天界面位于 toboo 的陪伴页和设置页。后台链路为：`TobooChatClient` → 独立的本地桥 → Harness `ctx.llm` → 已配置模型服务。无需在 Harness 或其他应用中操作聊天界面。

## 安装

1. 安装 DeepSeek Harness，至少启动一次，完成模型服务配置。
2. 从源码根目录或解压后的便携包目录执行：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/install-toboo-harness.ps1
   ```

3. 从系统托盘完全退出 Harness，再重新启动。普通关闭窗口可能只将其留在后台。
4. 打开 toboo，在设置页刷新连接并选择模型，再回陪伴页发送消息。

安装脚本只添加独立 `toboo-companion-bridge` 条目，不改全局模型选择。插件文件必须保持原路径可用；移动源码或解压目录之前，应先卸载对应配置条目后重新安装。脚本遇到已占用且路径不同的插件 ID 会停止，避免覆盖现有配置。

## 协议与隔离

桥只监听 `127.0.0.1:37848`，每个请求使用随机 Bearer 令牌。运行时连接信息保存在 `%USERPROFILE%\.toboo-harness\connection.json`；会话、请求和运行状态也位于该用户私有目录。不要将这些文件加入仓库或分享。

每轮请求显式指定模型、消息及陪伴风格，不声明工具。相同请求 ID 去重，同一会话串行；停止生成、插件重载和持久化失败均需返回明确状态，避免重复追加或幽灵回复。桥不能代替模型供应商的认证配置。

聊天文本会发送给 Harness 配置的模型服务；相机画面、个人文件和硬件命令不通过此桥发送。记录不会复用其他应用的会话目录。

## 排障

- 未连接：确认 Harness 已重新加载插件、端口未被占用，连接文件属于当前运行实例。
- 有连接但没有模型：在 Harness 中完成服务配置，再在 toboo 刷新。
- 安装提示 ID 路径冲突：先检查现有 toboo 配置指向的目录，保持原目录可用；不要删除其他插件配置。
- 调试自检：`toboo.exe --chat-self-test` 检查桥与模型目录，不向模型提交对话。日志包含服务状态，不输出 Bearer 令牌。
