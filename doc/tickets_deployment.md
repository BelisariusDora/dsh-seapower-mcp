# 当前共享工单运行契约

首次部署以 `<MOD_ROOT>/README.md` 为准。本文适用于 DSH/Codex；不再把早期 TR1 的单后端和旧 revision 当成当前安装要求。历史过程只在源码 doc/ticket_runtime_plan.md 保留。

## 执行路径

| 模式 | 主 Agent | 执行者 |
|---|---|---|
| 默认 Worker | 判断意图/授权、创建工单、确认并结束 | scheduler 通过 Adapter 持有独立 DSH one-shot 或 Codex app-server Worker |
| MainAgentOnly | 判断边界、创建工单、在同轮执行并报告 | 原主会话，不创建 Worker；scheduler 仍保存状态和执行判据 |

执行方式在建单时固定；切换设置只影响新任务。Worker 和 main 使用同一授权调用管线，API 回执和账本为执行证据。部署 Agent 可以阅读工具实现，业务 Worker 使用 sp_api_find/schema/call/finish，不查源码、开 shell 或再次委派。

## 入口与数据

- Bridge 的 /tickets 通过 commands/responses/view 文件协议与 Python 单写者通信，避免阻塞 Unity 主线程读取。
- ticket_store.py 保留请求去重和状态；ticket_api.py 检查资源范围、预算、HOLD、世界及活动状态，未知写入不重发。
- 通用 command.kind=mcp_task 指定 tools/resources/max_calls，使用 execution_recorded；set_speed 使用匹配 speed_reached，由 scheduler 两次新鲜真值达标后 completed。
- executed / execution_only 仅表示接口已执行，不是起飞、命中或摧毁确认；blocked/needs_input 必须有具体 reason，未知写入优先 uncertain。
- Worker 私有 report_token 不交给主模型。main 来源由宿主绑定 harness/session/world/turn，不允许跨会话接管。

## 停止与通知

DESTROY 隐藏并取消指定任务，拒绝后续接口，停止对应 Worker 或主执行轮次。STOP 停止所选目标的当前主轮次；默认 Worker 已派出的任务需要指定取消。已提交的游戏命令不能撤回。

Worker 未实际退出前不释放执行槽；main 中途结束未报告时不能自动改派或重试。超时按墙钟，游戏暂停也不停止计时。切局使旧任务失效，旧回报不进入新局。

问题结果交给原主会话文字说明，玩家输入优先，通知轮次禁止一切工具。main 已报告的问题不重复唤醒。Watch 事件可以记录和展示，但通用 Watch 自动结单未接通。

## 安装和健康

DSH/Codex 的完整命令、插件文件安装、Cordis 配置和 Bearer health 请求均在根 README。当前插件 revision 从部署脚本导出值与实际 health 比较，协议为 ticketProtocol=2、apiTicketProtocol=1、taskResultProtocol=1、notificationProtocol=1；不能只凭文件存在声称加载成功。

同一 comms/run/tickets 只能有一个 scheduler。既有 runtime.lock 只有在核实归属进程退出后才可处理；不得删日志、退游标、换 requestId 或 call_id 重放结果不明的游戏写入。启动成功后做只读闭环，不下达未经授权的战术命令。
