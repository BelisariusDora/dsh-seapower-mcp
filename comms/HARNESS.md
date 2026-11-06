# Harness 接入索引

完整首次部署步骤已集中到 `<MOD_ROOT>/README.md`，先选择仅 MCP、DSH、Codex 或双后端。安装包的 harness 目录必须保持在模组根下，不能单独搬走。

- DSH/README.md：Host 插件、sender/tailer 与不同 --out 的分工。
- Codex/README.md：临时主聊天与调度器 Worker，尤其 CLI PATH 的不同来源。
- DSH/TICKETS.md：当前共享工单契约，适用于 DSH 和 Codex。
- MAIN_AGENT_MODE.md：仅主 Agent 的同轮绑定、停止与结果边界。
- TICKET_NOTIFICATIONS.md：问题通知投递到原主会话，通知轮次只允许回复。
- TICKET_DETAILS.md：宿主证据与界面详情；原始回报不等于真值。
- SETTINGS.md、LOCALIZATION.md：玩家偏好、语言文件与明确的生效时机。

共享调度器全项目一个，DSH sender/tailer 各一个，Codex chat 一个；DSH 插件在实际 Host 内运行，不另运行一份 Node 工单运行时。主会话身份、人设、首句注入与默认模型由对应 Harness 提供；游戏输入经本项目绑定到本局会话。
