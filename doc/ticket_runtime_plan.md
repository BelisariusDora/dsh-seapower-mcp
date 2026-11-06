# Python 工单调度器 · TR 阶段记录

> 本文属于第三层，保留 TR1 起的历史设计与验收过程。当前四层职责见 [文档索引](README.md)，当前执行流程见 [Harness 调度层](layer3_workflow.md)，仅主 Agent 已实现，见 [主模式](main_agent_mode.md)。下文“唯一 DSH 后端”“主 Agent 建单后必须结束”等 TR1 表述仅适用于当时或当前 Worker 模式，不代表主模式限制。

> 2026-11-06 当前增量：Codex 已新增每局独立的临时 app-server 主会话，双向游戏收发、STOP 与切局隔离；旧桌面转发为 legacy。调度器按每张工单的 executorContext 路由 DSH/Codex，`--executor` 仅决定默认来源，共享单个调度器与执行槽。下文 TR4/TR5 的 WIP、切换前必须清空工单等描述记录的是当时状态，已由此增量取代。真实模型隔离验证通过，游戏内验证另记。见 [Codex 接入](../comms/CODEX_DEPLOY.md)。


TR1 已实现单舰航速闭环：`submit → persisted queued → dispatch → inject → injected → monitoring → completed`。主 Agent 只负责意图、授权边界、建单和一句受理回复；Worker 只注入命令；Python 调度器独立读取游戏真值判断完成。此开发版本尚未并入 Alpha 0.2 发布包。

## 模块和依赖

| 模块 | 职责 |
|---|---|
| `mcp_server.py` | 保留现有 MCP 工具入口；工单字段增加结构化 command、predicate、receipt。 |
| `src/TicketBridge.cs`、`src/Api.cs` | 沿用 `/tickets` 和 commands/responses/view 文件协议；`/tickets` 继续在 HTTP 工作线程等待，避免阻塞 Unity 状态读取。 |
| `ticket_scheduler.py` | 独立 Python 进程、单写者锁、文件收发、派工、取消、截止和低频监视。 |
| `ticket_store.py` | JSONL 追加并 fsync；去重、状态和持久证据的权威来源。 |
| `ticket_predicates.py` | 单舰航速契约、目标检查和确定性速度判断。 |
| `ticket_executor_dsh.py` | 独占 DSH 会话指针、插件鉴权、Worker 提示词和生命周期调用。 |
| `comms/dsh_ticket_plugin.mjs` | DSH 原生 one-shot Worker 持有者；原生工具注册、启动、状态和停止。 |
| `comms/prompts/onceopen.md` | 首条游戏消息隐藏注入主 Agent 职责与 TR1 能力范围。 |

Python 仅用标准库，调度器复用现有 MCP 的 Bridge opener，不替换底层通信。DSH Adapter 是当前唯一执行实现；不假设 Codex 原生子代理能脱离主聊天运行。旧 Node `ticket_runtime.js` / `ticket_store.js` 保留供回退检查，不应与 Python 共用目录同时启动。

## 部署和启动

源码构建部署按 `DEPLOY.md`。Python 实现已按模块部署，根目录保留兼容入口，插件入口位于 `<MOD_ROOT>/harness/DSH/dsh_ticket_plugin.mjs`。本次 Python 与插件更新不要求修改或重编 C#。

在 `<MOD_ROOT>` 启动独立进程：

```powershell
python .\ticket_scheduler.py --out .\comms
```

Bridge 默认 `19080`，DSH 原生插件默认 `19389`，可用 `--bridge-port` / `--plugin-port` 指定。聊天收发器仍按 `harness/DSH/README.md` 启动。调度器先启动并生成 `comms/run/tickets/auth.token`，插件读取同一文件；不得公开此 token 或把整个运行目录打包发布。

将插件安装为 DSH 的本地 ESM 模块并在 Cordis 配置启用，例如：

```yaml
- insert:
    - id: seapower-ticket-worker
      name: seapower-ticket-worker
      config:
        runtimeDir: '<MOD_ROOT>/comms/run'
        port: 19389
```

模块文件必须导出 `name`、`inject` 和 `apply`；包入口指向本版本文件。更新后重启实际持有 DSH RPC 端口的宿主，在没有活动任务时操作。不能只凭文件替换声称插件已加载：带本地 token 请求 `/health`，要求 `ticketProtocol: 2`，当前 revision 为 `20261105-native-one-shot-v5-tr1`。旧协议不会启动 Worker。

同一 `tickets` 目录仅运行一个运行时。Python 使用现有 `runtime.lock` 拒绝存活的 Node/Python 写者；只有确认旧进程已退出才可切换。v1 历史可读但标记 legacy、resultVerified=false；可能执行过的旧任务不会自动重放。新格式日志不支持直接交给旧 Node 运行时，回退须先停止 Python、检查未决 Worker，使用独立或已备份的旧运行目录。

## 工具契约

`sp_ticket_create` 保留 requestId、title、task、completion、timeoutSeconds，并为 TR1 要求以下结构。目标必须是本局己方、存活、可读速度上限的水面舰艇，目标速度在允许范围内。

```json
{
  "command": {"kind": "set_speed", "unit_id": 56, "knots": 5},
  "predicate": {"kind": "speed_reached", "unit_id": 56, "target_knots": 5, "tolerance_knots": 0.5}
}
```

示例 ID 不可跨局复用。completion 仅是显示文字，不能代替 predicate。task 明确玩家授权与限制。建单先持久化 queued，再派工；主 Agent 收到受理回执即结束本轮。未绑定 DSH 时可排队，超时前等待 Adapter 就绪。同一单位已有活动工单时明确拒绝冲突。

Worker 使用 `sp_ticket_report`，带新 UUID requestId、id、私有 reportToken、status、detail。`injected` 必须带匹配命令的原始成功 Bridge receipt，包括 ok、cmd、data；原生 `sp_speed` 返回完整 envelope。旧拼写 completed 只按 injected 处理，也不能跳过回执和观测。写回执不确定时报告 uncertain，禁止自动重试游戏写操作。查询仍用 `sp_ticket_list`，销毁用 `sp_ticket_destroy` 或游戏内 DESTROY。

## 状态和边界

`queued → starting → running → monitoring → completed`。Worker 报告 injected 后即停止；调度器每约 1.5 秒读取同一世界的实际航速，连续两次新读取满足容差才完成，并持久化两条 sampleId、世界、时间、速度和谓词证据。数据缺失或短暂断连不能满足条件。失败、墙钟到期、玩家销毁和已确认切局分别终止为 failed、timed_out、cancelled；不确定的注入保留 uncertain，需人工审查或取消，绝不重新派工重放。

期限默认 900 秒，允许 10–3600 秒；暂停游戏也继续计时。DESTROY 先隐藏，异步停止原生任务，停止失败会重试。取消 Worker 不会撤销已经注入的游戏命令。进程在 starting 阶段崩溃后恢复为 uncertain；monitoring 恢复时重新取得两次证据。

DSH 使用 `ctx.subagents.start` 的 one-shot 运行，插件持有 result/dispose，不依赖主 Agent 轮次持续活跃，也不自动发消息唤醒主 Agent。Worker 的宿主结束状态 cancelled 表示报告后被调度器清理，须结合工单状态区分业务完成与生成停止。

## 验证记录与后续范围

2026-11-05 实际游戏测试：通过部署版 MCP stdio 建单，己方 56 号舰由 0 节设为 5 节。queued 在派工前持久化；Worker 回报 injected 后先停止；工单当时仍 monitoring。随后两次真实速度观测满足 5±0.5 节才 completed，resultVerified=true。工单 ID 为 `ticket-cc5ca5de-365a-47b9-be1b-88b259d8d145`。这证明单舰速度最小闭环，不代表复杂战斗任务已经支持。

回归在源码根目录执行：

```powershell
python -m unittest discover -s test -p test_ticket_scheduler.py
node test/dsh_ticket_tools.mjs
node test/dsh_ticket_plugin.mjs
```

40 项 Python 测试覆盖真值判定、Worker 先退出、幂等冲突、错误回报、uncertain 不重放、超时、取消、切局、断连、崩溃恢复、HOLD、旧协议拒绝、旧日志和单写者锁。原生工具测试覆盖原始写回执及 report 字段；插件模拟测试覆盖 one-shot 生命周期。

TR2 已完成真实主 Agent 建单后结束、连续玩家输入、按需查询、游戏内 DESTROY、活动 Worker 取消、暂停时墙钟超时、真实切局和迟到回报拒绝验证。舰载机起飞、Watch 事件接入、增量查询、复杂命令和全面权限收敛尚未实现。可选 Codex Executor 的后续进展见 TR4。当前 Worker 的动作范围主要由任务提示词约束，现有其他全局工具并未全部隔离，不应将其描述为通用的硬权限执行沙箱。

## TR2 实机生命周期验收

2026-11-05，沿用 TR1 运行时完成以下实测。本阶段新增生命周期回归，未修改游戏业务逻辑、C# Bridge 或 DSH 收发协议。

| 用例 | 运行证据 |
|---|---|
| 主 Agent 建单后结束 | 经 `/comms/send` → 真实游戏队列 → DSH 主会话建单，受理回执后约 2.7 秒结束，未等待速度达标。 |
| 接续聊天 | 工单仍 monitoring 时下一句普通聊天约 1.4 秒结束，无工具调用、无新工单。 |
| 按需查询 | 第三轮仅调用一次 sp_ticket_list，回答后结束，无轮询。父会话总共三轮，没有后台工单自动唤醒。 |
| UI 撤销 | 游戏 DESTROY 请求被消费，工单 cancelled、hidden=true，从面板消失；历史保留。 |
| 活动 Worker 取消 | running 阶段取消，原生子代理 stopped/cancelled，workerExited=true、cancelPending=false；子代理日志无游戏工具调用，receipt=null。 |
| 暂停时超时 | 游戏模拟时间固定，30 秒墙钟期限工单约在 30.158 秒变为 timed_out、resultVerified=false。 |
| 真实切局 | 用户退出主菜单并重进测试局，世界世代由 :2 变为 :5；旧工单在截止前 cancelled，原因 Game world changed，Worker 已退出。 |
| 旧回报拒绝 | 显式旧 worldKey 返回 HTTP 409 world_changed；只带旧工单 id、绑定到新世界也明确拒绝，不污染新局。 |

切局实测工单为 `ticket-efd6094a-4b4f-4a46-bbe3-c5fb084d31b9`。上一次中断测试产生的 world-change 工单实际按墙钟超时，保留为中断记录，不作为切局通过证据。

主 Agent 第一轮从输入到结束约 25 秒，包含身份核对和三次 shell 只读调用；“建单后结束”不等于所有请求都能在几秒内受理。当前 DSH 原生工具尚未提供 sp_unit / sp_session_fleet 的快捷只读入口，主 Agent 可能退回 shell 查询，这是后续缩短建单前开销的具体改进点。TR2 不保证模型永远严格只回复一句，实际受理回复仍包含说明表格。

相关回归增加：取消服务断连后重试停止、取消后的迟到报告拒绝、监视不阻塞另一个建单、按需快照无写副作用、冻结观测时墙钟到期与跨世界报告拒绝。TR3 多工单、冲突和 Adapter 边界验收见下一节。

## TR3 多工单与 Harness Adapter

2026-11-05 完成 DSH 双工单实测，并对 Codex 官方 app-server 做独立进程探测。游戏侧仍只支持单舰航速这一种工单；多工单不代表复杂动作已实现。

执行槽现在以原生 Worker 退出确认作为释放条件。仅进入 monitoring、隐藏工单或请求取消不足以释放槽；有 workerHandle 但尚未 workerExited、或 cancelPending 时不派发下一 Worker。uncertain 即使子代理已经退出，也继续占槽，直到人工取消。旧日志中的子代理句柄先经 Adapter 对账并确认停止，不能直接忽略。正在监视且 Worker 已退出的工单不占执行槽。

此规则修复两个用测试复现的问题：取消服务短暂离线时隐藏工单可能提前放行新 Worker；注入报告已受理但停止尚未确认时可能提前放行新 Worker。回归先失败、修复后通过。

Adapter 的最小约定是 context(world)、start(ticket)、status(ticket)、stop(ticket)，上下文和句柄由 Adapter 自己解释。stop 返回 true 必须代表原生任务已退出。Harness 无关的 ExecutorUnavailable 放在 ticket_executor.py，表示尚未尝试原生 start，可安全保持 queued；其他启动异常按结果未知处理，不重放。调度器核心不读取 DSH 私有格式；仅 main 组合入口选择并实例化 DSH 实现。未引入基类、插件框架或通用依赖注入容器。

实测两舰工单：己方 56 号目标 5 节，120 号目标 8 节。第二 Worker 在第一 Worker 原生结束 137 ms 后创建。暂停阶段两张工单同时 monitoring、workerExited=true、resultVerified=false；恢复 1x 后各自用两次新速度采样完成。第一张观测 4.569/4.760 节，第二张 7.609/7.769 节，互不串用证据。注入期间和监视期间向同一单位提交冲突指令均明确拒绝，没有创建覆盖任务。测试恢复原暂停和时间倍率设置。

21 项 Python 回归通过，另有 DSH 原生工具和 one-shot 生命周期模拟测试。新增用例覆盖双 Worker 串行、并存监视独立判定、停止未确认时占槽、同单位冲突、旧句柄对账和 uncertain 占槽。

### Codex 接口尝试与当前边界

依据 [OpenAI 官方 app-server 文档](https://learn.chatgpt.com/docs/app-server)，核对本机 codex-cli 0.166.0-alpha.6，并生成其实际 JSON Schema。独立 stdio app-server 实测 initialize、ephemeral thread/start、turn/start、turn/interrupt；无工具的最小回复成功，取消轮次返回 interrupted，独占服务进程随后退出。未连接或操作现有桌面聊天，也不依赖主聊天子代理在后台存活。

可手动复跑以下探测，消耗少量模型额度；不属于默认单元测试，不调用游戏或自动批准请求：

```powershell
python test/codex_app_server_probe.py --out "<EVIDENCE_DIR>/codex-probe.json" --cwd "<PROBE_WORKSPACE>"
```

该探测只证明本机独立会话、回复和中断接口可用。Codex 的游戏工具注册、工单 report 关联、进程恢复与执行 Adapter 尚未实现，也未做 Codex 游戏命令验证。因此 UI 的 Codex (WIP) 标记保留，生产执行仍用 DSH。下一步若接入，使用调度器持有的独立 app-server 或执行进程及受限工具，不通过桌面 UI 自动发送消息，不假设原生子代理天然脱离主会话。

## TR4 可选 Codex 执行器

2026-11-06，在 TR3 接口探测之后实现 ticket_executor_codex.py，并通过真实游戏单舰航速和取消测试。共享调度器、C# Bridge、工单入口、原始回执要求和游戏真值判据不变。默认执行器仍为 DSH，可显式选择：

```powershell
python .\ticket_scheduler.py --out .\comms --executor codex
```

省略 --executor 或使用 --executor dsh 即为原 DSH 路径。一个运行目录只能启动一个调度器；切换前完成或取消所有活跃工单，确认 Worker 退出，再停止旧调度器。恢复旧后端的活跃任务时必须使用原后端；启动检查拒绝跨后端接管活跃工单。测试完成后本机已恢复 DSH。此处的执行后端与游戏面板 Target 聊天转发目标是两个概念：现有 DSH 主 Agent 可以继续建单，由所选后端执行；Codex 桌面聊天转发仍为 WIP。

依赖本机 PATH 中的 codex（实测 0.166.0-alpha.6）、现有登录状态及支持动态工具的 app-server；Python 标准库无需新框架。每个工单启动独立 stdio app-server 与 ephemeral 会话，主桌面聊天不参与；进程由 Adapter 持有，报告后中断生成并关闭服务，只有确认进程退出才释放执行槽。临时会话不作为跨局聊天记录保存。codex-workers.json 只记录生命周期、进程/临时会话标识和写入尝试，属于运行数据，不打包发布。

动态工具只注册 sp_comms_state、sp_speed，命令参数由宿主与工单精确比对。宿主写入前核对本局世界、HOLD、工单仍处于 starting/running、期限和取消信号，并在网络发送前持久化 writeAttempted。同一 Worker 最多尝试一次速度写入；写回执未知记 uncertain，不重发。命令成功后由 Worker 宿主直接回报匹配的原始 receipt，使用同一 UUID 有界重试回报，不让模型自行编造注入证据。模型退出或进程完成不等于工单完成，依然由共享调度器读两次游戏速度判定。

独立 app-server 关闭 shell_tool、unified_exec、multi_agent、apps 功能和 web_search，并对工作会话禁用配置中的 MCP 服务；未知客户端工具/审批请求被拒绝。只读 sandbox 和动态命令校验为本阶段边界，不将其描述为面向所有 Codex 版本与插件的通用硬权限沙箱。不会修改用户全局配置或自动批准请求。

取消设置停止信号，Worker 请求 turn/interrupt 并关闭独占进程；取消不能撤销已发出的游戏命令。写入之前和取消之间仍存在正常的在途请求边界，不能声称取消能追溯撤销。若调度器崩溃，可能已注入的任务不重放；恢复时对失去所有权的存活 PID 保留 unknown/uncertain，不仅凭 PID 杀进程或重新派工。确认原进程已退出后才能释放资源并人工处置不确定工单。这一路径通过模拟恢复测试，未通过强杀实机进程做全链路验收。

实机工单 ticket-ef599f31-d382-4f0e-bb78-b0c98cff6891：56 号舰目标 5 节。暂停期间 Codex 命令已注入且进程退出，工单仍 monitoring、resultVerified=false；恢复 1x 后以 4.678/4.842 节两次新采样 completed。另一个 running Worker 被取消，exited=true、未记录 writeAttempted、receipt=null，确认没有注入。测试恢复原时间状态，航速命令保留在测试局。

28 项默认回归通过，其中 7 项 Codex 测试覆盖参数授权、一次写入、丢失回执不重发、世界/HOLD/隐藏/期限、未知工具拒绝、取消退出确认和恢复不重放。另用真实 Codex 对临时模拟 Bridge 测试动态工具、原始回执和自动报告/退出。可手动复跑，消耗模型额度但不操作游戏：

```powershell
python test/codex_ticket_protocol_probe.py --out "<EVIDENCE_DIR>/codex-ticket-probe.json"
```

本阶段没有新增 C# 修改、重编 DLL、启用 Codex 桌面聊天转发或实现复杂战斗工单。用户应区分可用的 Codex 执行后端与仍为 WIP 的面板聊天投递。

## TR5 通用业务接口工单与快速目录

2026-11-06 按“现有接口机制接入工单、子代理无需查资料”的目标实现。原有 79 个 MCP 入口中，72 个业务入口复用原 handler 接入通用工单；7 个工单/通信管理入口保留主控调用，禁止 Worker 递归建单、销毁工单或向聊天投递消息。额外注册 4 个快速入口，总计 83 个工具。sp_raw 仍可访问业务 Bridge，但禁止绕过 /tickets、/comms、/ui 控制边界。

### 子代理的四个入口

| 工具 | 作用 |
|---|---|
| sp_api_find | 按短关键词或 domain 查少量接口签名、前置条件、简短工作流，不执行游戏操作。 |
| sp_api_schema | 只返回所选接口的真实 MCP schema 与已知 Bridge 注意事项。 |
| sp_api_call | 以工单私有能力调用已有 handler；验证世界、生命周期、HOLD、工具范围、资源和调用预算；按 call_id 去重并落盘回执。 |
| sp_api_finish | 宿主核对全部真实调用记录后结束工单；模型自述不能代替证据。 |

目录由现有 mcp_server.TOOLS_BY_NAME 生成，不复制一份接口实现。工作流按 plot、deck、units、watch、time、inspect、comms 分组；可先找少量接口再取准确签名。修正了起飞工具 schema 中 loadout 可省略的旧描述，Bridge 实际要求明确挂载。

DSH 通用子代理实际屏蔽 185 个无关全局工具，只保留四个接口助手和图像读取，不让它回退到 shell、源码或文档搜索。主 Agent 仍只做 RP、授权和建单，受理后结束。Codex 注册同一份 api_tools.json 动态 schema，私有 ticket_id/report_token 由宿主绑定，不发送给模型。

### 工单结构与状态

```json
{
  "requestId": "<NEW_UUID>",
  "title": "标注方案",
  "task": "玩家授权的目标、对象、范围和限制",
  "completion": "执行授权接口并记录回执，不声称异步战果",
  "command": {
    "kind": "mcp_task",
    "tools": ["*"],
    "resources": ["world"],
    "max_calls": 32
  },
  "predicate": {"kind": "execution_recorded"},
  "timeoutSeconds": 900
}
```

requestId 必须是新的标准 UUID，不能把标题拼进标识。tools 可以缩到明确的业务接口列表；`["*"]` 只包含业务入口。resources 默认 world，与所有活动工单冲突；仅绘图可声明 plot，指定单位可声明 unit:<本局ID>，时间操作为 time。写入宿主检查声明的资源，省略目标而使用当前选中单位的导航写入必须持有 world。对资源不确定时使用 world，不用随意的标签绕过冲突。max_calls 为 1–64，只统计业务调用，不统计目录/schema 查询。

通用任务状态为 queued → starting → running → executed / failed / uncertain。executed 表示所有记录调用有成功回执，verification=execution_only、resultVerified=false，不能冒充游戏异步结果完成。现有 set_speed/speed_reached 专用工单不变，仍通过两次新速度观测才能 completed。当前没有为所有异步业务建立通用真值判据；完成“攻击接口调用”不能等同于摧毁，登记 Watch 也不等于工单运行时已实现持续事件消费。

调用前持久化 intent；重复 call_id 和相同参数返回原回执，不重复调用。参数变更复用 call_id 会被拒绝。intent/uncertain 不自动重放，存在未知调用时禁止继续执行；finish 将其报告为 uncertain。显式失败回执保留，finish 报 failed，不隐藏中途失败。schema/权限检查在调用前失败时没有业务写入，可修正参数。取消及切局后拒绝能力调用；已经提交的游戏命令不回滚。

`comms/run/tickets/api_calls/<ticket_id>` 保存调用参数、原始回执、呈现结果与必要的图像文件。回执独立于模型摘要，私有 token 不写入调用日志。截图从 base64 保存为本地 PNG，返回 artifact 路径与哈希，Worker 需要视觉判断时可直接读取该图，无需查项目文件。通用接口复用原 MCP 的 present_tool_result，因此继续沿用既有字段投影、轨迹运动/MLP 相关结果处理；不会为呈现再次执行接口。

### 部署与验证

Python 3.11+、Node.js 24+；新增 api_catalog.py、ticket_api.py、api_tools.json 已纳入 build.ps1。保持既有部署步骤；本次不要求修改 C# 或重编 DLL。DSH 插件使用新版 dsh_ticket_plugin.mjs，实测 revision=20261106-native-one-shot-v6-api，/health 必须包含 ticketProtocol=2、apiTicketProtocol=1。文件替换不等于实际加载；应在没有活动 DSH 任务时重载其真实宿主。可安装为 ESM 包 seapower-ticket-worker/index-v6.mjs，配置示例：

```yaml
- insert:
    - id: seapower-ticket-worker
      name: seapower-ticket-worker/index-v6.mjs
      config:
        runtimeDir: '<MOD_ROOT>/comms/run'
        pythonPath: '<PYTHON_EXE>'
        bridgePort: 19080
        port: 19389
```

pythonPath 必须为可在后台运行的 Python 可执行文件；也可通过 SPMCP_PYTHON 指定。插件只注册精简助手，每次调用启动已有 Python MCP 入口，不需要新服务框架。Bridge 端口随配置传入 Python。首条隐藏提示已更新到 onceopen.md，新局采用新规则；旧局已初始化的主会话不会强行重写历史。

真实 DSH 会话验收：主 Agent 建一个通用工单后结束；子代理执行 sp_state、sp_session_fleet、sp_plot_marker、sp_plot_drawings 四次调用，在己方舰当前坐标新增一个 TR5-API-TEST 标记并读回。Worker 约 12.7 秒结束，未调用源码、shell 或文档工具，原生记录屏蔽 185 个无关工具，父会话没有被唤醒。主 Agent 首次 UUID 非法被拒绝，修正后仅持久化一个工单，没有重复派工。

真实 Codex 验收：单个通用工单调用 sp_session_fleet、sp_deck、sp_airborne、sp_watch_create、sp_watch_get、sp_screenshot 六个业务接口。登记一次 Velocity > 1000、ttl_s=30 的短期 Watch，不等待；截图生成有效 PNG。未起飞、未下单位/武器/时间命令，Worker 退出后工单 executed、resultVerified=false。测试后恢复默认 DSH；Codex 桌面聊天转发仍为 WIP。

40 项 Python 回归通过，另有 DSH 工具/one-shot 回归与两种真实 Harness 的目录/通用执行验证。代表性接口完成运行验证，不等于逐一验证了全部 72 个业务入口在所有游戏对象和状态下的语义。当前仍不声称有通用硬权限沙箱或自动战果判定。


当前文件布局见 [模块化部署目录](deployment_layout.md)：工单核心在 modules/tickets，业务 API 在 modules/api，MCP 在 modules/mcp，两种执行 Adapter 在对应 harness 目录。原根路径启动命令保持兼容。


游戏 mcptest 实测（2026-11-06）：Codex 出站消息得到唯一测试串回复，Bridge 的 codex 通道与游戏面板显示一致；主会话建单后结束，独立 Codex Worker 只读调用一次 sp_session_fleet，记录 executed、resultVerified=false 并退出。实际点击游戏 STOP 按钮，从流式回复恢复 IDLE，该轮部分/迟到回复不再显示。未下达单位、武器、起飞、绘图或时间命令。跨 world 的新会话与旧进程关闭已在真实 app-server 隔离测试验证，游戏内切局尚未另做人工验收。


## 工单结果与展开详情增量

第一步新增结构化任务结果、宿主命令证据及 Watch 边界详情。旧 executed 不再显示为任务完成；受阻/需澄清可明确结束。详见 [工单结果与详情](ticket_results_ui.md)。本阶段不自动唤醒主 Agent 或执行额外操作。
