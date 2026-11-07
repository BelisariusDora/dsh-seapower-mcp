# DSH 接入：当前部署说明

首次部署直接读 `<MOD_ROOT>/README.md` 第 1、2、4、5、7 节；其中已包含完整安装、Cordis 配置、启动命令和验收，不再要求沿旧 TR 章节拼接步骤。

安装后运行 `harness/doctor.py --backend dsh`，启动组件并进入场景后再加 `--live`。完整命令与结果解释见 [Harness 部署检查](DOCTOR.md)。

## 进程分工

| 组件 | 用途 | 关键路径或端口 |
|---|---|---|
| DSH Host | 运行本局主 Agent、原生 Worker provider 与本项目插件 | RPC 默认 19387，插件 19389 |
| harness_link.js | 玩家出站与 STOP，建立本局专属会话 | --out `<MOD_ROOT>/comms`，--game-world |
| dsh_tailer.js | 读取绑定会话可见日志并回传游戏 | --out `<MOD_ROOT>/comms/run`，--game-world |
| ticket_scheduler.py | 单写者任务生命周期、派工、判据和通知 | --out `<MOD_ROOT>/comms`；全项目一个实例 |

插件配置 runtimeDir 也是 comms/run；pythonPath 是可后台执行的 Python 绝对路径。插件入口和 dsh_ticket_notifications.mjs 必须同目录。配置若显式指向 index-v6.mjs，更新该文件或同步改配置；不能只复制 index.mjs。

## 启动与核验

按根 README 初始化路径变量，在不同常驻进程运行：

```powershell
& $Python (Join-Path $ModRoot 'ticket_scheduler.py') --out (Join-Path $ModRoot 'comms') --executor dsh
```

```powershell
& $Node (Join-Path $ModRoot 'harness\DSH\harness_link.js') --out (Join-Path $ModRoot 'comms') --cwd $GameRoot --game-world
```

```powershell
& $Node (Join-Path $ModRoot 'harness\DSH\dsh_tailer.js') --out (Join-Path $ModRoot 'comms\run') --game-world
```

先安装并启用对应 profile 的插件，再在无活动任务时重载 Host；scheduler 生成 token 后带 Bearer 调用 /health。revision 与当前 dsh_ticket_plugin.mjs 导出值比较，不照抄历史版本号。协议要求 ticketProtocol=2、apiTicketProtocol=1、taskResultProtocol=1、notificationProtocol=1；最终用游戏内只读编成任务验证，不以 health 代替闭环。

默认 Worker 模式下主 Agent 建单后结束，原生 one-shot Worker 由插件独立持有，使用 sp_api_find/schema/call/finish。开启仅主 Agent 后仍建单，由本轮主会话使用 sp_main_api_call/finish，不派 Worker。通知轮次始终禁止工具，只向玩家说明结果。详见同目录 TICKETS.md。

首句 onceopen 仅在新会话注入，含回复语言跟随玩家输入的规则；persona 每次输入重读。界面语言由游戏/Settings 决定，与模型回复语言独立。切局不恢复旧聊天，不跟随桌面前台会话。

代码定位：本目录 dsh_ticket_plugin.mjs、dsh_ticket_notifications.mjs、ticket_executor_dsh.py；游戏接口看 `<MOD_ROOT>/modules/mcp/mcp_server.py`，不是根目录兼容 shim。部署 Agent 可读实现，受控业务 Worker 仍只使用精简工具。
# 游戏通信澄清与部署检查

游戏通信只能显示正文。需要澄清时，在正文一次性提出问题，可用 1/2/3 列候选，禁止调用 ask_user_question 或其他选择题、确认卡工具。此规则写在 persona.md 和 prompts/onceopen.md 中。

persona.md 在 --game-world 模式的每条玩家输入时重新读取。修改后会影响该模式已有会话的下一条输入。其他模式不会注入这些提示词。已停在卡片问题上的轮次，需要先在 DSH 界面回答或取消；不要假定后续文本一定能解除等待。一次性提示词只在受管会话首条输入时注入，新增它的规则不会自动补入已经发送过的首条消息。

文件部署与 Host 加载需要分别验收。发行包自带检查器，使用 Python 与模组根目录的绝对路径运行。自定义插件端口可用 --dsh-port 指定。

```powershell
& $Python (Join-Path $ModRoot 'harness/doctor.py') --backend dsh --live
```

插件 health 通过后，还需确认 sender 的启动参数带 --game-world，并在目标 DSH 会话实际列出 sp_* 工具、完成一次只读对话。health 本身不能证明目标会话绑定和工具注入正确。安装路径、profile 注册、重载及完整验收步骤见模组根目录 README.md 第 5 节。
