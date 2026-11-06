# 第三层 · Harness 调度层

[文档索引](README.md) · [第二层：转译](layer2_translation.md) · [第四层：人机交互](layer4_interaction.md)

第三层负责 Agent 会话和任务执行生命周期：接收玩家意图、绑定当前对局、创建工单、选择执行者、限制业务调用、保存证据、判定结果和投递异常通知。界面布局、按钮和玩家设置操作归第四层。本页替代原“玩家交互与工单运行时”的混合描述。

## 模块与边界

| 模块 | 当前职责 |
|---|---|
| comms/harness_link.js、comms/dsh_tailer.js | DSH 每局新建并绑定主会话；发送玩家输入、读取可见回复及处理 STOP |
| comms/codex_chat.py | Codex 每局 ephemeral app-server 主会话；原生动态工具、同轮绑定、STOP 和迟到输出抑制 |
| ticket_scheduler.py | 独立 Python 单写者调度器；消费文件请求、派发/取消、截止、真值采样、快照与通知 |
| ticket_store.py、ticket_predicates.py | 追加持久账本、请求去重、资源冲突、状态和确定性判据 |
| ticket_executor.py、ticket_executor_dsh.py、ticket_executor_codex.py | 共享执行契约及 DSH/Codex Worker Adapter |
| comms/dsh_ticket_plugin.mjs | DSH 原生工具、独立 one-shot Worker 持有者和主模式轮次限制 |
| api_catalog.py、api_tools.json | 从既有 MCP registry 提供接口发现、准确 schema 与使用注意事项 |
| ticket_api.py、ticket_main.py | 工单范围内调用现有第二层 handler；主模式宿主绑定来源，私有凭证不交给主模型 |
| ticket_presentation.py | 将宿主账本和结果生成只读视图，供第四层显示 |
| ticket_notifications.py、comms/dsh_ticket_notifications.mjs | 问题事件、原会话投递、去重、仅回复权限和生命周期 |

这些模块在物理部署中分布于 modules/tickets、modules/api、harness/DSH 和 harness/Codex；目录名称不等于独立架构层。第一层 src/TicketBridge.cs 只承接 /tickets 文件协议，不代替 Python 调度器。业务调用复用第二层实现；调度器可直接 GET 第一层真值，避免让 LLM 轮询等待。

## 主会话与通信

- DSH 通过 --game-world 每局新开游戏会话，首句隐藏注入 onceopen，每次输入重读 persona。sender 的 --out 是 <MOD_ROOT>/comms；tailer 的 --out 是 <MOD_ROOT>/comms/run，读取该目录 session.pointer 并写入 live.jsonl。
- Codex 在本局首条输入时新建临时 thread，同局复用，切局中断并关闭；不跟随桌面前台聊天、不恢复旧局聊天。输出 codex.chat.json 供游戏显示。
- 模型看到的隐藏上下文和玩家面板中的原文分开处理。STOP 校验会话、对局以及可用的轮次/时效；已提交的游戏命令不能靠停止生成撤销。
- 安装与启动见 [DSH](../comms/DSH.md)、[Codex](../comms/CODEX_DEPLOY.md) 和 [部署指南](DEPLOY.md)。不假设 Codex 原生子代理天然能脱离主会话后台运行；后台执行由当前 Adapter 和独立进程明确持有。

## 两种执行方式

| 模式 | 主 Agent | 执行者与调度器 |
|---|---|---|
| Worker（默认） | 判断授权边界、建单、简短确认并结束当前轮次 | 调度器经对应 Adapter 持有独立 Worker；主轮次结束后继续执行 |
| 仅主 Agent | 判断边界并建单，按回执 main 在同轮调用 sp_main_api_call/finish，报告后结束 | 不创建 Worker；调度器仍保存状态、处理截止和已有真值判据 |

执行方式在建单时固定，切换设置只影响新工单。重复 requestId 返回原结果，不能改变已受理任务的执行者。main 来源绑定 harness、sessionId、worldKey、turnId；其他轮次或会话不能接管执行。更多约束与实测见 [仅主 Agent 模式](main_agent_mode.md)。

## 受控调用和结果

执行者使用 sp_api_find/schema 查询实际签名。Worker 使用 sp_api_call/finish，主模式使用 sp_main_api_call/finish；两者进入同一账本和既有 MCP handler，不查源码、不复制底层接口。宿主检查资源范围、调用预算、HOLD、世界代际和活动状态。结果不明的写入禁止自动重发。

工单调用状态与任务结果分开：executed / execution_only / resultVerified=false 表示 API 有执行记录，不能自动推出起飞、命中或摧毁。fulfilled/partial 是执行者声明，blocked/needs_input 必须附具体原因。单舰 set_speed 继续 injected → monitoring，连续两次新鲜速度样本达标后 completed。Watch 工具调用和历史事件可记录为证据，通用 Watch 事件驱动自动结单尚未实现。

DESTROY 隐藏任务、拒绝后续接口并停止对应执行者。Worker 模式不能在原生子任务尚未退出时提前释放执行槽；main 模式结束未收尾则保留 uncertain，STOP 时按现有调用证据取消或记为 uncertain，不自动转派。工单落盘用于恢复和防重发，不代表跨存档恢复主聊天。

## 异常通知与第四层的交接

Worker 的 blocked、needs_input、failed、uncertain、timed_out 可生成一次问题事件，绑定原主会话；主会话忙时排队，玩家输入优先。通知只允许文字说明或澄清，宿主拒绝全部工具，不因问题通知追加操作。main 已结构化报告的问题不再重复唤醒原主会话。详见 [异常通知闭环](ticket_notifications.md)。

第三层向第四层提供任务快照、已提交命令、未确认写入、读取记录、Watch 边界和通知结果。第四层只渲染证据及提交用户控制意图，不自行判定 completed。persona 约束表达，不授予新的游戏操作权限。

## 当前验证

2026-11-06 两端主模式实机只读任务、游戏 STOP、经原工单协议的销毁通过；关闭开关后 DSH Worker 和异常通知闭环回归通过。战术写入、切局、超时和真值判据各自的历史/隔离/实机范围分别记录在专题文档，不把文档或离线通过当成全量实机完成。

[工单运行时与 TR 历史](ticket_runtime_plan.md) 保留逐阶段调查记录；与其早期 TR1 单后端描述冲突时，以本页和对应当前专题为准。
