# 第三层 · 玩家交互与工单运行时

第三层连接玩家、Agent 会话和游戏状态。当前已实现游戏内通信和 DSH 接入，Codex 桌面转发标为 WIP。工单运行时仍处于设计阶段，第一层 Watch 与第二层游标读取已可作为接入基础。

| 层 | 职责 |
|---|---|
| [第一层 · 游戏桥接](README.md) | 在游戏进程内读取状态、提交命令、登记和求值 Watch。 |
| [第二层 · MCP 转译](layer2_translation.md) | 将 MCP 工具调用转为 Bridge 请求，提供结构化回执和 Watch 事件读取。 |
| 第三层 · 玩家交互与工单运行时 | 按目标路由通信。规划中的工单运行时负责持续消费事件、关联证据、推进状态并通知玩家。 |

## 游戏内通信

在战术界面用 `N` 打开或切换控制台，选择 `HARNESS COMMS`。底部 `Target` 控制发送目标和显示的会话记录。`Portrait: Auto` 在 DSH 下使用 `deepseek`，在 Codex 下使用 `gpt`。手选头像会保留到再次修改。面板里的 `queued` 表示已写入本地队列，送达需要对应转发器继续处理。

| 目标 | 出站路径 | 回复位置 | 验证状态 |
|---|---|---|---|
| DSH | `comms/outbox.jsonl` → `harness_link.js` → 绑定的 DSH 会话 | `dsh_tailer.js` 将可见记录写到 `comms/run/live.jsonl`，游戏读取并展示。 | 玩家已实测收发。 |
| Codex (WIP) | `comms/codex.outbox.jsonl` → `codex_link.js` → Windows UI Automation → 指定桌面聊天 | Codex 桌面聊天。 | 独立入队已实测，实际桌面投递尚未通过。 |

队列路径均相对于模组根目录。DSH 以 `--game-world` 启动时每局新建会话，切局请求停止旧游戏会话。首条消息附上 `prompts/onceopen.md`，游戏界面显示玩家原文。Codex 通过 `comms/codex.target.json` 或 `--thread` 与 `--chat-title` 指定聊天，消费游标绑定线程。测试桌面投递时可先用 `--check` 检查当前前台聊天的控件，再用 `--outbox` 指向临时队列。

DSH 的 `STOP` 位于 `SEND` 旁边，生成或工具执行期间可用。游戏写入 `comms/run/dsh.stop.json`，新版转发器消费请求，检查 5 秒时效与当前绑定的会话，再调用 `session/cancel`。面板提示已请求，DSH 接受情况看 `comms/run/link.log`。已运行的转发器需要重启才能加载新版脚本。有效、过期、会话不匹配和防重放已用模拟 RPC 验证，游戏内实际中止待验证。

安装包内的运行命令见 `harness/DSH/README.md` 与 `harness/Codex/README.md`。仓库对应说明见 [DSH](../comms/DSH.md) 和 [Codex](../comms/CODEX_DEPLOY.md)。

## 工单运行时设计

工单用于跨回合跟踪玩家交办的事项。Agent 提出事项及可观察条件，第一层在游戏主线程持续求值，工单运行时负责在两次 Agent 回合之间推进状态。第一版可覆盖「登记观察 → 命中或到期 → 通知玩家」这一条流程。

1. **登记**。记录工单 ID、玩家意图、截止时间和观察条件。通过 `sp_watch_create` 保存 `watch_id`、`world_generation` 及初始 `event_cursor`，覆盖登记时的即时命中。
2. **读回**。通过 `sp_watch_events` 从已保存游标继续读取。按规则和世界代际关联事件，以 `event_key` 去重。仅将 `evidence=true` 的事件用于确认条件命中。
3. **推进**。建议先支持观察中、已命中、已到期、已取消和待重新核对。命中时保留关联证据，到期时通知玩家并询问后续处理。工单结束时撤销仍存活的观察规则。
4. **恢复**。状态、已处理事件和游标应一同持久化，重启后从已提交的游标继续。遇到 `outbox_reset`、世界代际变化或 Watch 丢失时，先重新核对活动规则，再决定是否重建。新的世界需要重新解析单位 ID。
5. **通知与权限**。工单通知包含工单 ID、状态及可核查证据。后续战术动作沿用玩家授权和 MCP 命令回执。通知接入方式、工单存储格式和正式 Agent 的工具面仍待设计。

MVP 验收包括即时命中、延后命中、到期、重启后继续消费，以及换图后旧证据隔离。DSH 或 Codex 聊天记录只用于交互与通知，工单状态以运行时保存的状态和 Bridge 事件为准。
