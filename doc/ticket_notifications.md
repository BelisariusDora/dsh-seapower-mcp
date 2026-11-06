# 工单异常通知闭环

归属：第三层通知调度；文字呈现属于第四层。参见 [四层文档索引](README.md)。

2026-11-06。实现 Worker 异常结果 → Python 通知事件 → 原主 Agent 仅文字转述 → 游戏通联面板。

## 触发与归属

调度器为新产生的 blocked、needs_input、failed、uncertain、timed_out 生成一次问题通知；正常执行、成功完成、玩家取消和隐藏不唤醒主会话。Worker 仍只向宿主提交结构化结果并退出，不主动发送 parent inbox 消息。Worker 模式主 Agent 建单后结束轮次，Worker 独立执行；仅主 Agent 模式同轮自行执行和报告，已结构化报告的问题不重复唤醒。

创建来源由 Harness 宿主注入 origin={harness,sessionId,worldKey,turnId}（旧来源允许缺少 turnId，主模式必须绑定轮次），不向模型提供选择通知目标的字段。执行后端 executorContext 与回报目标是两个不同概念。DSH 使用工具执行上下文的 agent.id；Codex 使用当前 ephemeral thread ID。旧客户端或直接 API 请求没有 origin 时不猜测目标，仍可正常执行和显示结果，但不会自动通知主会话。

只通知相同对局中的原主会话，不随前台窗口或当前选中的聊天迁移。消费前重新核对源任务、原会话、worldKey 和 hidden。主会话忙时排队，玩家输入优先；销毁任务或切局后跳过待发通知。通知执行期间发现任务销毁、范围变化或超时，会中断该通知。STOP 仍只停止主会话本轮回复，不撤销已经提交的游戏命令。

## 回复权限与证据

通知包含任务名称、问题原因、宿主账本生成的命令摘要和已有 Watch 边界摘要，不带 reportToken 或执行凭证。Worker 的自然语言回报作为资料处理，不能授权新动作。当前 persona.md 在每次投递时重读；要求按海军顾问口吻向玩家说明已做、未做、问题与需要补充的信息，不机械输出工单 ID。隐藏的 seapower-notice 输入不显示为玩家发言，主 Agent 的实际文字回复沿原通联链路显示。

仅靠提示词不足以保证不追加操作，因此增加宿主执行限制：

- DSH：使用原主 Agent 的 scoped tools.restrict({allow:[]}) 屏蔽继承工具，并安装 scoped guard 拒绝所有工具执行，包括不能被 mask 隐藏的局部工具。通过本机 Agent API 排入下一轮。消息采用已核对的 DSH UserMessage 不可变值契约，不依赖外部插件无法解析的私有 SDK 包导入。限制同步保持到对应 session 的 turn/end，随后解除，避免影响已经排队的正常玩家轮次。
- Codex：继续使用本机官方 app-server 和现有 ephemeral thread。通知轮次由 Chat 宿主在任何工具 handler 执行前拒绝全部工具调用；不增加 shell、外部服务或代理入口。完成后恢复正常主会话工具契约。

通知轮次不得重试、改派、创建任务、查询进度、注册 Watch 或下达游戏动作。自动服务只读取现有状态和结果，不因通知额外观察战术完成条件。

## 持久状态与故障

<MOD_ROOT>/comms/run/tickets/notifications 下存放：

| 文件 | 用途 |
|---|---|
| baseline.json | 首次升级时已有终态/问题任务，避免旧失败任务集中补发 |
| events/notice-*.json | 调度器生成的不可变通知资料；每个任务问题结果一个稳定 ID |
| delivery/notice-*.json | pending 之后的 sending、delivered、cancelled、skipped、failed、unknown 诊断状态 |

发送前先 flush/fsync 持久化 sending，之后才提交模型轮次。回复结束且有可见文本才记 delivered。重启发现 sending 时记 unknown，不自动重发；这是避免重复唤醒的 at-most-once 选择，不能承诺断电或未知投递后必达。delivered 表示主 Agent 已完成可见文本回复，不表示任务已完成或游戏效果得到验证。任务快照提供 notificationState；运行文件不是跨存档上下文，不打进发布包。

## 模块与部署

| 源文件 | 职责 / 部署位置 |
|---|---|
| ticket_notifications.py | 通知事件、来源校验、基线与投递记录；modules/tickets |
| ticket_scheduler.py | 完成原有调度周期后发布问题事件 |
| ticket_store.py | 持久保存宿主注入的来源，快照提供通知状态 |
| comms/dsh_ticket_notifications.mjs | DSH 消费、会话级工具限制、原生轮次事件与解除限制；harness/DSH |
| comms/dsh_ticket_plugin.mjs | 注入建单来源、启动通知服务；health.notificationProtocol=1 |
| comms/dsh_tailer.js | 隐藏通知输入，沿用原回复投影 |
| comms/codex_chat.py | Codex 来源绑定、空闲消费、仅回复工具闸门及 STOP/切局处理 |

没有修改 C# Bridge、底层传输、账本判定或游戏接口。模块清单已加入 ticket_notifications.py；build.ps1 -Deploy 会同步新 DSH helper 与本文到 harness/TICKET_NOTIFICATIONS.md。

升级前让活动任务结束，停止本项目调度器及相关聊天/尾读进程，部署后重新启动。DSH 外部插件包必须同步正在配置的入口模块以及同目录 dsh_ticket_notifications.mjs；不要仅更新部署目录后继续运行旧插件。插件包位于 <DSH_PROFILE>/node_modules/seapower-ticket-worker，实际入口可能由插件配置显式指定，须以该配置为准。在无活动会话时重载 Harness，确认 /health.notificationProtocol=1、revision=v8-notices。共享 Python 调度器保持单实例，沿用已有启动命令。

## 验证

回归覆盖历史基线、来源与执行后端分离、幂等、投递意图后重启不重发、工具拒绝与恢复、忙时排队、玩家输入优先、隐藏/销毁、错会话、错对局、STOP 和迟到输出。真实 Codex 隔离测试完成主会话建单并结束、独立 Worker 查询、needs_input 回报、原主会话文字转述，全程没有游戏写接口。

mcptest 实测 DSH、Codex 各创建一项只读编成任务：各读取一次 sp_session_fleet，以 needs_input 结束，原主会话给出目标航迹号澄清；通知轮次没有调用工具，没有新增游戏命令。已实际切换游戏通联面板核对两端回复显示，未展示隐藏注入输入。新通知的 STOP、切局、销毁和恢复边界在回归中验证，本轮未要求玩家再次切局或对通知点击真实 STOP。
