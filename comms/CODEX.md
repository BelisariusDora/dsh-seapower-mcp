# 游戏聊天路由

Target 决定消息队列和显示记录，Portrait 单独控制头像。DSH 使用 `comms/outbox.jsonl`，Codex 使用 `comms/codex.outbox.jsonl`。两边均支持每局新会话。

Codex 现通过独立 app-server 临时会话双向收发，启动方式和生命周期见 [Codex 游戏会话](CODEX_DEPLOY.md)。旧桌面转发器位于 `legacy/`，新入口不再绑定桌面聊天。
