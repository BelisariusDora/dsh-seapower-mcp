# Codex 游戏会话

> Alpha 1.0：默认 Worker 模式沿用下述建单后结束/独立执行流程。Settings 开启“仅使用主 Agent 操作”后，仍建单，由创建工单的主会话同轮调用 sp_main_api_call/finish，不派 Worker；回执 executionMode 决定执行方式。STOP 同时收束该主轮次的活动任务，未知写入不重发。详见源码 doc/main_agent_mode.md，安装包 harness/MAIN_AGENT_MODE.md。早期 TR 状态属于历史记录。

Codex 与 DSH 一样使用每局独立的游戏会话。第一条玩家消息创建临时 app-server thread；同局后续消息复用它，退出或切换对局会中断并关闭旧会话，旧回复不进入新局。不绑定、不读取当前桌面聊天，也不恢复上次进程的聊天记录。

需要 Python 3.11+、PATH 中可用且已登录的 Codex CLI（`codex app-server --stdio`）。沿用本机 Codex 的账号和默认模型配置。Node.js 仅用于兼容启动入口，直接运行 Python 时不需要 Node.js。

在部署目录启动独立聊天转发器：

```powershell
& "<PYTHON_EXE>" "<MOD_ROOT>/harness/Codex/codex_chat.py" --out "<MOD_ROOT>/comms" --game-world
```

或使用兼容 Node 入口：

```powershell
node "<MOD_ROOT>/harness/Codex/codex_link.js" --python "<PYTHON_EXE>" --out "<MOD_ROOT>/comms" --game-world
```

若 CLI 不在 PATH，给 Python 入口增加 `--codex "<CODEX_EXE>"`。一个 comms 目录只启动一个 Codex 聊天转发器。DSH 收发脚本可同时运行，各消费自己的出站队列。

游戏面板 Target 选 Codex 后直接收发。`persona.md` 每条消息重读；首句另外隐藏注入本目录 `prompts/onceopen.md`，游戏只显示玩家原文。主会话只聊天、澄清和建单，不直接执行业务命令。

另需启动一个共享工单调度器：

```powershell
& "<PYTHON_EXE>" "<MOD_ROOT>/ticket_scheduler.py" --out "<MOD_ROOT>/comms" --executor dsh
```

这里 `--executor` 只决定没有显式选择后端的请求的默认值。游戏 Codex 主会话创建的任务由宿主强制绑定 Codex Worker；DSH 沿用默认 DSH。两边共用一个调度器、资源锁和串行执行槽，不要各启动一个调度器。已创建任务独立于主会话继续运行，玩家用 DESTROY 隐藏并取消任务；STOP 仅中断正在回复的 Codex 主会话，不撤销已经提交的游戏命令或已受理任务。

STOP 请求含会话、轮次、对局标识与 5 秒时效；中断后不显示该轮的部分回复和迟到回复。主会话只提供建单/查询等小型动态工具，不继承其他 MCP、shell、原生多 Agent 或外部审批能力。执行 Worker 继续使用现有受控 API 工具。

运行数据均在 `<MOD_ROOT>/comms`：`codex.outbox.jsonl` 为出站队列，`run/codex.chat.json` 为游戏读取的当前快照，`run/codex.cursor.json` 为已取消息游标，`run/codex.stop.json` 为一次中断请求，`run/codex.chat.lock` 防止重复启动。新安装从已有队列末尾开始；未知投递结果不自动重发，避免重复下令。新局/重启不恢复旧聊天；这些运行文件不是跨存档记忆，不打包发布。

旧桌面 UI 投递代码保留在 `legacy/`，仅作历史参考；`codex.target.json`、桌面标题、前台识别及 `--thread`/`--chat-title` 不参与新入口。

验证：15 项聊天/路由回归及真实 app-server 的同局复用、建单绑定、中断、换局隔离测试通过，原有 40 项工单回归通过。游戏内验收见文末记录。


游戏 mcptest 实测（2026-11-06）：Codex 出站消息得到唯一测试串回复，Bridge 的 codex 通道与游戏面板显示一致；主会话建单后结束，独立 Codex Worker 只读调用一次 sp_session_fleet，记录 executed、resultVerified=false 并退出。实际点击游戏 STOP 按钮，从流式回复恢复 IDLE，该轮部分/迟到回复不再显示。未下达单位、武器、起飞、绘图或时间命令。跨 world 的新会话与旧进程关闭已在真实 app-server 隔离测试验证，游戏内切局尚未另做人工验收。
