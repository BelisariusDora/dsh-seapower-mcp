# DeepSeek Harness 接入

> Alpha 1.0：默认 Worker 模式沿用下述建单后结束/独立执行流程。Settings 开启“仅使用主 Agent 操作”后，仍建单，由创建工单的主会话同轮调用 sp_main_api_call/finish，不派 Worker；回执 executionMode 决定执行方式。STOP 同时收束该主轮次的活动任务，未知写入不重发。详见源码 doc/main_agent_mode.md，安装包 harness/MAIN_AGENT_MODE.md。早期 TR 状态属于历史记录。

需要 Node.js 24+、Python 3，以及已启动的本地 DSH。RPC 默认 `127.0.0.1:19387`；发送器使用本机用户的 DSH 配置，可用 `--port`、`--bridge-port` 或 `DSH_HOME` 调整。

在 `<MOD_ROOT>/harness/DSH` 分别启动收发器：

```powershell
node .\harness_link.js --out ..\..\comms --cwd "<DSH_WORKSPACE>" --game-world
node .\dsh_tailer.js --out ..\..\comms\run --game-world
```

`<DSH_WORKSPACE>` 替换为实际工作区绝对路径。进入对局后选择 Target: DSH。每局新建聊天，切局请求停止旧游戏聊天；首条玩家消息隐藏附带 `prompts/onceopen.md`，面板显示原始消息。用 `--probe` 做只读诊断，故障查看 `comms/run/link.log` 与 `link.status.json`。同一队列各运行一个收发进程。

## TR1 工单

开发版本采用独立 Python 调度器。安装本版本原生插件后，在 `<MOD_ROOT>` 启动：

```powershell
python .\ticket_scheduler.py --out .\comms
```

也可在 `harness/DSH` 执行 `python ..\..\ticket_scheduler.py --out ..\..\comms`。不要同时启动旧 Node `ticket_runtime.js`。插件 `/health` 必须显示 `ticketProtocol: 2`；仅替换文件而没有重载宿主不能生效。

早期 TR1 仅支持单舰航速；当前通用接口工单见下文。主 Agent 建单受理后结束；原生 one-shot Worker 注入一次命令并报告 injected 后退出；调度器读取实际速度判断 completed。期限按墙钟计时，默认 15 分钟。DESTROY 隐藏工单并请求停止 Worker，已发出的游戏命令继续执行。

完整插件配置、迁移限制和验证范围见源码 `doc/ticket_runtime_plan.md`；部署包内同目录的 `TICKETS.md` 提供相同说明。当前开发功能尚未进入 Alpha 0.2 发布包。

TR2 实测已通过：主 Agent 建单后结束并可继续接收聊天、单次按需查询、游戏内撤销、活动 Worker 停止、暂停时墙钟超时、切局失效和旧回报拒绝。建单前身份核对仍可能有模型和只读查询开销，详见 TICKETS.md。

TR3 已验证双工单串行注入、同时监视和同单位冲突拒绝。调度器必须确认前一个原生 Worker 已退出才派发下一个；取消请求未确认时继续占用执行槽。请确保部署包含 ticket_executor.py，并重启 Python 调度器加载此修复。Codex 执行后端的后续进展见 TICKETS.md 的 TR4；DSH 默认路径保留。

执行后端可显式选择 `--executor codex`，但必须先结束所有活跃工单并停止原调度器；默认不变。DSH 主会话仍负责建单，Codex Worker 可独立执行。此选项不代表 Codex 桌面聊天转发已经可用。

## 通用接口工单

当前开发版可用 command.kind=mcp_task 将现有 72 个业务接口交给子代理。主 Agent 只声明任务、范围和预算，受理后结束；Worker 使用 sp_api_find/schema/call/finish，不需查源码或手册。目录和 schema 从现有 MCP 工具表自动生成，管理接口不交给 Worker 递归调用。

需加载 revision=20261106-native-one-shot-v6-api 的插件，/health 显示 apiTicketProtocol=1。配置 runtimeDir 与可后台运行的 pythonPath，完整示例见 TICKETS.md。通用 Worker 的无关工具已过滤；截图返回本地 artifact，可按需直接读取图像。

通用 executed 仅表示接口调用有宿主回执，效果未独立验证；航速专用工单继续按实际速度 completed。调用有预算、资源锁与 call_id 去重；未知写结果不重放。DSH 绘图及 Codex 甲板/Watch/截图通用工单均已实测，默认运行后端保持 DSH。


### 游戏通联 persona

`<MOD_ROOT>/persona.md` 是可编辑的玩家回复风格提示。保留 Harness 的身份、人设和称谓，将游戏回复调整为自然、简短的海军通联；普通受令不说“工单、子代理、回执”等内部术语，不把已下令说成已确认战果。技术调试问题仍可解释实现细节。

DSH 每次转发玩家消息都重新读取此文件并隐藏注入，现有聊天下一句即可采用修改；游戏记录只显示玩家原文。首次安装需重启新版收发脚本，此后改 persona 文件无需重启。缺失或留空时沿用 Harness 默认表达，不阻断通信。构建部署首次复制默认文件，之后保留部署目录中的自定义版本。

当前模块布局：MCP/工单实现已移入 modules，根目录保留原启动入口；DSH 执行 Adapter 位于本目录，旧 Node 工单代码归入 legacy。队列、运行数据与 persona 位置不变，参见模组根 MODULES.md。
