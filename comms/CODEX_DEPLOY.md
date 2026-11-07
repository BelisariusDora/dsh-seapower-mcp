# Codex 接入：当前部署说明

首次部署读 `<MOD_ROOT>/README.md` 第 1、2、4、6、7 节即可启动。此路径不要求 DSH 或 Node.js。

安装后运行 `harness/doctor.py --backend codex`，启动组件并进入场景后再加 `--live`。完整命令与结果解释见 [Harness 部署检查](DOCTOR.md)。

## 进程和 CLI 来源

- 主聊天：`harness/Codex/codex_chat.py`，独立持有 ephemeral app-server thread。
- 后台 Worker：`harness/Codex/ticket_executor_codex.py`，由共享调度器启动、持有和停止，不能假设 Codex 原生子代理自动脱离主轮次运行。
- 调度器：根 ticket_scheduler.py 兼容入口，实际实现位于 modules/tickets；两个通道共用一个实例。
- 主 chat 可通过 --codex 传 CLI 路径；Worker 从调度器的 PATH 定位 codex。只配置 chat 的 --codex 不足以配置 Worker。

按根 README 初始化变量。启动调度器前把 `$Codex` 所在目录加入该进程环境：

```powershell
$env:PATH = (Split-Path -Parent $Codex) + ';' + $env:PATH
Get-Command codex
& $Python (Join-Path $ModRoot 'ticket_scheduler.py') --out (Join-Path $ModRoot 'comms') --executor codex
```

另一个独立常驻进程：

```powershell
& $Python (Join-Path $ModRoot 'harness\Codex\codex_chat.py') --out (Join-Path $ModRoot 'comms') --game-world --codex $Codex
```

若已有共享调度器，不再启动第二个。--executor 只决定未明确选择后端的默认值；游戏 Codex 建单由宿主绑定 Codex。Node 兼容 codex_link.js 内部仍运行同一个 chat，不能与 Python 入口并开。

## 会话、权限和停止

第一条本局输入才新建临时 thread，同局复用、切局关闭、进程重启不恢复旧聊天。不读取桌面前台聊天，不保证临时 thread 出现在桌面会话列表。账号、模型和 CLI 登录由本机正常配置提供，本项目不提供账号代理服务。

默认 Worker 模式：主 Agent 建单确认后结束，后台 Worker 独立执行。仅主 Agent 模式：仍建单，由同一主轮次用 sp_main_api_call/finish 执行；不创建 Worker。都复用调用账本、HOLD、范围、预算和世界边界。

STOP 校验当前会话、轮次、世界和请求时效，中断后抑制该轮的部分/迟到回复。Worker 模式取消具体后台任务用 DESTROY；主模式 STOP 同时收束本轮活动任务。已提交的游戏命令不能撤回，未知写入不自动重放。

主会话只得到有限动态工具，不继承 shell、其他 MCP 或原生多 Agent 能力。异常通知轮次宿主拒绝全部工具，只允许文字报告，不自动续建任务。

## 证据与验收

读 comms/run/codex.chat.json 检查 link、generation、worldKey；读 comms/run/tickets/view.json 和 api_calls 核对执行，原始凭据与记录不公开。仅普通聊天成功不代表 Worker 能启动，必须增加一次有明确边界的编成只读任务。

首句 prompts/onceopen.md 隐藏注入，模型回复语言跟随玩家输入；persona.md 每句重读。旧桌面投递脚本位于 legacy，当前模式不用 --thread、--chat-title 或 codex.target.json。
