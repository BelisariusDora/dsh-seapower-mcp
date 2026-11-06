# Sea Power MCP：部署与接入指南

发行版本：Alpha 1.1（v1.1.0-alpha）。仍处于 Alpha 阶段。

本 README 是部署入口。Agent 应先确定用户要“仅 MCP、DSH、Codex、双后端”中的哪种，再按对应步骤操作。安装包中的路径均相对于本 README 所在的模组根目录；无需先读其他文档才能启动。部署请求授权安装和接入，不等于授权作战测试；先用只读查询验收。

## 1. 路径、前置与接入模式

| 占位符 | 实际含义 |
|---|---|
| `<SEA_POWER_ROOT>` | 含 Sea Power.exe 的游戏目录；通过 Steam“管理 → 浏览本地文件”定位 |
| `<MOD_ROOT>` | `<SEA_POWER_ROOT>/Sea Power_Data/StreamingAssets/dsh-spmcp`，本 README 所在目录 |
| `<SOURCE_ROOT>` | 可选源码仓库，含 build.ps1；只安装发行 ZIP 不需要源码 |
| `<PYTHON_EXE>` | Python 3.11+ 的可执行文件绝对路径 |
| `<NODE_EXE>` | Node.js 24+ 的可执行文件绝对路径，仅 DSH 收发或 Node 兼容入口需要 |
| `<CODEX_EXE>` | 已安装、已登录的 Codex CLI 可执行文件；支持 app-server --stdio |
| `<DSH_PROFILE>` | 实际运行的 DSH Host 使用的 profile 目录，不能假定为某个固定用户名路径 |

先读现有配置和进程命令行，复用正确的安装。缺少的依赖才安装；登录、账号或系统权限步骤交给用户完成，不复制其他机器的凭据。本文所有尖括号必须替换为当前机器的实际绝对路径，不能照抄占位符运行。

| 模式 | 需运行的组件 |
|---|---|
| 仅外部 MCP 客户端操作游戏 | AnchorChain + Bridge；MCP 客户端自行启动 mcp_server.py，不需要工单调度器或游戏聊天转发器 |
| 游戏内 DSH 聊天与工单 | Bridge、一个共享调度器、DSH Host 内的本项目插件、DSH sender、DSH tailer |
| 游戏内 Codex 聊天与工单 | Bridge、一个共享调度器、Codex chat；不必安装 DSH 或 Node.js |
| 游戏内两种目标均可用 | 合并上两行，仍只有一个共享调度器；各通道只有一个 sender/tailer 或 chat |

外部 MCP 配置与游戏内聊天是两条接入路径。只配置 MCP 不会自动启动游戏内聊天；只启动聊天也不会替外部客户端填写 MCP 配置。

## 2. 安装/升级模组并确认 Bridge

1. 独立安装 [AnchorChain Preloader 与 Workshop 模组](https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html)，并在游戏模组配置中启用所需前置和本模组。本项目不打包 AnchorChain.dll，不要把前置组件复制进 dsh-spmcp。
2. 退出游戏。升级时将旧 dsh-spmcp 整体备份到 StreamingAssets 之外，再放入新版。保留用户的 setting.ini、persona.md 和自定义语言/头像资源，按需迁移；不要覆盖运行中的 DLL。
3. ZIP 应解出一个 dsh-spmcp 文件夹，直接放到 StreamingAssets；确认 README.md、_info.ini、SeaPowerAgentBridge.dll、mcp_server.py 与 modules 同级，不多套一层目录。
4. 安装包不含会话、日志或凭据。不要复制别人机器的 comms/run、auth.token、session.pointer 或账号配置。新安装可从空 comms 开始；升级已有运行目录前，先确认没有活动任务和对应执行进程，不能为排错直接删锁或账本后重跑。
5. 启动游戏，请用户载入测试场景。Agent 不自行退出正在玩的对局或重开场景。

```powershell
$GameRoot = '<SEA_POWER_ROOT>'
$ModRoot = Join-Path $GameRoot 'Sea Power_Data\StreamingAssets\dsh-spmcp'
$Python = '<PYTHON_EXE>'
$Node = '<NODE_EXE>'       # 仅 DSH/Node 路径需要
$Codex = '<CODEX_EXE>'     # 仅 Codex 路径需要
& $Python --version
$ping = Invoke-RestMethod 'http://127.0.0.1:19080/ping'
$ping.ok
$ping.data.build
```

要求 ok=true、build.stamp 与 build.onDiskStamp 一致、staleDll=false。还应 GET /comms/state 确认 worldKey 非空，才进行场景内验收。ping 能成功不等于已经载入战局，也不证明任一 Harness 已可用。19080 默认仅监听本机。

## 3. 仅 MCP 客户端：填写 stdio 配置

向客户端现有 MCP 配置中合并一个服务，不覆盖其他服务。支持 mcpServers 格式的客户端示例：

```json
{
  "mcpServers": {
    "seapower": {
      "command": "<PYTHON_EXE>",
      "args": ["<MOD_ROOT>/mcp_server.py"]
    }
  }
}
```

使用实际绝对路径；Windows JSON 中可以使用正斜线，或将反斜线写成双反斜线。服务走 JSON-RPC/stdio，由客户端持有进程，不要把它当独立 HTTP MCP 服务。MCP 客户端启动/重载后，用 tools/list 找当前工具，再调用 sp_ping。此模式到这里即可，不必继续安装 Harness 插件。

## 4. 游戏内工单：启动一个共享调度器

在 `<MOD_ROOT>` 运行下列常驻命令。若 Codex Worker 需要用到 CLI，即使 chat 入口传了 --codex，也必须确保调度器进程的 PATH 能找到 codex；Worker Adapter 自行从该进程环境定位 CLI。

```powershell
Set-Location $ModRoot
# 使用 Codex 时，在启动调度器之前设置；仅 DSH 时跳过。
$env:PATH = (Split-Path -Parent $Codex) + ';' + $env:PATH
Get-Command codex -ErrorAction SilentlyContinue
& $Python (Join-Path $ModRoot 'ticket_scheduler.py') --out (Join-Path $ModRoot 'comms') --executor dsh
```

仅 Codex 部署可以用 --executor codex。--executor 是没有明确后端来源时的默认值，不是全局互斥开关；游戏 Codex 主会话建单由宿主绑定 Codex，DSH 请求沿 DSH 路径。双后端不要各启动一个调度器。

这是常驻命令，不会执行完退出。下文的 sender、tailer、chat 也各是独立常驻进程，不能在同一阻塞终端依次等它们退出。Agent 后台启动时使用 Start-Process -WindowStyle Hidden，路径参数正确加引号，分别记录 PID、命令行、stdout/stderr；先检查同一脚本和同一 comms 是否已运行，不重复启动。新增终端须重新设置本文变量。不要停止其他项目的 Node、Python、DSH 或 Codex 进程。

调度器创建 comms/run/tickets/auth.token、runtime.lock 和 view.json。auth.token 是本项目本机插件的 Bearer 凭据，不是模型 API key，不写入提示词或对话；启动前不存在属于正常情况。

## 5. DSH：安装原生插件，再启动收发器

### 5.1 选择实际 profile 并安装文件

先确认 DSH 已安装、账号可用，找出当前 Host 实际使用的 profile（可检查 Host 启动参数或 DSH 自身配置）。必须改实际加载的 profile；不能根据桌面窗口名称猜配置目录。

新安装示例，把以下两个模块放在同一个 ESM 包内：

```powershell
$DshProfile = '<DSH_PROFILE>'
$PluginDir = Join-Path $DshProfile 'node_modules\seapower-ticket-worker'
New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null
Copy-Item (Join-Path $ModRoot 'harness\DSH\dsh_ticket_plugin.mjs') (Join-Path $PluginDir 'index.mjs')
Copy-Item (Join-Path $ModRoot 'harness\DSH\dsh_ticket_notifications.mjs') (Join-Path $PluginDir 'dsh_ticket_notifications.mjs')
$package = '{"name":"seapower-ticket-worker","version":"1.1.0-alpha","type":"module","main":"index.mjs"}'
[IO.File]::WriteAllText((Join-Path $PluginDir 'package.json'), $package, (New-Object Text.UTF8Encoding($false)))
```

在该 profile 实际使用的 Cordis 配置中合并一条插件记录。若使用 cordis.patch.yml 的 insert 形状，示例如下；不要用这段覆盖整个配置文件：

```yaml
- insert:
    - id: seapower-ticket-worker
      name: seapower-ticket-worker
      config:
        runtimeDir: '<MOD_ROOT>/comms/run'
        pythonPath: '<PYTHON_EXE>'
        bridgePort: 19080
        port: 19389
```

runtimeDir 必须是绝对路径，必须指向 comms/run。若已有记录指向 index-v6.mjs 等显式文件，应更新那个实际入口，或同步修改配置使其指向新的入口；只覆盖未被加载的 index.mjs 没有效果。不要重复注册同一插件。升级时保留已有配置，确保 notifications 模块与入口同目录。

插件需要 Host 提供原生 agents、tools、subagents 能力；Worker provider 必须支持独立上下文及工具过滤。其生命周期由插件持有，不依赖主 Agent 轮次继续运行。DSH 版本不具备这些能力时应报告不兼容，不能伪造 Worker 已启动或退化为无边界的 shell 代执行。

### 5.2 重载 Host 并检查实际版本

在没有活动游戏主轮次/Worker 时，重载插件或重启对应 DSH Host。只操作已核实归属的 Host，不按进程名结束所有 DSH。启动调度器后，本机读取 token 发 health 请求（只打印 health 结果，不打印 token）：

```powershell
$tokenPath = Join-Path $ModRoot 'comms\run\tickets\auth.token'
$headers = @{Authorization = 'Bearer ' + [IO.File]::ReadAllText($tokenPath).Trim()}
$health = Invoke-RestMethod 'http://127.0.0.1:19389/health' -Headers $headers
$health | Select-Object ok, revision, ticketProtocol, apiTicketProtocol, taskResultProtocol, notificationProtocol
Remove-Variable headers
```

当前包的插件 revision 从 harness/DSH/dsh_ticket_plugin.mjs 顶部导出值读取，并与 health 比较；不要照抄历史 TR 文档里的 revision。要求 ok=true、ticketProtocol=2、apiTicketProtocol=1、taskResultProtocol=1、notificationProtocol=1。health 证明插件加载，不证明模型授权、provider 和执行任务已全部通过，仍要做第 7 节只读验收。

### 5.3 启动 sender 与 tailer

DSH 本机 RPC 默认 19387，插件服务默认 19389，游戏 Bridge 默认 19080，三个用途不同。收发器使用当前用户本机 DSH 配置；非默认 DSH 根目录时在对应进程设置 DSH_HOME。不要把账号配置内容粘贴到游戏或文档。

```powershell
& $Node (Join-Path $ModRoot 'harness\DSH\harness_link.js') --out (Join-Path $ModRoot 'comms') --cwd $GameRoot --game-world
```

```powershell
& $Node (Join-Path $ModRoot 'harness\DSH\dsh_tailer.js') --out (Join-Path $ModRoot 'comms\run') --game-world
```

特别注意 --out：sender 是 comms；tailer 是 comms/run。两者传成同一路径会使指针、回报位置不一致，表现为游戏一直 queued/no-session 或看不到回复。

进入本局会创建专属 DSH 会话；不跟随当前桌面聊天。sender 读出站队列并请求 DSH，tailer 从绑定会话读取可见结果，写回游戏读取的 live.jsonl。主会话不读旧局记录，切局停止旧绑定。首次出现 no-session 可能只是等待场景或本局会话建立；检查 worldKey、session.pointer、link.status.json，不删除队列强行重发。

## 6. Codex：启动每局临时主会话

确认 `$Codex` 是可用 CLI，账号已在该机器完成正常登录；不依赖 Codex 桌面 UI 或当前前台聊天。仍使用第 4 节同一个调度器，并确保它的 PATH 可定位 codex。

```powershell
& $Python (Join-Path $ModRoot 'harness\Codex\codex_chat.py') --out (Join-Path $ModRoot 'comms') --game-world --codex $Codex
```

推荐直接 Python 入口。Node 兼容入口为 harness/Codex/codex_link.js，内部仍启动同一个 Python chat；不要同时运行两种入口。

chat 在本局第一条玩家输入时才创建 ephemeral app-server thread，同局复用，切局中断关闭；不保证它出现在桌面聊天列表，也不绑定已有桌面聊天。进程关闭后不恢复旧聊天。Codex app-server 和 Worker 由本项目持有，不能假设 Codex 原生子代理天然能脱离主轮次后台运行。

## 7. 验收：从可连接到真实只读闭环

1. GET /ping 检查实际加载构建；GET /comms/state 检查 worldKey 和通道状态。
2. 游戏内用菜单键打开控制台（默认 N，可在 Settings 修改），选择通联页和要验证的 Target。
3. 先发普通简短招呼，确认当前通道收到真实模型回复；queued 只表示进入本地队列，不能写成已送达。
4. 再发明确的只读任务，例如“只查询一次己方编成并报告数量，不下达攻击、移动、起飞、绘图或时间命令”。核对卡片和 API 账本，仅有该只读调用；不能以提示词发送成功代替模型/接口闭环。
5. 默认 Worker 模式应建单后主轮次结束、Worker 执行后退出；Settings 开启仅主 Agent 时仍建单，但同一主轮次执行 sp_main_api_call/finish，不创建 Worker。执行方式在建单时固定，设置仅影响新任务。
6. STOP 停止当前主轮次；Worker 模式下它不代替取消已派出的任务，指定任务用 DESTROY。仅主 Agent 模式会收束该轮活动任务。停止/销毁不能撤销已发出的游戏命令，未知写入禁止自动重发。
7. 最后报告组件版本、运行 PID/归属、选用模式、只读验证结果和未验证项；恢复临时测试设置，不清除用户记录。

通用 executed 只表示 API 有执行记录，不能宣称起飞、发射、命中或摧毁已确认。航速专用任务由调度器采集两次新鲜真值后才 completed；通用 Watch 自动结单尚未接通。模型普通回复和结果通知按玩家输入语言表达，UI 固定文字则跟随游戏/Settings 语言，两者不混淆。

## 8. 游戏接口：优先查当前代码与 schema

安装与接入 Agent 可以读文件定位实现，不需要维护一份与代码重复的完整接口手册。业务执行 Worker 则使用精简发现工具；它的受控权限不因部署 Agent 能读源码而放宽。

| 问题 | 读取位置（相对 MOD_ROOT） |
|---|---|
| MCP 工具名、参数、handler 和 HTTP 映射 | modules/mcp/mcp_server.py 的 TOOLS、TOOLS_BY_NAME 及 t_* handler；根 mcp_server.py 只是兼容引导 |
| 快速找接口与完整签名 | modules/api/api_catalog.py、api_tools.json；运行 sp_api_find、sp_api_schema |
| 工单资源、预算、调用账本与回执处理 | modules/api/ticket_api.py、modules/tickets/ticket_store.py、ticket_predicates.py |
| 主模式来源绑定及模式读取 | modules/tickets/ticket_main.py |
| 真值采样、生命周期、通知调度 | modules/tickets/ticket_scheduler.py、ticket_notifications.py |
| DSH 原生工具、插件、子任务和主轮次限制 | harness/DSH/dsh_ticket_plugin.mjs、dsh_ticket_notifications.mjs、ticket_executor_dsh.py |
| Codex 主会话与 Worker | harness/Codex/codex_chat.py、ticket_executor_codex.py |

不连接游戏也能查询工具目录和 schema，例如：

```powershell
'{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"sp_api_find","arguments":{"domain":"deck","query":"launch","limit":3}}}' | & $Python (Join-Path $ModRoot 'mcp_server.py')
'{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"sp_api_schema","arguments":{"names":["sp_launch_aircraft"]}}}' | & $Python (Join-Path $ModRoot 'mcp_server.py')
```

这些是单次 stdio 诊断，不是常驻进程的启动命令。目录只给候选，实际业务调用前应读精确 schema，不猜参数。直接 API 写入仍受玩家授权、HOLD 和当前世界约束；工单执行必须走相应的受控助手，不绕过账本用 sp_raw/shell 达成同一动作。

更底层 HTTP/Unity 实现在可选源码仓库 `<SOURCE_ROOT>/src/Api.cs`、Watch.cs、GameReflect.cs、CommsBridge.cs、TicketBridge.cs。发行目录不含 C# 源码，只有 DLL；需要深入调查时读取源码仓库，不能声称能从现有安装包直接读到它们。源码：[dsh-seapower-mcp](https://github.com/BelisariusDora/dsh-seapower-mcp)。

## 9. 端口、运行证据与排错

| 项目 | 默认位置/含义 |
|---|---|
| 19080 | 游戏 Bridge；由 Python 转译和调度器访问 |
| 19387 | DSH Host 的本机 RPC；sender 使用 |
| 19389 | DSH 插件控制服务；scheduler 和通知使用 |
| comms/outbox.jsonl | DSH 出站队列；不要手动回退游标重放未知消息 |
| comms/run/session.pointer、link.status.json、link.log | DSH 本局绑定与投递证据 |
| comms/run/live.jsonl、tailer.state.json | DSH 可见回传与读取状态 |
| comms/codex.outbox.jsonl、run/codex.chat.json | Codex 出站与当前聊天快照 |
| comms/run/tickets/view.json、journal.jsonl | 工单快照与持久状态；journal 是证据，不是待重复执行脚本 |
| comms/run/tickets/api_calls、notifications | 实际调用与问题通知记录；不要打包公开 |
| bridge.log、*.build、deploy_sha.txt | Bridge 诊断和磁盘构建身份 |

- ping 失败：先确认前置、模组启用、加载路径和游戏是否运行，查看 bridge.log，不先重装 Harness。
- staleDll=true：磁盘文件与已加载程序集不同；等用户退出游戏后重新加载，不重复覆盖 DLL 声称生效。
- DSH 一直 queued/no-session：核对场景、19387、实际 DSH_HOME/profile、sender/tailer 的不同 --out、绑定指针与进程日志；不改成“跟随最近聊天”。
- 工单一直 queued：检查单一 scheduler 心跳、所需后端健康、已登录 CLI、Worker provider 和资源冲突。auth.token 只用于本机 health 检查。
- Codex 主聊天成功但 Worker 失败：首先查调度器进程的 PATH；chat 的 --codex 不会自动传给调度器内的 Worker Adapter。
- DSH health 旧版：检查实际 Cordis 条目加载的是哪个入口，再在空闲时重载 Host。改一个未加载的同名文件不会更新插件。
- 修改提示词无效果：persona.md 每次输入重读；harness/*/prompts/onceopen.md 只在新会话首句隐藏注入，旧会话不重新注入。
- 中文乱码：Markdown/INI 用 UTF-8 明确读写；Windows PowerShell 5.1 读取无 BOM UTF-8 时指定 -Encoding UTF8，不能先按系统代码页读成乱码后再保存。

## 10. 配置、升级及后续文档

setting.ini 存玩家偏好；persona.md 控制自然通联表达；language_en/ui.ini 与 language_cn/ui.ini 为中英资源。部署只补语言缺失键，保留已有翻译。Settings 的 MainAgentOnly 默认关闭；普通 Worker 模式、主模式及通知轮次具有不同工具权限，问题通知始终只能文字回复。

从源码更新：源码需位于 `<SEA_POWER_ROOT>/tools/spmcp_v2`（现有 build.ps1 按此布局定位游戏程序集）。游戏退出后，在包含 build.ps1 的源码目录运行 `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Deploy`。编译需已安装的 AnchorChain.dll；非默认位置用 ANCHORCHAIN_DLL 指向前置的实际文件。构建只编译引用，不复制此前置 DLL。随后只重启实际改动且归属本项目的空闲进程；仅改文档不必重启游戏或 Harness。

源码打包使用 package_release.py，清单为 deployment/modules.json；不要直接压缩运行中的模组目录。全量四层设计与历史 TR 过程在源码 doc 下，历史标记不覆盖本 README 的当前启动方式。安装目录专题补充：harness/README.md、harness/DSH/README.md、harness/Codex/README.md、harness/DSH/TICKETS.md、harness/MAIN_AGENT_MODE.md、harness/LOCALIZATION.md、harness/SETTINGS.md。阅读它们用于深入排错，不是首次部署的前置要求。
